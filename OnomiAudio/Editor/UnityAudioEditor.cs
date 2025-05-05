using UnityEngine;
using UnityEditor;

namespace Modules.Audio.Editor
{
    [CustomEditor(typeof(UnityAudio))]
    public class UnityAudioEditor : UnityEditor.Editor
    {
        // Parameters for sliders
        private float smoothness = 0.1f;
        private float pitchFactor = 1.0f;
        private float volume = 1.0f;
        private float frequency = 440.0f;
        private bool lowPassEnabled = false;
        private float lowPassCutoff = 1.0f;
        private bool reverbEnabled = false;
        private float reverbAmount = 0.5f;
        private float reverbDecay = 0.5f;

        public override void OnInspectorGUI()
        {
            // Draw the default inspector
            DrawDefaultInspector();

            UnityAudio audio = (UnityAudio)target;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Audio Controls", EditorStyles.boldLabel);

            // Playback controls
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Play"))
            {
                audio.Play();
            }
            if (GUILayout.Button("Pause"))
            {
                audio.Pause();
            }
            if (GUILayout.Button("Stop"))
            {
                audio.Stop();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();

            // Smooth playback controls
            EditorGUILayout.BeginHorizontal();
            smoothness = EditorGUILayout.Slider("Smoothness", smoothness, 0.0f, 2.0f);
            EditorGUILayout.EndHorizontal();
            
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Play Smooth"))
            {
                audio.PlaySmooth(smoothness);
            }
            if (GUILayout.Button("Stop Smooth"))
            {
                audio.StopSmooth(smoothness);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Audio Parameters", EditorStyles.boldLabel);

            // Pitch controls
            EditorGUILayout.BeginHorizontal();
            pitchFactor = EditorGUILayout.Slider("Pitch Factor", pitchFactor, 0.01f, 2.0f);
            if (GUILayout.Button("Set", GUILayout.Width(60)))
            {
                audio.SetPitchFactor(pitchFactor);
            }
            EditorGUILayout.EndHorizontal();

            // Volume controls
            EditorGUILayout.BeginHorizontal();
            volume = EditorGUILayout.Slider("Volume", volume, 0.0f, 1.0f);
            if (GUILayout.Button("Set", GUILayout.Width(60)))
            {
                audio.SetNormalisedVolume(volume);
            }
            EditorGUILayout.EndHorizontal();

            // Frequency controls
            EditorGUILayout.BeginHorizontal();
            frequency = EditorGUILayout.Slider("Frequency (Hz)", frequency, 20.0f, 2000.0f);
            if (GUILayout.Button("Set", GUILayout.Width(60)))
            {
                audio.SetBaseFrequency(frequency);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Filter Controls", EditorStyles.boldLabel);

            // Low Pass Filter
            EditorGUILayout.BeginHorizontal();
            lowPassEnabled = EditorGUILayout.Toggle("Low Pass Enabled", lowPassEnabled);
            if (GUILayout.Button("Apply", GUILayout.Width(60)))
            {
                audio.SetLowPassEnabled(lowPassEnabled);
            }
            EditorGUILayout.EndHorizontal();

            // Low Pass Cutoff
            EditorGUILayout.BeginHorizontal();
            lowPassCutoff = EditorGUILayout.Slider("Low Pass Cutoff", lowPassCutoff, 0.01f, 1.0f);
            if (GUILayout.Button("Set", GUILayout.Width(60)))
            {
                audio.SetLowPassCutoff(lowPassCutoff);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Reverb Controls", EditorStyles.boldLabel);

            // Reverb Enabled
            EditorGUILayout.BeginHorizontal();
            reverbEnabled = EditorGUILayout.Toggle("Reverb Enabled", reverbEnabled);
            if (GUILayout.Button("Apply", GUILayout.Width(60)))
            {
                audio.SetReverbEnabled(reverbEnabled);
            }
            EditorGUILayout.EndHorizontal();

            // Reverb Amount
            EditorGUILayout.BeginHorizontal();
            reverbAmount = EditorGUILayout.Slider("Reverb Amount", reverbAmount, 0.0f, 1.0f);
            if (GUILayout.Button("Set", GUILayout.Width(60)))
            {
                audio.SetReverbAmount(reverbAmount);
            }
            EditorGUILayout.EndHorizontal();

            // Reverb Decay
            EditorGUILayout.BeginHorizontal();
            reverbDecay = EditorGUILayout.Slider("Reverb Decay", reverbDecay, 0.0f, 1.0f);
            if (GUILayout.Button("Set", GUILayout.Width(60)))
            {
                audio.SetReverbDecay(reverbDecay);
            }
            EditorGUILayout.EndHorizontal();

            // Apply changes when targets are modified
            if (GUI.changed)
            {
                EditorUtility.SetDirty(target);
            }
        }
    }
}