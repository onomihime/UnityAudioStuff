// Copyright (c) 2025 onomihime (github.com/onomihime)
// Originally from: github.com/onomihime/UnityCustomUI
// Licensed under the MIT License. See the LICENSE file in the repository root for full license text.
// This file may be used in commercial projects provided the above copyright notice and this permission notice appear in all copies.




using System.Collections;
using UnityEngine;

namespace Modules.Audio
{
    // Define states (moved outside the class for potential broader use, or keep inside if preferred)
    public enum AudioState
    {
        Stopped,
        Playing,
        Transitioning // Covers volume changes, fade-in, fade-out
    }

    // Renamed enum (kept for pending action logic)
    public enum PendingActionType
    {
        Play,
        Stop // Kept for potential future use, though only Play is stored now
    }

    // Renamed struct (kept for pending action logic)
    public struct PendingAction // Made public for potential external inspection if needed
    {
        public PendingActionType Type;
        public float PitchMultiplier;
        public float Interval;
        public float TargetVolume;
        public float StartTime;
    }

    /// <summary>
    /// An implementation of CustomAudioComponent that uses direct sample manipulation
    /// in OnAudioFilterRead for smooth, pop-free volume transitions (fades, volume changes).
    /// Manages playback state and handles interruptions gracefully.
    /// Adapted from CustomAudioType.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class SmoothAudio : CustomAudioComponent
    {
        #region Configuration Fields

        [Header("Audio Source")]
        [SerializeField] public AudioSource audioSource;

        [Header("Default Transition Intervals (Seconds)")]
        [SerializeField] private float quickTransitionIntervalSeconds = 0.02f; // Used when interrupting playback
        [SerializeField] private float maxIntervalDuration = 2.0f; // Max duration for any volume transition
        [SerializeField] private float defaultFadeInInterval = 0.05f; // Default interval for Play() fade-in (used if smoothness is 0 in PlaySmooth)
        [SerializeField] private float defaultFadeOutInterval = 0.05f; // Default interval for Stop() fade-out (used if smoothness is 0 in StopSmooth)
        [SerializeField] private float defaultVolumeChangeInterval = 0.1f; // Default interval for SetNormalisedVolume()

        [Header("Initial Settings")]
        [SerializeField] [Range(0f, 1f)] private float initialBaseVolume = 0.8f; // Initial target volume on Initialize

        #endregion

        #region Private State Fields

        // Volume Management
        private float targetBaseVolume; // The desired steady-state volume level
        private volatile float currentAppliedVolume = 0.0f; // The volume currently being applied sample-by-sample

        // Sample-based Volume Transition State (for OnAudioFilterRead)
        private volatile bool isChangingVolume = false;
        private float volumeChangeTarget = 0.0f;
        private float volumeChangeStart = 0.0f;
        private int volumeChangeDurationSamples = 0;
        private int volumeChangeSamplesElapsed = 0;

        // State Machine
        private AudioState currentState = AudioState.Stopped;
        private volatile bool stopAfterVolumeChange = false;
        private bool playAfterQuickStop = false;
        private PendingAction pendingPlayAction;

        // Failsafe Timer
        private float transitionTimer = 0f;

        // Pending Pitch
        private float? pendingPitchMultiplier = null;
        private bool applyPendingPitch = false;

        // Cached Values (Audio Thread Safe)
        private float cachedOutputSampleRate = 44100f;
        private float cachedClipLength = 0f;

        // Constants
        private const float VOLUME_THRESHOLD = 0.0001f; // Threshold for considering volume effectively zero or non-zero
        private const float PARAM_SMOOTHING_FACTOR = 0.002f; // Smaller value = slower smoothing for filter params

        // --- Filter States ---
        // Low-Pass Filter (LPF)
        private bool lowPassEnabled = false;
        private float targetLowPassCutoffNormalized = 1.0f; // Target value set by public methods (0.01 to 1.0)
        private float currentLowPassCutoffNormalized = 1.0f; // Smoothed value used in DSP
        private float lpf_lastOutputL = 0.0f; // Previous output sample for LPF (Left)
        private float lpf_lastOutputR = 0.0f; // Previous output sample for LPF (Right)

        // Reverb Filter
        private bool reverbEnabled = false;
        private float targetReverbAmount = 0.0f; // Target dry/wet mix (0-1)
        private float currentReverbAmount = 0.0f; // Smoothed dry/wet mix
        private float targetReverbDecay = 0.0f; // Target decay factor (0-1)
        private float targetReverbGain = 0.0f; // Target feedback gain derived from decay
        private float currentReverbGain = 0.0f; // Smoothed feedback gain

        private float[] reverbBufferL; // Reverb delay buffer (Left)
        private float[] reverbBufferR; // Reverb delay buffer (Right)
        private int reverbBufferSize = 0;
        private int reverbWriteIndex = 0;
        private int reverbReadIndex = 0;
        private int reverbDelaySamples = 0; // Fixed delay length for simplicity

        #endregion

        #region CustomAudioComponent Implementation

        public override bool IsPlaying
        {
            get => currentState == AudioState.Playing || (currentState == AudioState.Transitioning && volumeChangeTarget > VOLUME_THRESHOLD);
            set
            {
                if (value && !IsPlaying)
                {
                    Play();
                }
                else if (!value && IsPlaying)
                {
                    Stop();
                }
            }
        }

        /// <summary>
        /// Plays the audio immediately (minimal fade-in).
        /// </summary>
        public override void Play()
        {
            // Use a very small default fade-in to prevent potential clicks, but effectively immediate.
            // Use current targetBaseVolume if > 0, otherwise use initialBaseVolume.
            float targetVol = (targetBaseVolume > VOLUME_THRESHOLD) ? targetBaseVolume : initialBaseVolume;
            HandlePlayRequest(1f, quickTransitionIntervalSeconds, targetVol, 0f);
        }

        /// <summary>
        /// Plays the audio with a smooth transition.
        /// </summary>
        /// <param name="smoothness">The fade-in duration in seconds.</param>
        public override void PlaySmooth(float smoothness)
        {
            float interval = Mathf.Clamp(smoothness, 0f, maxIntervalDuration);
            // If interval is effectively zero, use the quick transition interval.
            if (interval <= VOLUME_THRESHOLD) interval = quickTransitionIntervalSeconds;
            float targetVol = (targetBaseVolume > VOLUME_THRESHOLD) ? targetBaseVolume : initialBaseVolume;
            HandlePlayRequest(1f, interval, targetVol, 0f);
        }

        /// <summary>
        /// Pauses the currently playing audio by stopping it immediately.
        /// Note: This implementation stops playback, it doesn't truly pause and resume.
        /// </summary>
        public override void Pause()
        {
            Debug.Log($"Pause called. Stopping immediately.");
            ExecuteStopInternal(0f);
        }

        /// <summary>
        /// Stops the currently playing audio immediately.
        /// </summary>
        public override void Stop()
        {
            HandleStopRequest(0f);
        }

        /// <summary>
        /// Stops the currently playing audio with a smooth transition.
        /// </summary>
        /// <param name="smoothness">The fade-out duration in seconds.</param>
        public override void StopSmooth(float smoothness)
        {
            float interval = Mathf.Clamp(smoothness, 0f, maxIntervalDuration);
            // If interval is effectively zero, stop immediately.
            if (interval <= VOLUME_THRESHOLD) interval = 0f;
            HandleStopRequest(interval);
        }

        /// <summary>
        /// Sets the pitch factor for the audio.
        /// </summary>
        /// <param name="pitchFactor">The factor to multiply the base pitch by.</param>
        public override void SetPitchFactor(float pitchFactor)
        {
            SetPitchInternal(pitchFactor, true); // Apply instantly
        }

        /// <summary>
        /// Sets the normalised volume (between 0 and 1) with a default smooth transition.
        /// </summary>
        /// <param name="volume">Volume value between 0 (silent) and 1 (full volume).</param>
        public override void SetNormalisedVolume(float volume)
        {
            SetVolumeInternal(volume, defaultVolumeChangeInterval); // Use default transition interval
        }

        /// <summary>
        /// Sets the base frequency for the audio in Hz.
        /// NOTE: This implementation assumes 'frequency' acts as a pitch multiplier, similar to SetPitchFactor.
        /// A more accurate implementation would require knowing the original clip's base frequency.
        /// </summary>
        /// <param name="frequency">The pitch multiplier.</param>
        public override void SetBaseFrequency(float frequency)
        {
            Debug.LogWarning("SetBaseFrequency is interpreting the frequency value as a pitch multiplier.");
            SetPitchInternal(frequency, true); // Apply instantly
        }

        /// <summary>
        /// Enables or disables the low-pass filter.
        /// </summary>
        /// <param name="enabled">Whether the filter is enabled.</param>
        public override void SetLowPassEnabled(bool enabled)
        {
            lowPassEnabled = enabled;
            // Optional: Instantly reset filter state if disabling?
            // if (!enabled) {
            //     lpf_lastOutputL = 0f;
            //     lpf_lastOutputR = 0f;
            //     currentLowPassCutoffNormalized = 1.0f; // Or target value?
            // }
        }

        /// <summary>
        /// Sets the cutoff frequency for the low-pass filter.
        /// </summary>
        /// <param name="cutoff">Normalized cutoff frequency (0.01 to 1.0).</param>
        public override void SetLowPassCutoff(float cutoff)
        {
            targetLowPassCutoffNormalized = Mathf.Clamp(cutoff, 0.01f, 1.0f);
        }

        /// <summary>
        /// Enables or disables the reverb effect.
        /// </summary>
        /// <param name="enabled">Whether reverb is enabled.</param>
        public override void SetReverbEnabled(bool enabled)
        {
            reverbEnabled = enabled;
            // Optional: Instantly clear buffer or reset gain if disabling?
            if (!enabled && reverbBufferL != null)
            {
                 // System.Array.Clear(reverbBufferL, 0, reverbBufferSize);
                 // System.Array.Clear(reverbBufferR, 0, reverbBufferSize);
                 // currentReverbAmount = 0f; // Or target value?
                 // currentReverbGain = 0f;
            }
        }

        /// <summary>
        /// Sets the reverb amount (dry/wet mix).
        /// </summary>
        /// <param name="amount">Amount of reverb (0.0 - 1.0).</param>
        public override void SetReverbAmount(float amount)
        {
            targetReverbAmount = Mathf.Clamp01(amount);
        }

        /// <summary>
        /// Sets the reverb decay time factor.
        /// </summary>
        /// <param name="decay">Decay factor (0.0 - 1.0), higher values = longer decay.</param>
        public override void SetReverbDecay(float decay)
        {
            targetReverbDecay = Mathf.Clamp01(decay);
            // Simple mapping from decay factor to feedback gain.
            // Adjust the 0.99f multiplier to control max decay length relative to buffer size.
            targetReverbGain = targetReverbDecay * 0.99f;
        }

        #endregion

        #region Initialization

        void Awake()
        {
            cachedOutputSampleRate = AudioSettings.outputSampleRate;

            if (audioSource == null) audioSource = GetComponent<AudioSource>();
            if (audioSource == null)
            {
                Debug.LogError("SmoothAudio requires an AudioSource component.", this);
                this.enabled = false;
                return;
            }

            Initialize(); // Initialize base state

            // Initialize Filter States
            InitializeFilters(); // Call filter specific init

            if (audioSource.clip != null)
            {
                cachedClipLength = audioSource.clip.length;
            }
            else
            {
                 Debug.LogWarning("AudioSource or AudioClip not assigned in Awake, cannot cache length.", this.gameObject);
            }
        }

        private void Initialize()
        {
            targetBaseVolume = initialBaseVolume;
            currentAppliedVolume = 0f;
            currentState = AudioState.Stopped;

            audioSource.volume = 1.0f; // Master volume is always 1.0, control happens in OnAudioFilterRead
            audioSource.loop = false;
            audioSource.playOnAwake = false;

            isChangingVolume = false;
            stopAfterVolumeChange = false;
            playAfterQuickStop = false;
            applyPendingPitch = false;
            pendingPitchMultiplier = null;

            Debug.Log($"SmoothAudio Initialized. Initial Target Volume: {targetBaseVolume}");

            if (cachedClipLength == 0f && audioSource != null && audioSource.clip != null)
            {
                 cachedClipLength = audioSource.clip.length;
            }
        }

        /// <summary>
        /// Initializes filter-related states and buffers.
        /// </summary>
        private void InitializeFilters()
        {
            // LPF Defaults
            lowPassEnabled = false;
            targetLowPassCutoffNormalized = 1.0f;
            currentLowPassCutoffNormalized = 1.0f;
            lpf_lastOutputL = 0.0f;
            lpf_lastOutputR = 0.0f;

            // Reverb Defaults & Buffer Allocation
            reverbEnabled = false;
            targetReverbAmount = 0.0f;
            currentReverbAmount = 0.0f;
            targetReverbDecay = 0.0f;
            targetReverbGain = 0.0f;
            currentReverbGain = 0.0f;

            // Allocate reverb buffer (e.g., 0.5 seconds max delay)
            reverbBufferSize = Mathf.CeilToInt(0.5f * cachedOutputSampleRate);
            if (reverbBufferSize <= 0) reverbBufferSize = 22050; // Fallback size
            reverbBufferL = new float[reverbBufferSize];
            reverbBufferR = new float[reverbBufferSize];

            // Set a fixed delay length (e.g., 300ms) - must be less than buffer size
            reverbDelaySamples = Mathf.Clamp(Mathf.CeilToInt(0.3f * cachedOutputSampleRate), 1, reverbBufferSize - 1);

            reverbWriteIndex = 0;
            // Calculate initial read index based on write index and delay
            reverbReadIndex = (reverbWriteIndex - reverbDelaySamples + reverbBufferSize) % reverbBufferSize;

            Debug.Log($"Filters Initialized. Reverb Buffer Size: {reverbBufferSize}, Delay Samples: {reverbDelaySamples}");
        }


        #endregion

        #region State Management (Update Loop)

        void Update()
        {
            // --- State Machine Logic ---
            if (!isChangingVolume && currentState == AudioState.Transitioning)
            {
                transitionTimer = 0f;
                HandleTransitionEnd();
            }
            else if (currentState == AudioState.Playing && !audioSource.isPlaying)
            {
                transitionTimer = 0f;
                HandleNaturalStop();
            }
            else if (currentState == AudioState.Transitioning)
            {
                transitionTimer += Time.deltaTime;
            }
            else
            {
                transitionTimer = 0f;
            }

            // --- Failsafe Checks ---
            if (currentState == AudioState.Transitioning && transitionTimer > maxIntervalDuration)
            {
                Debug.LogWarning($"Failsafe: Transition timed out (> {maxIntervalDuration}s). Forcing stop.");
                ForceStopAndResetState();
                transitionTimer = 0f;
            }

            if (currentState != AudioState.Transitioning)
            {
                if (currentState == AudioState.Playing && !audioSource.isPlaying)
                {
                    Debug.LogWarning($"Failsafe: State is Playing but AudioSource is not. Forcing Stopped state.");
                    HandleNaturalStop();
                }
                else if (currentState == AudioState.Stopped && audioSource.isPlaying)
                {
                    Debug.LogWarning($"Failsafe: State is Stopped but AudioSource is playing. Forcing immediate stop.");
                    ExecuteStopInternal(0f);
                }

                if (playAfterQuickStop)
                {
                    Debug.LogWarning($"Failsafe: playAfterQuickStop is true but not Transitioning. Resetting flag and forcing stop.");
                    playAfterQuickStop = false;
                    ForceStopAndResetState();
                }

                if (stopAfterVolumeChange)
                {
                    Debug.LogWarning($"Failsafe: stopAfterVolumeChange is true but not Transitioning. Resetting flag and ensuring stop.");
                    stopAfterVolumeChange = false;
                    if (audioSource.isPlaying) audioSource.Stop();
                    currentState = AudioState.Stopped;
                    targetBaseVolume = 0f;
                    currentAppliedVolume = 0f;
                }
            }
        }

        private void HandleTransitionEnd()
        {
            Debug.Log($"Update: Transition finished. CurrentAppliedVolume: {currentAppliedVolume:F3}");

            if (playAfterQuickStop && currentAppliedVolume <= VOLUME_THRESHOLD)
            {
                Debug.Log("Update: Executing pending Play action after quick stop.");
                if (stopAfterVolumeChange)
                {
                    audioSource.Stop();
                    stopAfterVolumeChange = false;
                }
                playAfterQuickStop = false;
                ExecutePlayInternal(pendingPlayAction.PitchMultiplier, pendingPlayAction.Interval, pendingPlayAction.TargetVolume, pendingPlayAction.StartTime);
                return;
            }
            playAfterQuickStop = false; // Reset flag if transition ended but volume wasn't zero

            AudioState nextState = (currentAppliedVolume > VOLUME_THRESHOLD) ? AudioState.Playing : AudioState.Stopped;
            currentState = nextState;
            Debug.Log($"Update: State changed to {currentState} (no pending actions)");

            if (currentState == AudioState.Playing && applyPendingPitch && pendingPitchMultiplier.HasValue)
            {
                Debug.Log($"Update: Applying pending pitch: {pendingPitchMultiplier.Value}");
                SetPitchInternal(pendingPitchMultiplier.Value, instant: true);
                applyPendingPitch = false;
                pendingPitchMultiplier = null;
            }

            if (stopAfterVolumeChange)
            {
                Debug.Log("Update: Finalizing stop after volume transition.");
                audioSource.Stop();
                stopAfterVolumeChange = false;
                currentState = AudioState.Stopped;
                targetBaseVolume = 0f;
            }
            else if (currentState == AudioState.Stopped)
            {
                targetBaseVolume = 0f;
            }
        }

        private void HandleNaturalStop()
        {
            Debug.Log("Update: Audio stopped naturally (end of clip).");
            currentState = AudioState.Stopped;
            isChangingVolume = false;
            currentAppliedVolume = 0f;
            targetBaseVolume = 0f;
            stopAfterVolumeChange = false;
            playAfterQuickStop = false;
            // No queue to clear
        }

        private void ForceStopAndResetState()
        {
            Debug.LogWarning("Executing ForceStopAndResetState.");
            if (audioSource.isPlaying) audioSource.Stop();

            isChangingVolume = false;
            stopAfterVolumeChange = false;
            playAfterQuickStop = false;
            applyPendingPitch = false;
            pendingPitchMultiplier = null;
            currentAppliedVolume = 0f;
            targetBaseVolume = 0f;
            volumeChangeSamplesElapsed = 0;
            currentState = AudioState.Stopped;
        }

        #endregion

        #region Internal Action Execution & Helpers

        /// <summary>
        /// Handles the logic for initiating playback, including interruptions.
        /// </summary>
        private void HandlePlayRequest(float pitchMultiplier, float fadeInInterval, float targetVolume, float startTime)
        {
            float finalTargetVolume = Mathf.Max(VOLUME_THRESHOLD, Mathf.Clamp01(targetVolume)); // Ensure target is not effectively zero
            Debug.Log($"Play requested. StartTime: {startTime}s, Interval: {fadeInInterval}s, Pitch: {pitchMultiplier}, TargetVol: {finalTargetVolume}. CurrentState: {currentState}");

            if (currentState == AudioState.Transitioning || currentState == AudioState.Playing)
            {
                Debug.Log($"Play: Interrupting current state ({currentState}) with quick stop.");
                pendingPlayAction = new PendingAction
                {
                    Type = PendingActionType.Play,
                    PitchMultiplier = pitchMultiplier,
                    Interval = fadeInInterval,
                    TargetVolume = finalTargetVolume,
                    StartTime = startTime
                };
                playAfterQuickStop = true;
                InitiateQuickStopInternal();
                currentState = AudioState.Transitioning; // State is now Transitioning (to stop)
            }
            else // currentState == AudioState.Stopped
            {
                ExecutePlayInternal(pitchMultiplier, fadeInInterval, finalTargetVolume, startTime);
            }
        }

        /// <summary>
        /// Handles the logic for initiating stop, including interruptions.
        /// </summary>
        private void HandleStopRequest(float fadeOutInterval)
        {
             Debug.Log($"Stop requested. Interval: {fadeOutInterval}s. CurrentState: {currentState}");

            if (currentState == AudioState.Transitioning)
            {
                Debug.Log("Stop: Interrupting current transition to start new fade-out.");
                playAfterQuickStop = false; // Cancel any pending play after quick stop
                UpdateVolumeInternal(0f, fadeOutInterval); // Start transition to 0 from current volume
                stopAfterVolumeChange = true; // Ensure it stops after fade
                currentState = AudioState.Transitioning; // Remain/Ensure Transitioning state
            }
            else if (currentState == AudioState.Playing)
            {
                ExecuteStopInternal(fadeOutInterval);
            }
            // If already Stopped, do nothing.
        }


        /// <summary>
        /// Internal execution logic for Play. Assumes it can run now.
        /// </summary>
        private void ExecutePlayInternal(float pitchMultiplier, float fadeInInterval, float targetVolume, float startTime)
        {
            Debug.Log($"ExecutePlayInternal: StartTime: {startTime}s, Interval: {fadeInInterval}s, Pitch: {pitchMultiplier}, TargetVol: {targetVolume}");

            if (!audioSource.clip)
            {
                Debug.LogWarning("ExecutePlayInternal: No AudioClip assigned.", this);
                currentState = AudioState.Stopped;
                return;
            }

            SetPitchInternal(pitchMultiplier, instant: true); // Apply pitch immediately
            applyPendingPitch = false;
            pendingPitchMultiplier = null;

            audioSource.Stop(); // Ensure clean start

            audioSource.time = Mathf.Clamp(startTime, 0f, cachedClipLength > 0 ? cachedClipLength : audioSource.clip.length); // Use cached length if available
            this.targetBaseVolume = targetVolume; // Update steady-state target

            if (fadeInInterval > VOLUME_THRESHOLD)
            {
                currentAppliedVolume = 0f; // Start fade from silence
                UpdateVolumeInternal(targetVolume, fadeInInterval);
                currentState = AudioState.Transitioning;
            }
            else
            {
                isChangingVolume = false;
                currentAppliedVolume = targetVolume;
                volumeChangeTarget = targetVolume;
                volumeChangeStart = targetVolume;
                volumeChangeSamplesElapsed = 0;
                currentState = AudioState.Playing;
            }

            stopAfterVolumeChange = false;
            audioSource.Play();
        }

        /// <summary>
        /// Internal execution logic for Stop. Assumes it can run now (Playing state).
        /// </summary>
        private void ExecuteStopInternal(float fadeOutInterval)
        {
            Debug.Log($"ExecuteStopInternal: Interval: {fadeOutInterval}s");

            // This check might be redundant if called correctly, but good for safety.
            if (currentState != AudioState.Playing && currentState != AudioState.Transitioning)
            {
                 Debug.LogWarning($"ExecuteStopInternal called unexpectedly from state {currentState}. Current volume: {currentAppliedVolume}. Forcing stop state.");
                 // Force stop state even if not playing, ensure clean state
                 ForceStopAndResetState();
                 return;
            }


            if (fadeOutInterval <= VOLUME_THRESHOLD) // Immediate Stop
            {
                audioSource.Stop();
                isChangingVolume = false;
                stopAfterVolumeChange = false;
                playAfterQuickStop = false;
                currentAppliedVolume = 0f;
                targetBaseVolume = 0f;
                volumeChangeSamplesElapsed = 0;
                volumeChangeStart = 0f;
                volumeChangeTarget = 0f;
                currentState = AudioState.Stopped;
            }
            else // Smooth Stop (Fade Out)
            {
                targetBaseVolume = 0f; // Component's steady-state target is 0
                UpdateVolumeInternal(0f, fadeOutInterval); // Start transition to 0
                stopAfterVolumeChange = true; // Signal Update to stop AudioSource when volume reaches 0
                currentState = AudioState.Transitioning;
            }
        }

        /// <summary>
        /// Internal logic for setting volume smoothly.
        /// </summary>
        private void SetVolumeInternal(float volume, float transitionInterval)
        {
            float newTarget = Mathf.Clamp01(volume);
            float actualInterval = Mathf.Clamp(transitionInterval, 0f, maxIntervalDuration);

            Debug.Log($"SetVolumeInternal called: Target {newTarget:F3}, Interval: {actualInterval}s. CurrentState: {currentState}");

            targetBaseVolume = newTarget; // Update the component's steady-state target volume
            stopAfterVolumeChange = false; // SetVolume overrides any pending stop
            playAfterQuickStop = false; // SetVolume overrides any pending play

            if (actualInterval <= VOLUME_THRESHOLD) // Apply immediately
            {
                isChangingVolume = false;
                currentAppliedVolume = newTarget;
                volumeChangeTarget = newTarget;
                volumeChangeStart = newTarget;
                volumeChangeSamplesElapsed = 0;

                bool sourceShouldBePlaying = newTarget > VOLUME_THRESHOLD && audioSource.isPlaying;
                currentState = sourceShouldBePlaying ? AudioState.Playing : AudioState.Stopped;

                if (currentState == AudioState.Stopped && audioSource.isPlaying)
                {
                    audioSource.Stop();
                }
            }
            else // Apply smoothly
            {
                UpdateVolumeInternal(newTarget, actualInterval);
                if (currentState != AudioState.Stopped || audioSource.isPlaying)
                {
                    currentState = AudioState.Transitioning;
                }
            }
        }

        /// <summary>
        /// Internal logic for setting pitch.
        /// </summary>
        private void SetPitchInternal(float pitchMultiplier, bool instant = true)
        {
            if (instant || currentState == AudioState.Playing)
            {
                Debug.Log($"SetPitchInternal: Applying pitch {pitchMultiplier} instantly.");
                audioSource.pitch = Mathf.Clamp(pitchMultiplier, 0.1f, 4.0f);
                applyPendingPitch = false;
                pendingPitchMultiplier = null;
            }
            else
            {
                Debug.Log($"SetPitchInternal: Deferring pitch {pitchMultiplier} until Playing state.");
                pendingPitchMultiplier = pitchMultiplier;
                applyPendingPitch = true;
            }
        }


        /// <summary>
        /// Internal helper to set up volume transition state variables for OnAudioFilterRead.
        /// </summary>
        private void UpdateVolumeInternal(float newTargetVolume, float transitionInterval)
        {
            transitionInterval = Mathf.Clamp(transitionInterval, 0f, maxIntervalDuration);
            volumeChangeStart = currentAppliedVolume; // Start from the exact current sample volume
            volumeChangeTarget = newTargetVolume;
            volumeChangeDurationSamples = Mathf.Max(1, Mathf.CeilToInt(transitionInterval * cachedOutputSampleRate));
            volumeChangeSamplesElapsed = 0;
            isChangingVolume = true;
        }

        /// <summary>
        /// Internal helper to initiate a quick fade-out to zero.
        /// </summary>
        private void InitiateQuickStopInternal()
        {
            Debug.Log($"Initiating Quick Stop from volume {currentAppliedVolume:F3} over {quickTransitionIntervalSeconds}s.");
            targetBaseVolume = 0f; // Target is zero after this stop
            UpdateVolumeInternal(0f, quickTransitionIntervalSeconds);
            stopAfterVolumeChange = true; // Ensure source stops when volume reaches 0
            // The calling method (HandlePlayRequest) sets currentState = AudioState.Transitioning
        }

        #endregion

        #region Audio Processing (OnAudioFilterRead)

        private void OnAudioFilterRead(float[] data, int channels)
        {
            int samplesInThisBuffer = data.Length / channels;
            if (samplesInThisBuffer == 0) return;

            float sampleVolume = currentAppliedVolume; // Start with current stable/last volume

            // --- Parameter Smoothing (per buffer, could be per sample for max smoothness) ---
            // Smooth LPF Cutoff
            currentLowPassCutoffNormalized = Mathf.Lerp(currentLowPassCutoffNormalized, targetLowPassCutoffNormalized, PARAM_SMOOTHING_FACTOR * samplesInThisBuffer);
            // Smooth Reverb Params
            currentReverbAmount = Mathf.Lerp(currentReverbAmount, targetReverbAmount, PARAM_SMOOTHING_FACTOR * samplesInThisBuffer);
            currentReverbGain = Mathf.Lerp(currentReverbGain, targetReverbGain, PARAM_SMOOTHING_FACTOR * samplesInThisBuffer);

            // --- Calculate LPF coefficient (alpha) based on smoothed cutoff ---
            // Simple non-linear mapping: lower cutoff = smaller alpha = more filtering
            // More accurate formulas exist, but this is simple for normalized input.
            float lpf_alpha = currentLowPassCutoffNormalized * currentLowPassCutoffNormalized; // Square for faster drop-off
            lpf_alpha = Mathf.Clamp01(lpf_alpha); // Ensure alpha is valid

            // --- Process each sample frame in the buffer ---
            for (int i = 0; i < samplesInThisBuffer; i++)
            {
                // --- 1. Calculate Volume for this sample frame ---
                if (isChangingVolume)
                {
                    int duration = Mathf.Max(1, volumeChangeDurationSamples);
                    float t = Mathf.Clamp01((float)volumeChangeSamplesElapsed / duration);
                    float smoothT = t * t * (3f - 2f * t); // Smoothstep
                    sampleVolume = Mathf.Lerp(volumeChangeStart, volumeChangeTarget, smoothT);

                    volumeChangeSamplesElapsed++;

                    if (volumeChangeSamplesElapsed >= volumeChangeDurationSamples)
                    {
                        sampleVolume = volumeChangeTarget;
                        currentAppliedVolume = volumeChangeTarget;
                        isChangingVolume = false;
                        volumeChangeSamplesElapsed = 0;
                    }
                }
                else
                {
                    sampleVolume = currentAppliedVolume; // Use stable volume
                }

                // --- 2. Process Filters (before volume scaling) ---
                float inputL = 0f, inputR = 0f;
                float outputL = 0f, outputR = 0f;

                // Get input samples for this frame
                inputL = data[i * channels + 0];
                if (channels > 1) inputR = data[i * channels + 1]; else inputR = inputL; // Mono fallback

                outputL = inputL; // Start with dry signal
                outputR = inputR;

                // --- Apply Low-Pass Filter ---
                if (lowPassEnabled)
                {
                    // Simple one-pole IIR filter: y[n] = alpha * x[n] + (1 - alpha) * y[n-1]
                    outputL = lpf_alpha * outputL + (1.0f - lpf_alpha) * lpf_lastOutputL;
                    outputR = lpf_alpha * outputR + (1.0f - lpf_alpha) * lpf_lastOutputR;

                    // Store output for next sample's calculation
                    lpf_lastOutputL = outputL;
                    lpf_lastOutputR = outputR;
                }

                // --- Apply Reverb Filter ---
                if (reverbEnabled && reverbBufferSize > 0)
                {
                    // Read delayed samples
                    float delayedL = reverbBufferL[reverbReadIndex];
                    float delayedR = reverbBufferR[reverbReadIndex];

                    // Calculate wet signal (feedback comb filter output)
                    float wetL = delayedL * currentReverbGain;
                    float wetR = delayedR * currentReverbGain;

                    // Write input + wet signal back into buffer for feedback
                    // Use the LPF'd signal (outputL/R) as the input to the reverb
                    reverbBufferL[reverbWriteIndex] = Mathf.Clamp(outputL + wetL, -1.0f, 1.0f);
                    reverbBufferR[reverbWriteIndex] = Mathf.Clamp(outputR + wetR, -1.0f, 1.0f);

                    // Mix dry (outputL/R from LPF) and wet signals based on amount
                    outputL = outputL * (1.0f - currentReverbAmount) + wetL * currentReverbAmount;
                    outputR = outputR * (1.0f - currentReverbAmount) + wetR * currentReverbAmount;

                    // Increment and wrap buffer indices
                    reverbReadIndex = (reverbReadIndex + 1) % reverbBufferSize;
                    reverbWriteIndex = (reverbWriteIndex + 1) % reverbBufferSize;
                }


                // --- 3. Apply Volume Scaling (to the filtered output) ---
                if (sampleVolume != 1.0f) // Optimization: only multiply if needed
                {
                    outputL *= sampleVolume;
                    outputR *= sampleVolume;
                }

                // --- 4. Write final output back to data buffer ---
                // Handle potential NaN/Inf values
                data[i * channels + 0] = (float.IsNaN(outputL) || float.IsInfinity(outputL)) ? 0f : outputL;
                if (channels > 1)
                {
                    data[i * channels + 1] = (float.IsNaN(outputR) || float.IsInfinity(outputR)) ? 0f : outputR;
                }
            }

            // Update currentAppliedVolume for the next buffer if volume transition is ongoing
            if (isChangingVolume)
            {
                currentAppliedVolume = sampleVolume;
            }
        }

        #endregion
    }
}
