// Copyright (c) 2025 onomihime (github.com/onomihime)
// Originally from: github.com/onomihime/UnityCustomUI
// Licensed under the MIT License. See the LICENSE file in the repository root for full license text.
// This file may be used in commercial projects provided the above copyright notice and this permission notice appear in all copies.



using System.Collections.Generic;
using UnityEngine; // Needed for Debug.Log if used

namespace Modules.Audio
{
    // Use the same struct definition as in HarmonicAudio for consistency
    // Alternatively, define it here and reference it from HarmonicAudio
    [System.Serializable]
    public struct HarmonicInfo
    {
        [Tooltip("Harmonic number (1 = fundamental, 2 = first overtone, etc.)")]
        public int number;
        [Tooltip("Relative amplitude (strength) of this harmonic (e.g., 1.0 for fundamental)")]
        public float amplitude;

        public HarmonicInfo(int num, float amp)
        {
            number = num;
            amplitude = amp;
        }
    }

    public static class HarmonicPresets
    {
        public enum PresetType
        {
            Piano,
            Guitar // Example: Acoustic Steel String
            // Add more presets here (e.g., Sine, Square, Sawtooth, Organ...)
        }

        private static Dictionary<PresetType, List<HarmonicInfo>> presets;

        // Static constructor to initialize the presets automatically
        static HarmonicPresets()
        {
            presets = new Dictionary<PresetType, List<HarmonicInfo>>();
            InitializePresets();
        }

        private static void InitializePresets()
        {
            // --- Piano Preset ---
            var pianoHarmonics = new List<HarmonicInfo>
            {
                new HarmonicInfo(1, 1.0f),    // Fundamental
                new HarmonicInfo(2, 0.55f),   // Octave
                new HarmonicInfo(3, 0.40f),   // Fifth
                new HarmonicInfo(4, 0.30f),   // 2 Octaves
                new HarmonicInfo(5, 0.25f),   // Third region
                new HarmonicInfo(6, 0.20f),   // Fifth region
                new HarmonicInfo(7, 0.16f),   // Seventh region
                new HarmonicInfo(8, 0.13f),   // 3 Octaves
                new HarmonicInfo(9, 0.11f),
                new HarmonicInfo(10, 0.09f),
                new HarmonicInfo(11, 0.07f),
                new HarmonicInfo(12, 0.06f),
                new HarmonicInfo(13, 0.05f),
                new HarmonicInfo(14, 0.045f),
                new HarmonicInfo(15, 0.04f),
                new HarmonicInfo(16, 0.035f),
                new HarmonicInfo(17, 0.03f),
                new HarmonicInfo(18, 0.025f),
                new HarmonicInfo(19, 0.02f),
                new HarmonicInfo(20, 0.015f)
            };
            presets.Add(PresetType.Piano, pianoHarmonics);

            // --- Guitar Preset (Simplified Acoustic Steel String Example) ---
            // Typically brighter than piano initially, more complex high harmonics
            var guitarHarmonics = new List<HarmonicInfo>
            {
                new HarmonicInfo(1, 1.0f),   // Fundamental (strong)
                new HarmonicInfo(2, 0.6f),   // Octave
                new HarmonicInfo(3, 0.4f),   // Fifth
                new HarmonicInfo(4, 0.25f),  // Double Octave
                new HarmonicInfo(5, 0.2f),   // Third
                new HarmonicInfo(6, 0.15f),  // Fifth
                new HarmonicInfo(7, 0.1f),   // Seventh area (often complex)
                new HarmonicInfo(8, 0.08f),
                new HarmonicInfo(9, 0.06f),
                new HarmonicInfo(10, 0.05f),
                new HarmonicInfo(11, 0.04f), // Higher, contribute to brightness/attack
                new HarmonicInfo(12, 0.03f)
            };
            presets.Add(PresetType.Guitar, guitarHarmonics);

            // --- Add other presets here ---
        }

        /// <summary>
        /// Gets the list of harmonic information for a given preset type.
        /// Returns an empty list if the preset is not found.
        /// </summary>
        public static List<HarmonicInfo> GetHarmonics(PresetType presetType)
        {
            if (presets.TryGetValue(presetType, out var harmonicList))
            {
                // Return a copy to prevent modification of the original preset data
                return new List<HarmonicInfo>(harmonicList);
            }
            else
            {
                Debug.LogWarning($"Harmonic preset '{presetType}' not found. Returning empty list.");
                return new List<HarmonicInfo>();
            }
        }
    }
}