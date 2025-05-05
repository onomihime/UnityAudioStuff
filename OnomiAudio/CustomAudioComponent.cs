// Copyright (c) 2025 onomihime (github.com/onomihime)
// Originally from: github.com/onomihime/UnityAudioStuff
// Licensed under the MIT License. See the LICENSE file in the repository root for full license text.
// This file may be used in commercial projects provided the above copyright notice and this permission notice appear in all copies.



using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Modules.Audio
{
    public abstract class CustomAudioComponent : MonoBehaviour
    {
        public abstract bool IsPlaying { get; set; }
        
        /// <summary>
        /// Plays the audio immediately
        /// </summary>
        public abstract void Play();
        
        /// <summary>
        /// Plays the audio with a smooth transition
        /// </summary>
        /// <param name="smoothness">The smoothness factor for the transition (higher = smoother)</param>
        public abstract void PlaySmooth(float smoothness);
        
        /// <summary>
        /// Pauses the currently playing audio
        /// </summary>
        public abstract void Pause();
        
        /// <summary>
        /// Stops the currently playing audio immediately
        /// </summary>
        public abstract void Stop();
        
        /// <summary>
        /// Stops the currently playing audio with a smooth transition
        /// </summary>
        /// <param name="smoothness">The smoothness factor for the transition (higher = smoother)</param>
        public abstract void StopSmooth(float smoothness);
        
        /// <summary>
        /// Sets the pitch factor for the audio
        /// </summary>
        /// <param name="pitchFactor">The factor to multiply the base pitch by</param>
        public abstract void SetPitchFactor(float pitchFactor);
        
        /// <summary>
        /// Sets the normalised volume (between 0 and 1)
        /// </summary>
        /// <param name="volume">Volume value between 0 (silent) and 1 (full volume)</param>
        public abstract void SetNormalisedVolume(float volume);
        
        /// <summary>
        /// Sets the base frequency for the audio in Hz
        /// </summary>
        /// <param name="frequency">The base frequency in Hz</param>
        public abstract void SetBaseFrequency(float frequency);

        /// <summary>
        /// Enables or disables the low-pass filter
        /// </summary>
        /// <param name="enabled">Whether the filter is enabled</param>
        public abstract void SetLowPassEnabled(bool enabled);

        /// <summary>
        /// Sets the cutoff frequency for the low-pass filter
        /// </summary>
        /// <param name="cutoff">Normalized cutoff frequency (0.01 to 1.0)</param>
        public abstract void SetLowPassCutoff(float cutoff);

        /// <summary>
        /// Enables or disables the reverb effect
        /// </summary>
        /// <param name="enabled">Whether reverb is enabled</param>
        public abstract void SetReverbEnabled(bool enabled);

        /// <summary>
        /// Sets the reverb amount (dry/wet mix)
        /// </summary>
        /// <param name="amount">Amount of reverb (0.0 - 1.0)</param>
        public abstract void SetReverbAmount(float amount);

        /// <summary>
        /// Sets the reverb decay time
        /// </summary>
        /// <param name="decay">Decay factor (0.0 - 1.0), higher values = longer decay</param>
        public abstract void SetReverbDecay(float decay);
    }
}