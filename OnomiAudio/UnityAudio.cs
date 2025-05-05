
// Copyright (c) 2025 onomihime (github.com/onomihime)
// Originally from: github.com/onomihime/UnityCustomUI
// Licensed under the MIT License. See the LICENSE file in the repository root for full license text.
// This file may be used in commercial projects provided the above copyright notice and this permission notice appear in all copies.

using System;
using UnityEngine;
using UnityEngine.Audio;
using System.Threading; // Keep for potential future use, but fade logic won't use Interlocked now
using System.Collections; // Needed for potential coroutines like smooth volume change

namespace Modules.Audio
{
    [RequireComponent(typeof(AudioSource))]
    [RequireComponent(typeof(AudioLowPassFilter))]
    [RequireComponent(typeof(AudioReverbFilter))]
    [AddComponentMenu("Modules/Audio/UnityAudio")]
    public class UnityAudio : CustomAudioComponent
    {
        [Header("Components")]
        [SerializeField] private AudioSource source;
        [SerializeField] private AudioLowPassFilter lowPassFilter;
        [SerializeField] private AudioReverbFilter reverbFilter;

        [Header("Fade Settings")]
        // [SerializeField] private int fadeInSamples = 512; // Now calculated dynamically
        // [SerializeField] private int fadeOutSamples = 512; // Now calculated dynamically
        [SerializeField] private int quickTransitionFadeOutSamples = 256; // Keep for crossfades
        [SerializeField] private float maxSmoothnessDuration = 2.0f; // Max duration in seconds for fades

        // Dynamically calculated fade samples
        private int currentFadeInSamples = 512;
        private int currentFadeOutSamples = 512;
        private int maxTransitionSamples; // Calculated in Awake


        [Header("Filter Settings")]
        [SerializeField] private float filterTransitionSpeed = 15f; // Adjust for faster/slower transitions

        // --- State Flags ---
        private bool _isPlaying = false;
        /// <summary>
        /// Gets a value indicating whether the audio is currently playing or transitioning to play.
        /// </summary>
        public override bool IsPlaying { get => _isPlaying; set => _isPlaying = value; }

        // Flags managed by main thread and audio thread communication
        private volatile bool isFadingOut = false;
        private volatile bool isFadingIn = false;
        private volatile bool playAfterFadeOut = false; // True if fade out is for restarting playback
        private volatile bool readyToStop = false; // Set by audio thread when fade-out completes

        // --- Volume & Pitch ---
        private float baseVolume = 1.0f;
        private float currentFadeLevel = 1.0f; // Tracks volume during sample-level fades (0 to 1)
        private float basePitch = 1.0f; // Base pitch factor (e.g., based on note frequency)
        private float currentPitchMultiplier = 1.0f; // Additional multiplier (e.g., for pitch bend)

        // Pending pitch change
        private float nextPitchMultiplier = 1f;
        private bool hasPendingPitch = false;

        // Low-pass filter state
        private bool enableLowPass = false;
        private float targetLowPassCutoff = 22000f;
        private bool isTransitioningFilter = false;

        // Coroutine reference for potential smooth base volume changes (optional feature)
        private Coroutine volumeChangeCoroutine = null;


        /// <summary>
        /// Called when the script instance is being loaded.
        /// </summary>
        private void Awake()
        {
            // Initialize components
            source = GetComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false; // Default to non-looping, can be overridden if needed

            // Get or add filter components
            lowPassFilter = GetComponent<AudioLowPassFilter>();
            if (lowPassFilter == null) lowPassFilter = gameObject.AddComponent<AudioLowPassFilter>();
            lowPassFilter.enabled = false; // Initially disabled, controlled by SetLowPassEnabled

            reverbFilter = GetComponent<AudioReverbFilter>();
            if (reverbFilter == null) reverbFilter = gameObject.AddComponent<AudioReverbFilter>();
            reverbFilter.enabled = false; // Initially disabled

            // Calculate max samples based on max duration and sample rate
            maxTransitionSamples = Mathf.CeilToInt(maxSmoothnessDuration * AudioSettings.outputSampleRate);

            // Initialize state
            IsPlaying = false;
            isFadingOut = false;
            isFadingIn = false;
            playAfterFadeOut = false;
            readyToStop = false;
            currentFadeLevel = 1.0f;
            source.volume = baseVolume; // Set initial volume
            source.pitch = basePitch * currentPitchMultiplier;
        }

        /// <summary>
        /// Called every frame. Handles state changes triggered by the audio thread.
        /// </summary>
        private void Update()
        {
            // Check flag set by audio thread (OnAudioFilterRead) for fade-out completion
            if (readyToStop)
            {
                readyToStop = false; // Consume flag

                bool restart = playAfterFadeOut; // Check if the fade was intended for a restart

                if (restart)
                {
                    // --- Restart Logic (Reset Time) ---
                    // Debug.Log("Update: Restarting audio via time reset after fade out.");
                    if (hasPendingPitch)
                    {
                        // Apply pending pitch immediately before restarting
                        currentPitchMultiplier = nextPitchMultiplier;
                        source.pitch = basePitch * currentPitchMultiplier;
                        hasPendingPitch = false;
                    }
                    source.timeSamples = 0; // Reset playback position
                    source.volume = baseVolume; // Ensure volume is reset
                    currentFadeLevel = 1.0f; // Reset internal fade level
                    IsPlaying = true; // State is now playing (restarted)
                    // Note: source.Play() is not needed if it was already playing before the quick fade
                    // If it wasn't playing, Play() should have been called in the method initiating the restart.
                    // Ensure it IS playing if it stopped during the fade:
                    if (!source.isPlaying) source.Play();

                }
                else
                {
                    // --- Normal Stop Logic (After Fade) ---
                    // Debug.Log("Update: Stopping audio after fade out.");
                    source.Stop(); // Stop the source completely
                    IsPlaying = false; // State is now stopped
                    currentFadeLevel = 1.0f; // Reset internal fade level
                    source.volume = baseVolume; // Reset volume for next play
                }

                // Reset flags after action is taken
                isFadingOut = false;
                playAfterFadeOut = false;
                // isFadingIn should already be false if we were fading out
            }
            // Add check for natural end-of-clip stop
            else if (IsPlaying && !source.isPlaying && !isFadingOut && !isFadingIn)
            {
                // If the internal state thinks it's playing, but the source stopped
                // AND we are not in the middle of a fade (which would trigger readyToStop or finish fade-in)
                // then the clip must have finished naturally.
                // Debug.Log("Update: Audio stopped naturally (end of clip).");
                IsPlaying = false; // Update internal state
                currentFadeLevel = 1.0f; // Reset level state
                isFadingOut = false; // Ensure fade flag is clear
                playAfterFadeOut = false; // Ensure restart flag is clear
                hasPendingPitch = false; // Clear any pending pitch
            }

            // Update smooth filter transition
            UpdateFilterTransition();
        }


        /// <summary>
        /// Plays the audio immediately without any transition. Stops current playback if any.
        /// </summary>
        public override void Play()
        {
            // Debug.Log("Play called - Immediate start.");
            // Cancel any ongoing fades or transitions
            isFadingOut = false;
            isFadingIn = false;
            playAfterFadeOut = false;
            readyToStop = false;
            hasPendingPitch = false; // Clear pending pitch

            // Stop potential smooth volume change coroutine
            StopVolumeCoroutine();

            // Apply current pitch immediately
            source.pitch = basePitch * currentPitchMultiplier;
            source.volume = baseVolume; // Set volume directly
            currentFadeLevel = 1.0f; // Reset internal fade level

            source.timeSamples = 0; // Start from beginning
            source.Play();
            IsPlaying = true;
        }

        /// <summary>
        /// Plays the audio, initiating a smooth fade-in or a quick crossfade if already playing.
        /// </summary>
        /// <param name="smoothness">Duration of the fade-in in seconds. If near zero, plays immediately.</param>
        public override void PlaySmooth(float smoothness)
        {
            // If smoothness is negligible, perform an immediate play
            if (Mathf.Approximately(smoothness, 0f))
            {
                Play();
                return;
            }

            // Calculate fade-in samples based on smoothness, clamped to max duration
            currentFadeInSamples = Mathf.Clamp(
                Mathf.CeilToInt(smoothness * AudioSettings.outputSampleRate),
                1, // Ensure at least 1 sample for fade
                maxTransitionSamples
            );

            // Debug.Log($"PlaySmooth called with smoothness {smoothness}s -> {currentFadeInSamples} samples. Current state: IsPlaying={IsPlaying}, isFadingOut={isFadingOut}, isFadingIn={isFadingIn}");

            // Use source.isPlaying OR isFadingOut/isFadingIn to check current activity
            if (source.isPlaying || isFadingOut || isFadingIn)
            {
                // --- Transition Logic (Quick Fade Out -> Restart) ---
                // Debug.Log("PlaySmooth: Transition needed. Initiating quick fade for restart.");

                // Set flags for a quick fade-out (using fixed quick samples) followed by a restart
                isFadingOut = true;
                isFadingIn = false;
                playAfterFadeOut = true;
                readyToStop = false;

                // Buffer the pitch for the restart in Update
                SetPitchOnNextPlay(currentPitchMultiplier);
            }
            else
            {
                // --- Start Fresh with Fade-In ---
                // Debug.Log($"PlaySmooth: Starting fresh playback with fade-in over {currentFadeInSamples} samples.");

                StopVolumeCoroutine();
                source.pitch = basePitch * currentPitchMultiplier;
                hasPendingPitch = false;
                source.volume = baseVolume;
                source.timeSamples = 0;
                source.Play();

                // Initiate fade-in state using calculated samples
                isFadingIn = true;
                isFadingOut = false;
                currentFadeLevel = 0f;
                IsPlaying = true;
                readyToStop = false;
                playAfterFadeOut = false;
            }
        }


        /// <summary>
        /// Pauses playback immediately.
        /// </summary>
        public override void Pause()
        {
            // Debug.Log("Pause called - Immediate pause.");
            // Cancel any ongoing fades
            isFadingOut = false;
            isFadingIn = false;
            playAfterFadeOut = false;
            readyToStop = false;
            // We keep IsPlaying true potentially, as it's paused, not stopped.
            // Or set IsPlaying = false; depending on desired behavior. Let's set it false.
            IsPlaying = false;
            hasPendingPitch = false; // Clear pending pitch

            source.Pause();
            // Restore volume in case it was mid-fade? Or leave it? Let's restore.
            // source.volume = baseVolume;
            // currentFadeLevel = 1.0f;
            // Let's leave volume as is, Resume should handle it or Play.
        }

        /// <summary>
        /// Stops playback immediately.
        /// </summary>
        public override void Stop()
        {
            // Debug.Log("Stop called - Immediate stop.");
            // Cancel any ongoing fades or transitions
            isFadingOut = false;
            isFadingIn = false;
            playAfterFadeOut = false;
            readyToStop = false;
            hasPendingPitch = false; // Clear pending pitch

            // Stop potential smooth volume change coroutine
            StopVolumeCoroutine();

            source.Stop();
            IsPlaying = false;
            currentFadeLevel = 1.0f; // Reset internal fade level
            source.volume = baseVolume; // Reset volume
        }

        /// <summary>
        /// Stops playback with a smooth fade-out.
        /// </summary>
        /// <param name="smoothness">Duration of the fade-out in seconds. If near zero, stops immediately.</param>
        public override void StopSmooth(float smoothness)
        {
            // If smoothness is negligible, perform an immediate stop
            if (Mathf.Approximately(smoothness, 0f))
            {
                Stop();
                return;
            }

            // Calculate fade-out samples based on smoothness, clamped to max duration
            currentFadeOutSamples = Mathf.Clamp(
                Mathf.CeilToInt(smoothness * AudioSettings.outputSampleRate),
                1, // Ensure at least 1 sample for fade
                maxTransitionSamples
            );

            // Debug.Log($"StopSmooth called with smoothness {smoothness}s -> {currentFadeOutSamples} samples. Current state: IsPlaying={IsPlaying}, source.isPlaying={source.isPlaying}, isFadingOut={isFadingOut}, isFadingIn={isFadingIn}");

            // Only start fade if playing and not already fading out *for a stop*
            if (source.isPlaying && (!isFadingOut || playAfterFadeOut || isFadingIn))
            {
                 if (!isFadingOut && !isFadingIn) {
                    // Debug.Log($"StopSmooth: Initiating fade out for stop over {currentFadeOutSamples} samples.");
                 } else if (isFadingIn) {
                     // Debug.Log($"StopSmooth: Aborting fade-in and initiating fade out for stop over {currentFadeOutSamples} samples.");
                 } else { // Was fading out for restart
                     // Debug.Log($"StopSmooth: Changing fade out intention from restart to stop over {currentFadeOutSamples} samples.");
                 }

                StopVolumeCoroutine();

                isFadingOut = true;
                isFadingIn = false;
                playAfterFadeOut = false; // Ensure it stops, doesn't restart
                readyToStop = false;
                hasPendingPitch = false;
            }
             else if (!source.isPlaying && IsPlaying) {
                 // Debug.Log("StopSmooth: Source not playing, performing immediate stop.");
                 Stop();
             }
             // else {
                 // Debug.Log("StopSmooth ignored: Not playing or already fading out for stop.");
             // }
        }

        /// <summary>
        /// Sets the pitch multiplier. Final pitch is basePitch * pitchFactor.
        /// </summary>
        /// <param name="pitchFactor">The pitch multiplier (1.0 is normal pitch).</param>
        public override void SetPitchFactor(float pitchFactor)
        {
            currentPitchMultiplier = Mathf.Max(0.01f, pitchFactor);
            // Apply immediately only if not fading out for a restart (pitch applied on restart)
            if (!(isFadingOut && playAfterFadeOut))
            {
                 source.pitch = basePitch * currentPitchMultiplier;
            }
            // If a restart is pending, the *next* multiplier is updated,
            // but the currently playing sound continues with its pitch until restart.
            if (hasPendingPitch) {
                nextPitchMultiplier = currentPitchMultiplier;
            }
        }

        /// <summary>
        /// Sets the base volume level (0 to 1). Smooth transitions use this as the target.
        /// </summary>
        /// <param name="volume">Normalized volume level (0-1).</param>
        public override void SetNormalisedVolume(float volume)
        {
            baseVolume = Mathf.Clamp01(volume);

            // Option 1: Apply immediately if not fading
            if (!isFadingIn && !isFadingOut)
            {
                source.volume = baseVolume;
                currentFadeLevel = baseVolume; // Keep fade level consistent if needed? Or 1.0? Let's use 1.0
                currentFadeLevel = 1.0f;
            }
            // Option 2: Use a smooth coroutine change (like CustomAudioType) - uncomment if needed
            // StartSmoothVolumeChange(baseVolume, 100f); // Example: 100ms duration

            // If fading in, the fade will naturally progress towards 1.0 (representing full baseVolume).
            // If fading out, it progresses towards 0. Changing baseVolume mid-fade doesn't alter the fade target (0 or 1).
            // The *actual* volume applied by the fade is currentFadeLevel * baseVolume.
            // Let's ensure the source.volume reflects the base for loudness reference, fade modifies output samples.
            source.volume = baseVolume;

        }

        /// <summary>
        /// Sets the base pitch based on a frequency. Assumes A4 = 440Hz.
        /// </summary>
        /// <param name="frequency">The base frequency in Hz.</param>
        public override void SetBaseFrequency(float frequency)
        {
            // Convert frequency to pitch factor (relative to A4 = 440Hz)
            // Or simply use it to set basePitch if the source clip is tuned to a specific note.
            // For simplicity, let's assume the clip is tuned such that frequency maps directly
            // to a pitch multiplier relative to the clip's original pitch.
            // If the clip is C4, and frequency is C4, basePitch = 1. If frequency is A4, basePitch = 440/261.63
            // This needs clarification based on how clips are prepared.
            // Let's assume a simple relative pitch for now:
            // If the source clip has a known fundamental frequency 'clipFreq', then:
            // basePitch = frequency / clipFreq;
            // If clipFreq is unknown, we might assume it's 1.0 or use the A4 reference:
            float pitchFactor = (frequency > 0) ? frequency / 440.0f : 1.0f; // Example using A4
            basePitch = pitchFactor;

            // Apply immediately only if not fading out for a restart
             if (!(isFadingOut && playAfterFadeOut))
             {
                 source.pitch = basePitch * currentPitchMultiplier;
             }
        }

        /// <summary>
        /// Enables or disables the low-pass filter with a smooth transition.
        /// </summary>
        /// <param name="enabled">True to enable, false to disable.</param>
        public override void SetLowPassEnabled(bool enabled)
        {
            if (lowPassFilter == null) return;

            enableLowPass = enabled;
            lowPassFilter.enabled = true; // Keep the component enabled

            // Set target cutoff based on enabled state
            targetLowPassCutoff = enabled ? GetLowPassCutoffFrequency() : 22000f; // Target the calculated cutoff or max freq

            isTransitioningFilter = true; // Start the smooth transition in Update
        }

        /// <summary>
        /// Sets the low-pass filter cutoff frequency (normalized 0-1).
        /// </summary>
        /// <param name="cutoff">Normalized cutoff value (0=min, 1=max).</param>
        public override void SetLowPassCutoff(float cutoff)
        {
            if (lowPassFilter == null) return;

            // Store the normalized value, calculate actual frequency when needed
            // Assuming cutoff parameter is the normalized value (0-1)
            // We need to store this normalized value if SetLowPassEnabled uses it.
            // Let's refine this: SetLowPassCutoff directly sets the *target* frequency.
            float targetFrequency = GetLowPassCutoffFrequency(cutoff);

            // If the filter is currently enabled, transition to the new cutoff
            if (enableLowPass)
            {
                targetLowPassCutoff = targetFrequency;
                isTransitioningFilter = true;
            }
            else
            {
                // If filter is disabled, just store the value for when it's enabled
                // Or should setting cutoff while disabled enable it? Let's assume store only.
                // We need a variable to store the desired cutoff when disabled.
                // Let's simplify: SetLowPassCutoff sets the target, SetLowPassEnabled toggles between this target and 22000Hz.
                 lowPassFilter.cutoffFrequency = targetFrequency; // Store the base cutoff value
                 targetLowPassCutoff = enableLowPass ? targetFrequency : 22000f; // Update target based on current enabled state
                 // No transition needed if just setting the base value while disabled
                 if (enableLowPass) {
                    isTransitioningFilter = true;
                 }
            }
        }

        /// <summary>
        /// Calculates the actual low-pass cutoff frequency in Hz from a normalized value.
        /// </summary>
        /// <param name="normalizedCutoff">Cutoff value from 0 (min freq) to 1 (max freq).</param>
        /// <returns>Cutoff frequency in Hz.</returns>
        private float GetLowPassCutoffFrequency(float normalizedCutoff = -1f)
        {
            // Use stored cutoff if not provided
            // Need a field to store the normalized cutoff value set by SetLowPassCutoff
            // Let's assume SetLowPassCutoff directly works with frequency for now, simplifying state.
            // If normalized input is required, the calling code should handle the mapping.
            // Re-decide: Follow the interface strictly. Assume cutoff is 0-1. Store it.

            // Add this field to the class:
            // private float storedNormalizedCutoff = 1.0f;
            // Modify SetLowPassCutoff:
            // public override void SetLowPassCutoff(float cutoff) {
            //     storedNormalizedCutoff = Mathf.Clamp01(cutoff);
            //     if (enableLowPass) {
            //         targetLowPassCutoff = GetLowPassCutoffFrequency(); // Use the stored value
            //         isTransitioningFilter = true;
            //     }
            // }
            // Modify GetLowPassCutoffFrequency:
            // private float GetLowPassCutoffFrequency() {
            //     float cutoffFrequency = Mathf.Lerp(10f, 22000f, storedNormalizedCutoff);
            //     return cutoffFrequency;
            // }

            // --- Applying the above logic directly here for now ---
            // This requires adding: private float storedNormalizedCutoff = 1.0f; to the class fields.
            // And modifying SetLowPassCutoff as described above.

            // Assuming storedNormalizedCutoff exists and is set by SetLowPassCutoff:
            // float cutoffFrequency = Mathf.Lerp(10f, 22000f, storedNormalizedCutoff);
            // return cutoffFrequency;

            // TEMPORARY: If SetLowPassCutoff sets frequency directly, use that.
            // Let's assume SetLowPassCutoff sets the target frequency for now.
            // This means the parameter 'cutoff' in SetLowPassCutoff is actually frequency.
            // Let's revert to the original UnityAudio implementation for mapping:
            float cutoffFrequency = Mathf.Lerp(10f, 22000f, Mathf.Clamp01(normalizedCutoff));
            return cutoffFrequency;
            // This implies SetLowPassCutoff should store the normalized value.
        }


        /// <summary>
        /// Enables or disables the reverb filter.
        /// </summary>
        /// <param name="enabled">True to enable, false to disable.</param>
        public override void SetReverbEnabled(bool enabled)
        {
            if (reverbFilter != null)
            {
                reverbFilter.enabled = enabled;
            }
        }

        /// <summary>
        /// Sets the reverb amount (mix).
        /// </summary>
        /// <param name="amount">Normalized reverb amount (0-1).</param>
        public override void SetReverbAmount(float amount)
        {
            if (reverbFilter == null) return;
            // Map amount (0-1) to Unity's reverb settings (example mapping)
            // This might need adjustment based on desired reverb preset/feel
            reverbFilter.reverbPreset = AudioReverbPreset.User; // Use User preset for custom settings
            float normAmount = Mathf.Clamp01(amount);
            reverbFilter.dryLevel = Mathf.Lerp(0f, -10000f, normAmount); // Full dry at 0, full wet needs adjustment
            reverbFilter.reverbLevel = Mathf.Lerp(-10000f, 0f, normAmount); // Map amount to reverb level (adjust range as needed)
            // The following room properties might also be linked to 'amount' or set separately
            // reverbFilter.room = Mathf.Lerp(-10000f, 0f, normAmount);
            // reverbFilter.roomHF = Mathf.Lerp(-10000f, 0f, normAmount);
        }

        /// <summary>
        /// Sets the reverb decay time.
        /// </summary>
        /// <param name="decay">Normalized decay time (0-1).</param>
        public override void SetReverbDecay(float decay)
        {
            if (reverbFilter == null) return;
            reverbFilter.reverbPreset = AudioReverbPreset.User; // Ensure custom settings are used
            // Map decay (0-1) to sensible decay time range (e.g., 0.1s-20s)
            reverbFilter.decayTime = Mathf.Lerp(0.1f, 20f, Mathf.Clamp01(decay));
        }

        // --- Helper Methods ---

        /// <summary>
        /// Stores a pitch multiplier to be applied the next time PlaySmooth is called resulting in a restart.
        /// </summary>
        /// <param name="multiplier">The pitch multiplier to apply.</param>
        private void SetPitchOnNextPlay(float multiplier)
        {
            nextPitchMultiplier = multiplier;
            hasPendingPitch = true;
        }

        /// <summary>
        /// Stops the volume change coroutine if it's running.
        /// </summary>
        private void StopVolumeCoroutine()
        {
            if (volumeChangeCoroutine != null)
            {
                StopCoroutine(volumeChangeCoroutine);
                volumeChangeCoroutine = null;
            }
        }

        /// <summary>
        /// Smoothly transitions the low-pass filter cutoff frequency in the Update loop.
        /// </summary>
        private void UpdateFilterTransition()
        {
            if (isTransitioningFilter && lowPassFilter != null)
            {
                float currentCutoff = lowPassFilter.cutoffFrequency;
                // Check if close enough to stop transitioning
                if (Mathf.Abs(currentCutoff - targetLowPassCutoff) < 1f) // Use a small threshold
                {
                    lowPassFilter.cutoffFrequency = targetLowPassCutoff;
                    isTransitioningFilter = false;
                }
                else
                {
                    // Smoothly interpolate current cutoff toward target
                    float newCutoff = Mathf.Lerp(currentCutoff, targetLowPassCutoff, Time.deltaTime * filterTransitionSpeed);
                    lowPassFilter.cutoffFrequency = newCutoff;
                }
            }
        }


        // --- Audio Thread Processing ---

        /// <summary>
        /// Processes audio samples on the audio thread for smooth transitions (fade-in/fade-out).
        /// </summary>
        private void OnAudioFilterRead(float[] data, int channels)
        {
            // Apply base volume scaling first - the fades operate on a 0-1 scale relative to this.
            // This ensures SetNormalisedVolume changes loudness correctly even during fades.
             for (int i = 0; i < data.Length; i++)
             {
                 data[i] *= baseVolume;
             }

            // Now apply fades if active
            if (isFadingOut)
            {
                ProcessFadeOut(data, channels);
            }
            else if (isFadingIn)
            {
                ProcessFadeIn(data, channels);
            }
            // If neither fading in nor out, pass data through (already scaled by baseVolume).
        }

        /// <summary>
        /// Processes the fade-out effect on the audio buffer (audio thread).
        /// Applies a smoothstep curve from currentFadeLevel towards 0.
        /// Uses dynamically calculated sample counts.
        /// </summary>
        private void ProcessFadeOut(float[] data, int channels)
        {
            int samplesInThisBuffer = data.Length / channels;
            if (samplesInThisBuffer == 0) return;

            // Determine the correct total duration for this fade operation
            // Use quick samples for restarts, otherwise use the dynamically set fade-out samples
            int totalFadeDuration = playAfterFadeOut ? quickTransitionFadeOutSamples : currentFadeOutSamples;
            if (totalFadeDuration <= 0) totalFadeDuration = 1; // Prevent division by zero

            float startLevel = currentFadeLevel; // Level at the start of this buffer

            // Calculate how much the level *should* decrease over this buffer duration
            float levelDecrementPerSample = 1.0f / totalFadeDuration;
            float levelDecrementThisBuffer = levelDecrementPerSample * samplesInThisBuffer;

            // Calculate the target level at the end of this buffer
            float endLevel = Mathf.Max(0f, startLevel - levelDecrementThisBuffer);

            // Apply smoothstep fade across samples in this buffer
            for (int i = 0; i < samplesInThisBuffer; i++)
            {
                // Normalized position 0 to 1 within this buffer's contribution to the fade
                float t = (samplesInThisBuffer > 1) ? (float)i / (samplesInThisBuffer - 1) : 1.0f;
                // Interpolate linearly between the start and end level for this buffer
                float linearLevel = Mathf.Lerp(startLevel, endLevel, t);
                // Apply smoothstep shaping (t * t * (3f - 2f * t))
                float smoothLevel = linearLevel * linearLevel * (3f - 2f * linearLevel);

                for (int c = 0; c < channels; c++)
                {
                    // Scale the sample (already scaled by baseVolume) by the fade level
                    data[i * channels + c] *= smoothLevel;
                }
            }

            // Update the overall fade level state for the next buffer
            currentFadeLevel = endLevel;

            // Check if fade is effectively complete
            if (currentFadeLevel <= 0.0001f)
            {
                currentFadeLevel = 0f; // Clamp to zero

                // Explicitly zero out the rest of the buffer to prevent artifacts
                // Calculate samples remaining in buffer to clear
                 int samplesProcessed = Mathf.CeilToInt( (startLevel / levelDecrementPerSample) ); // Estimate how many samples were needed to reach zero
                 int startIndexToClear = Mathf.Clamp(samplesProcessed, 0, samplesInThisBuffer);

                 for (int i = startIndexToClear; i < samplesInThisBuffer; i++) {
                     for (int c = 0; c < channels; c++) {
                         data[i * channels + c] = 0f;
                     }
                 }

                // Signal main thread ONLY IF we haven't already signalled it
                if (!readyToStop)
                {
                     readyToStop = true; // Signal Update() to stop/restart
                }
                // Note: isFadingOut is reset by Update thread once readyToStop is processed
            }
        }

        /// <summary>
        /// Processes the fade-in effect on the audio buffer (audio thread).
        /// Applies a smoothstep curve from currentFadeLevel towards 1.
        /// Uses dynamically calculated sample counts.
        /// </summary>
        private void ProcessFadeIn(float[] data, int channels)
        {
            int samplesInThisBuffer = data.Length / channels;
            if (samplesInThisBuffer == 0) return;

            // Use the dynamically set fade-in duration
            int totalFadeDuration = currentFadeInSamples;
            if (totalFadeDuration <= 0) totalFadeDuration = 1; // Prevent division by zero

            float startLevel = currentFadeLevel; // Level at the start of this buffer (starts near 0)

            // Calculate how much the level *should* increase over this buffer duration
            float levelIncrementPerSample = 1.0f / totalFadeDuration;
            float levelIncrementThisBuffer = levelIncrementPerSample * samplesInThisBuffer;

            // Calculate the target level at the end of this buffer
            float endLevel = Mathf.Min(1.0f, startLevel + levelIncrementThisBuffer);

            // Apply smoothstep fade across samples in this buffer
            for (int i = 0; i < samplesInThisBuffer; i++)
            {
                // Normalized position 0 to 1 within this buffer's contribution to the fade
                float t = (samplesInThisBuffer > 1) ? (float)i / (samplesInThisBuffer - 1) : 1.0f;
                // Interpolate linearly between the start and end level for this buffer
                float linearLevel = Mathf.Lerp(startLevel, endLevel, t);
                // Apply smoothstep shaping (same shape as fade out)
                float smoothLevel = linearLevel * linearLevel * (3f - 2f * linearLevel);

                for (int c = 0; c < channels; c++)
                {
                    // Scale the sample (already scaled by baseVolume) by the fade level
                    data[i * channels + c] *= smoothLevel;
                }
            }

            // Update the overall fade level state for the next buffer
            currentFadeLevel = endLevel;

            // Check if fade-in is effectively complete
            if (currentFadeLevel >= 0.9999f)
            {
                currentFadeLevel = 1.0f; // Clamp to one
                isFadingIn = false; // Stop the fade-in process
                // Debug.Log("Fade-in complete.");
                // Ensure remaining samples in buffer get full volume (level 1.0)
                 int samplesProcessed = Mathf.CeilToInt( ((1.0f - startLevel) / levelIncrementPerSample) ); // Estimate samples to reach 1.0
                 int startIndexFullVolume = Mathf.Clamp(samplesProcessed, 0, samplesInThisBuffer);

                 for (int i = startIndexFullVolume; i < samplesInThisBuffer; i++) {
                     // Samples are already scaled by baseVolume, no extra multiplication needed
                 }
            }
        }

        // --- Remove MainThreadDispatcher ---
        // The fade completion logic now uses the readyToStop flag checked in Update.
        // If MainThreadDispatcher was used for other things, it can remain, but it's removed here
        // as it's no longer needed for the core audio logic updated from CustomAudioType.

    } // End of UnityAudio class

    // Remove or comment out MainThreadDispatcher if it's not used elsewhere
    /*
    public class MainThreadDispatcher : MonoBehaviour
    {
        // ... (Dispatcher implementation) ...
    }
    */

} // End of namespace