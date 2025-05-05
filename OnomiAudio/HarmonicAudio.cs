// Copyright (c) 2025 onomihime (github.com/onomihime)
// Originally from: github.com/onomihime/UnityAudioStuff
// Licensed under the MIT License. See the LICENSE file in the repository root for full license text.
// This file may be used in commercial projects provided the above copyright notice and this permission notice appear in all copies.




using UnityEngine;
using System.Collections.Generic;

namespace Modules.Audio
{
    [RequireComponent(typeof(AudioSource))]
    public class HarmonicAudio : CustomAudioComponent
    {
        private bool _isPlaying = false;
        public override bool IsPlaying { get => audioSource.isPlaying; set => _isPlaying = value; }
        
        // --- Component Reference ---
        [SerializeField] private AudioSource audioSource;

        // --- Synthesis Parameters ---
        [Header("Synth Settings")]
        [Tooltip("Select the harmonic profile preset")]
        public HarmonicPresets.PresetType harmonicPreset = HarmonicPresets.PresetType.Piano;
        [Tooltip("The fundamental frequency (Hz) when pitch factor is 1 (e.g., A4 = 440 Hz)")]
        public float baseFrequency = 440.0f;
        // Display the loaded harmonics, but primary control is via the preset enum
        [Tooltip("Read-only display of loaded harmonics from the preset")]
        [SerializeField] // Show in inspector but primarily loaded from preset
        private List<HarmonicInfo> harmonics = new List<HarmonicInfo>();

        private float currentPitchFactor = 1.0f;

        // --- Low Pass Filter Settings ---
        [Header("Low Pass Filter")]
        [Tooltip("Enable the low pass filter")]
        public bool enableLowPassFilter = false;
        [Tooltip("Base cutoff frequency (Hz) when volume is max (1.0)")]
        [Range(20f, 22000f)] public float filterMaxCutoff = 20000f;
        [Tooltip("Cutoff frequency (Hz) when volume is min (0.0)")]
        [Range(20f, 22000f)] public float filterMinCutoff = 500f;
        [Tooltip("Adjusts the curve of volume-to-cutoff mapping (1=linear, >1 steeper drop at low vol, <1 gentler drop)")]
        public float filterVolumeCurve = 1.0f;

        // --- Filter State (per channel) ---
        private float[] lastOutputSample; // Store previous output for IIR filter
        private float filterAlpha = 1.0f; // Filter coefficient (1.0 = bypass)

        // --- Playback State ---
        private volatile bool isPlaying = false;
        private volatile bool isPaused = false;
        private double phase = 0.0;
        private int sampleRate = 0;

        // --- DSP Fade State ---
        private enum FadeState { Idle, FadingIn, FadingOut }
        private volatile FadeState currentState = FadeState.Idle;
        private float fadeDuration = 0.1f;
        private float fadeTimer = 0.0f;
        private float startVolume = 0.0f;
        private float targetVolume = 1.0f;
        private float currentDSPVolume = 0.0f;
        private float userSetVolume = 1.0f;

        // Note: HarmonicInfo struct is now defined in HarmonicPresets.cs (or could be duplicated here)

        void Awake()
        {
            audioSource = GetComponent<AudioSource>();
            sampleRate = AudioSettings.outputSampleRate;
            if (sampleRate <= 0) sampleRate = 48000; // Fallback if API fails

            // Load harmonics from the selected preset
            LoadHarmonicsFromPreset();

            // Configure AudioSource
            audioSource.playOnAwake = false;
            audioSource.Stop();
            audioSource.clip = null;
            audioSource.volume = 1.0f;
            audioSource.pitch = 1.0f;
            audioSource.loop = true; // Keep OnAudioFilterRead running

            // Initialize filter state array (will be sized correctly in first OnAudioFilterRead)
            lastOutputSample = null;

            // Start silent
            currentDSPVolume = 0.0f;
            currentState = FadeState.Idle;
        }

        // Call this if you change the preset property at runtime via script
        public void LoadHarmonicsFromPreset()
        {
             harmonics = HarmonicPresets.GetHarmonics(harmonicPreset);
             // Optional: Add validation if harmonics list is empty after loading
             if(harmonics.Count == 0) {
                 Debug.LogWarning($"No harmonics loaded for preset {harmonicPreset} on {gameObject.name}. Sound will be silent.");
             }
        }

        // --- CustomAudioComponent Implementation [Play, Pause, Stop, etc. -UNCHANGED from previous version] ---
        // ... (Keep the Play, PlaySmooth, Pause, Stop, StopSmooth methods exactly as before) ...
        public override void Play()
        {
            if (!audioSource.isPlaying) audioSource.Play(); // Ensure callbacks are running

            isPaused = false;
            isPlaying = true;
            currentState = FadeState.Idle; // Abrupt start
            currentDSPVolume = userSetVolume; // Apply volume immediately
            phase = 0.0; // Reset phase for consistent start
            RecalculateFilter(); // Update filter based on current volume
        }

        public override void PlaySmooth(float smoothness)
        {
             if (!audioSource.isPlaying) audioSource.Play(); // Ensure callbacks are running

            isPaused = false;
            isPlaying = true;
            phase = 0.0; // Reset phase

            fadeDuration = Mathf.Max(0.01f, smoothness);
            fadeTimer = 0.0f;
            startVolume = currentDSPVolume;
            targetVolume = userSetVolume;
            currentState = FadeState.FadingIn;
            RecalculateFilter(); // Update filter based on target volume
        }

        public override void Pause()
        {
            if (!isPlaying) return;
            isPaused = true;
             currentState = FadeState.Idle; // Consider fade cancelled
             currentDSPVolume = 0.0f; // Silence output immediately in filter read
             RecalculateFilter(); // Update filter (will likely be min cutoff)
        }

        public override void Stop()
        {
            if (!isPlaying) return;

            isPlaying = false;
            isPaused = false;
            currentState = FadeState.Idle; // Abrupt stop
            currentDSPVolume = 0.0f; // Will cause immediate silence in filter read
            phase = 0.0; // Reset phase
            RecalculateFilter(); // Update filter (will likely be min cutoff)
        }

        public override void StopSmooth(float smoothness)
        {
            if (!isPlaying || currentState == FadeState.FadingOut) return; // Already stopping or stopped

            isPaused = false; // Ensure not paused if stopping smoothly

            fadeDuration = Mathf.Max(0.01f, smoothness);
            fadeTimer = 0.0f;
            startVolume = currentDSPVolume; // Start fade from current actual volume
            targetVolume = 0.0f; // Fade to silence
            currentState = FadeState.FadingOut;
            // isPlaying remains true until the fade completes in OnAudioFilterRead
             // Filter will naturally adjust during fade in OnAudioFilterRead
        }

        public override void SetPitchFactor(float pitchFactor)
        {
            currentPitchFactor = Mathf.Max(0.01f, pitchFactor);
        }

        public override void SetNormalisedVolume(float volume)
        {
            userSetVolume = Mathf.Clamp01(volume);
            if (currentState == FadeState.Idle)
            {
                currentDSPVolume = userSetVolume;
            }
            else if (currentState == FadeState.FadingIn)
            {
                targetVolume = userSetVolume;
            }
            // Recalculate filter whenever target volume changes
            RecalculateFilter();
        }
        
        // Add these methods to the HarmonicAudio class

        public override void SetBaseFrequency(float frequency)
        {
            baseFrequency = Mathf.Max(20.0f, frequency);
        }

        public override void SetLowPassEnabled(bool enabled)
        {
            enableLowPassFilter = enabled;
            RecalculateFilter();
        }

        public override void SetLowPassCutoff(float cutoff)
        {
            // Map normalized cutoff (0-1) to frequency range
            cutoff = Mathf.Clamp01(cutoff);
            filterMaxCutoff = Mathf.Lerp(500f, 22000f, cutoff);
            RecalculateFilter();
        }

        // Reverb implementations
        private bool reverbEnabled = false;
        private float reverbAmount = 0.5f;
        private float reverbDecay = 0.5f;
        private float[] reverbBuffer = null;
        private int reverbBufferPosition = 0;
        private int reverbDelayLength = 0;

        public override void SetReverbEnabled(bool enabled)
        {
            reverbEnabled = enabled;
            
            // Initialize reverb buffer if enabling
            if (enabled && reverbBuffer == null)
            {
                InitializeReverbBuffer();
            }
        }

        public override void SetReverbAmount(float amount)
        {
            reverbAmount = Mathf.Clamp01(amount);
        }

        public override void SetReverbDecay(float decay)
        {
            reverbDecay = Mathf.Clamp01(decay);
        }




        // --- Filter Calculation ---
        private void RecalculateFilter()
        {
            if (!enableLowPassFilter || sampleRate <= 0)
            {
                filterAlpha = 1.0f; // Bypass filter
                return;
            }

            // Determine the volume level to use for cutoff calculation
            // Use the *target* volume ('userSetVolume') for responsiveness,
            // rather than the potentially changing 'currentDSPVolume' during fades.
            float volumeForFilter = userSetVolume;

            // Map volume (0..1) to cutoff frequency (minCutoff..maxCutoff)
            // Apply curve: Pow(volume, curve) adjusts the mapping shape
            float curvedVolume = Mathf.Pow(volumeForFilter, filterVolumeCurve);
            float targetCutoff = Mathf.Lerp(filterMinCutoff, filterMaxCutoff, curvedVolume);

            // Clamp cutoff to valid range (preventing issues near 0 or Nyquist)
            targetCutoff = Mathf.Clamp(targetCutoff, 20.0f, sampleRate / 2.0f - 1.0f); // Leave some headroom

            // Calculate the filter coefficient alpha for a first-order IIR LPF
            // Using the formula: alpha = 1 - exp(-2 * pi * fc / fs) - more stable
            float omega = 2.0f * Mathf.PI * targetCutoff / sampleRate;
            filterAlpha = 1.0f - Mathf.Exp(-omega);

            // Ensure alpha is within reasonable bounds (0 to 1)
            filterAlpha = Mathf.Clamp01(filterAlpha);
        }


        // --- Audio Generation & Filtering ---
        void OnAudioFilterRead(float[] data, int channels)
        {
            if (sampleRate <= 0) return; // Guard against invalid state

            // --- Initialize filter state buffer if needed ---
            if (lastOutputSample == null || lastOutputSample.Length != channels)
            {
                lastOutputSample = new float[channels];
                // Initialize previous samples to 0
                for(int i=0; i<channels; ++i) lastOutputSample[i] = 0f;
                //Debug.Log($"HarmonicAudio: Initialized filter state for {channels} channels.");
                // Calculate initial filter settings now that we know the channel count
                RecalculateFilter();
            }


            double timeIncrement = 1.0 / sampleRate;
            float currentSampleVolume = currentDSPVolume; // Base volume for this buffer pass

            // --- Update DSP Fade State ---
            if (currentState != FadeState.Idle)
            {
                 float lerpFactorStart = Mathf.Clamp01(fadeTimer / fadeDuration);
                 currentSampleVolume = Mathf.Lerp(startVolume, targetVolume, lerpFactorStart);
                 // Note: Filter cutoff doesn't dynamically change *during* a fade here,
                 // it's based on userSetVolume (the target). For dynamic filter fades,
                 // RecalculateFilter() would need to be called inside the sample loop based on currentSampleVolume.
            }

            // --- Generate and Filter Samples ---
            for (int i = 0; i < data.Length; i += channels)
            {
                float rawSample = 0.0f; // Sample before filtering and volume

                // --- Calculate Fade Volume for this specific sample frame ---
                if (currentState != FadeState.Idle)
                {
                    fadeTimer += (float)timeIncrement; // Advance timer per *frame* not per channel sample
                    float lerpFactor = Mathf.Clamp01(fadeTimer / fadeDuration);
                    currentSampleVolume = Mathf.Lerp(startVolume, targetVolume, lerpFactor);
                }

                // --- Generate Harmonics if Playing and Not Paused ---
                if (isPlaying && !isPaused)
                {
                    double currentPhase = phase;
                    float effectiveBaseFreq = baseFrequency * currentPitchFactor;

                    foreach (var harmonic in harmonics)
                    {
                        if (harmonic.number <= 0) continue;
                        double freq = effectiveBaseFreq * harmonic.number;
                        rawSample += harmonic.amplitude * (float)System.Math.Sin(currentPhase * freq * 2.0 * System.Math.PI);
                    }

                    // Advance phase for the next sample frame
                    phase += timeIncrement;
                }
                // If not playing/paused, rawSample remains 0 (silence)

                // --- Apply Filter and Volume (Per Channel) ---
                for (int j = 0; j < channels; ++j)
                {
                    float processedSample = rawSample; // Start with the raw summed harmonics

                    // Apply Low Pass Filter (if enabled and not bypassed)
                    if (enableLowPassFilter && filterAlpha < 0.9999f) // Check alpha isn't effectively 1 (bypass)
                    {
                        // Simple 1st order IIR: y[n] = alpha*x[n] + (1-alpha)*y[n-1]
                        processedSample = filterAlpha * processedSample + (1.0f - filterAlpha) * lastOutputSample[j];
                        lastOutputSample[j] = processedSample; // Store for next iteration
                    }
                    else if (enableLowPassFilter) // Filter enabled but bypassed (alpha=1)
                    {
                         // If filter is technically enabled but cutoff is maxed out (alpha near 1),
                         // ensure the filter state doesn't hold old values.
                         lastOutputSample[j] = processedSample;
                    }


                    // Apply overall volume (userSet + DSP fade)
                    processedSample *= currentSampleVolume;

                    // Clamp final output
                    data[i + j] = Mathf.Clamp(processedSample, -1.0f, 1.0f);
                }


                 // --- Check Fade Completion ---
                 if (currentState != FadeState.Idle && fadeTimer >= fadeDuration)
                 {
                     currentSampleVolume = targetVolume;
                     currentDSPVolume = targetVolume;

                     if (currentState == FadeState.FadingOut)
                     {
                         isPlaying = false;
                         phase = 0.0;
                         // Reset filter state on stop? Optional, maybe better to let it decay naturally if played again soon.
                         // for(int k=0; k<channels; ++k) lastOutputSample[k] = 0f;
                     }

                     currentState = FadeState.Idle;
                     fadeTimer = 0.0f;
                     // Recalculate filter based on the final volume state
                     RecalculateFilter();
                     // Don't break loop
                 }

            } // End of buffer loop


             // Update the main state volume tracker after processing the buffer
             if (currentState == FadeState.Idle)
             {
                 currentDSPVolume = isPlaying ? userSetVolume : 0.0f;
             } else {
                 currentDSPVolume = currentSampleVolume;
             }
        }
        
        // --- Reverb Buffer Initialization ---
        
        private void InitializeReverbBuffer()
        {
            // Calculate buffer size based on audio settings
            int sampleRate = AudioSettings.outputSampleRate;
            reverbDelayLength = Mathf.Max(100, (int)(sampleRate * 0.05f));
    
            // Initialize buffer for stereo
            reverbBuffer = new float[reverbDelayLength * 2];
            reverbBufferPosition = 0;
    
            // Clear buffer
            for (int i = 0; i < reverbBuffer.Length; i++)
            {
                reverbBuffer[i] = 0f;
            }
        }

        // Modify OnAudioFilterRead to include reverb processing
        // Add this code to the end of the existing OnAudioFilterRead method
        private void ApplyReverbEffect(float[] data, int channels)
        {
            if (!reverbEnabled || reverbBuffer == null) return;
    
            int samples = data.Length / channels;
    
            // Calculate decay factor
            float decay = 0.2f + (reverbDecay * 0.7f);
    
            for (int i = 0; i < samples; i++)
            {
                for (int c = 0; c < channels; c++)
                {
                    int index = i * channels + c;
                    int bufferIndex = (reverbBufferPosition * channels) + c;
            
                    float input = data[index];
                    float delayed = reverbBuffer[bufferIndex];
            
                    // Mix input with delayed signal
                    data[index] = input + (delayed * reverbAmount);
            
                    // Update delay buffer with feedback
                    reverbBuffer[bufferIndex] = input + (delayed * decay);
                }
        
                // Advance buffer position
                reverbBufferPosition = (reverbBufferPosition + 1) % reverbDelayLength;
            }
        }


        // Optional: Expose internal state for debugging / external logic
        public bool IsGenerating => isPlaying && !isPaused;
        public bool IsFading => currentState != FadeState.Idle;

        #if UNITY_EDITOR
        // Optional: Automatically reload harmonics in editor if preset changes
        void OnValidate()
        {
             if (Application.isPlaying && audioSource != null) // Check if Awake has run
             {
                 // Only reload if the preset selection has actually changed
                 // This check isn't perfect as the list itself isn't directly comparable easily
                 // A better approach might store the loaded preset and compare against that.
                 // For simplicity, let's just reload on any validation during play.
                  LoadHarmonicsFromPreset();
                  RecalculateFilter(); // Also update filter if params changed
             } else if (!Application.isPlaying) {
                 // If not playing, allow inspector changes to stick temporarily,
                 // but Awake will overwrite with preset on play start.
                 // Could force load here too if desired.
                 // harmonics = HarmonicPresets.GetHarmonics(harmonicPreset); // Uncomment to force load in editor
             }
        }
        #endif

    }
}