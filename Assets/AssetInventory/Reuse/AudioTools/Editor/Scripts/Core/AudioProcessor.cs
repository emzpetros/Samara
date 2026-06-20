using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace AudioTool
{
    /// <summary>
    /// Audio processing utilities for trimming, normalizing, fading, and exporting audio files.
    /// </summary>
    public static class AudioProcessor
    {
        /// <summary>
        /// Trims audio samples to the specified range.
        /// </summary>
        /// <param name="samples">The original samples (interleaved for multi-channel)</param>
        /// <param name="channels">Number of audio channels</param>
        /// <param name="startSample">Start sample index (per channel)</param>
        /// <param name="endSample">End sample index (per channel)</param>
        /// <returns>Trimmed samples array</returns>
        public static float[] TrimAudio(float[] samples, int channels, int startSample, int endSample)
        {
            if (samples == null || samples.Length == 0) return samples;
            if (startSample < 0) startSample = 0;
            if (endSample > samples.Length / channels) endSample = samples.Length / channels;
            if (startSample >= endSample) return new float[0];

            int trimmedLength = (endSample - startSample) * channels;
            float[] trimmedSamples = new float[trimmedLength];

            int sourceOffset = startSample * channels;
            Array.Copy(samples, sourceOffset, trimmedSamples, 0, trimmedLength);

            return trimmedSamples;
        }

        /// <summary>
        /// Detects silence at the beginning and end of the audio.
        /// </summary>
        /// <param name="samples">The audio samples (interleaved for multi-channel)</param>
        /// <param name="channels">Number of audio channels</param>
        /// <param name="threshold">Amplitude threshold below which is considered silence (0.0-1.0)</param>
        /// <returns>Tuple of (startSample, endSample) representing the non-silent portion</returns>
        public static (int startSample, int endSample) DetectSilence(float[] samples, int channels, float threshold = 0.01f)
        {
            if (samples == null || samples.Length == 0) return (0, 0);

            int totalSamples = samples.Length / channels;
            int startSample = 0;
            int endSample = totalSamples;

            // Find first non-silent sample
            for (int i = 0; i < totalSamples; i++)
            {
                bool isSilent = true;
                for (int ch = 0; ch < channels; ch++)
                {
                    int idx = i * channels + ch;
                    if (Mathf.Abs(samples[idx]) > threshold)
                    {
                        isSilent = false;
                        break;
                    }
                }
                if (!isSilent)
                {
                    startSample = i;
                    break;
                }
            }

            // Find last non-silent sample
            for (int i = totalSamples - 1; i >= startSample; i--)
            {
                bool isSilent = true;
                for (int ch = 0; ch < channels; ch++)
                {
                    int idx = i * channels + ch;
                    if (Mathf.Abs(samples[idx]) > threshold)
                    {
                        isSilent = false;
                        break;
                    }
                }
                if (!isSilent)
                {
                    endSample = i + 1;
                    break;
                }
            }

            return (startSample, endSample);
        }

        /// <summary>
        /// Normalizes audio samples to a target peak level.
        /// </summary>
        /// <param name="samples">The audio samples to normalize (modified in place)</param>
        /// <param name="targetPeak">Target peak amplitude (0.0-1.0)</param>
        /// <returns>The normalization factor applied</returns>
        public static float Normalize(float[] samples, float targetPeak = 0.95f)
        {
            if (samples == null || samples.Length == 0) return 1f;

            float currentPeak = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                float abs = Mathf.Abs(samples[i]);
                if (abs > currentPeak) currentPeak = abs;
            }

            if (currentPeak <= 0f) return 1f;

            float factor = targetPeak / currentPeak;
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] *= factor;
            }

            return factor;
        }

        /// <summary>
        /// Gets the peak amplitude of the audio samples.
        /// </summary>
        /// <param name="samples">The audio samples</param>
        /// <returns>Peak amplitude (0.0-1.0)</returns>
        public static float GetPeakAmplitude(float[] samples)
        {
            if (samples == null || samples.Length == 0) return 0f;

            float peak = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                float abs = Mathf.Abs(samples[i]);
                if (abs > peak) peak = abs;
            }
            return peak;
        }

        /// <summary>
        /// Applies a fade effect to the audio samples.
        /// </summary>
        /// <param name="samples">The audio samples (modified in place)</param>
        /// <param name="channels">Number of audio channels</param>
        /// <param name="fadeSamples">Number of samples over which to fade</param>
        /// <param name="fadeIn">True for fade in, false for fade out</param>
        /// <param name="curve">AnimationCurve defining the fade shape (null for linear)</param>
        public static void ApplyFade(float[] samples, int channels, int fadeSamples, bool fadeIn, AnimationCurve curve = null)
        {
            if (samples == null || samples.Length == 0 || fadeSamples <= 0) return;

            int totalSamples = samples.Length / channels;
            fadeSamples = Mathf.Min(fadeSamples, totalSamples);

            for (int i = 0; i < fadeSamples; i++)
            {
                float t = (float)i / fadeSamples;
                float fadeValue;

                if (curve != null)
                {
                    fadeValue = fadeIn ? curve.Evaluate(t) : curve.Evaluate(1f - t);
                }
                else
                {
                    fadeValue = fadeIn ? t : (1f - t);
                }

                int sampleIndex = fadeIn ? i : (totalSamples - 1 - i);
                for (int ch = 0; ch < channels; ch++)
                {
                    int idx = sampleIndex * channels + ch;
                    if (idx >= 0 && idx < samples.Length)
                    {
                        samples[idx] *= fadeValue;
                    }
                }
            }
        }

        /// <summary>
        /// Adjusts the volume of audio samples.
        /// </summary>
        /// <param name="samples">The audio samples (modified in place)</param>
        /// <param name="volume">Volume multiplier (0.0-2.0, where 1.0 is original volume)</param>
        public static void AdjustVolume(float[] samples, float volume)
        {
            if (samples == null || samples.Length == 0) return;

            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] *= volume;
                samples[i] = Mathf.Clamp(samples[i], -1f, 1f);
            }
        }

        /// <summary>
        /// Generates a unique filename for the exported audio file using auto-incrementing numbers.
        /// </summary>
        /// <param name="folder">The target folder</param>
        /// <param name="originalFilename">The original audio filename</param>
        /// <returns>Full path to the unique filename</returns>
        public static string GenerateUniqueFilename(string folder, string originalFilename)
        {
            string basename = Path.GetFileNameWithoutExtension(originalFilename);
            int counter = 1;
            string targetPath;

            do
            {
                string filename = $"{basename}_{counter:D2}.wav";
                targetPath = Path.Combine(folder, filename);
                counter++;
            } while (File.Exists(targetPath));

            return targetPath;
        }

        /// <summary>
        /// Exports audio samples to a WAV file.
        /// </summary>
        /// <param name="outputPath">Output file path</param>
        /// <param name="samples">Audio samples (interleaved for multi-channel)</param>
        /// <param name="channels">Number of audio channels</param>
        /// <param name="frequency">Sample rate in Hz</param>
        /// <param name="use32BitFloat">Use 32-bit float format instead of 16-bit PCM</param>
        public static void ExportToWav(string outputPath, float[] samples, int channels, int frequency, bool use32BitFloat = false)
        {
            if (samples == null || samples.Length == 0)
            {
                throw new ArgumentException("No audio samples to export");
            }

            string directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (FileStream stream = new FileStream(outputPath, FileMode.Create))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                int bitsPerSample = use32BitFloat ? 32 : 16;
                int bytesPerSample = bitsPerSample / 8;
                int blockAlign = channels * bytesPerSample;
                int byteRate = frequency * blockAlign;
                int dataSize = samples.Length * bytesPerSample;

                // RIFF header
                writer.Write(new[] {'R', 'I', 'F', 'F'});
                writer.Write(36 + dataSize); // File size minus 8 bytes
                writer.Write(new[] {'W', 'A', 'V', 'E'});

                // fmt subchunk
                writer.Write(new[] {'f', 'm', 't', ' '});
                writer.Write(16); // Subchunk1 size (16 for PCM)
                writer.Write((short)(use32BitFloat ? 3 : 1)); // Audio format (1 = PCM, 3 = IEEE float)
                writer.Write((short)channels);
                writer.Write(frequency);
                writer.Write(byteRate);
                writer.Write((short)blockAlign);
                writer.Write((short)bitsPerSample);

                // data subchunk
                writer.Write(new[] {'d', 'a', 't', 'a'});
                writer.Write(dataSize);

                if (use32BitFloat)
                {
                    // 32-bit float
                    for (int i = 0; i < samples.Length; i++)
                    {
                        writer.Write(samples[i]);
                    }
                }
                else
                {
                    // 16-bit PCM
                    for (int i = 0; i < samples.Length; i++)
                    {
                        float sample = Mathf.Clamp(samples[i], -1f, 1f);
                        short intSample = (short)(sample * 32767f);
                        writer.Write(intSample);
                    }
                }
            }

            Debug.Log($"Audio exported to: {outputPath}");
        }

        /// <summary>
        /// Creates a new AudioClip from processed samples (for preview before export).
        /// Uses a temporary WAV file because Unity's AudioUtil.PlayPreviewClip doesn't work
        /// with dynamically created clips (AudioClip.Create + SetData).
        /// </summary>
        /// <param name="samples">Audio samples</param>
        /// <param name="channels">Number of channels</param>
        /// <param name="frequency">Sample rate</param>
        /// <param name="name">Name for the clip</param>
        /// <returns>New AudioClip with the processed audio</returns>
        public static async Task<AudioClip> CreateClipFromSamples(float[] samples, int channels, int frequency, string name = "ProcessedAudio")
        {
            if (samples == null || samples.Length == 0)
            {
                Debug.LogWarning("CreateClipFromSamples: No samples provided");
                return null;
            }

            if (channels <= 0)
            {
                Debug.LogWarning($"CreateClipFromSamples: Invalid channel count {channels}");
                return null;
            }

            if (frequency <= 0)
            {
                Debug.LogWarning($"CreateClipFromSamples: Invalid frequency {frequency}");
                return null;
            }

            int sampleCount = samples.Length / channels;
            if (sampleCount <= 0)
            {
                Debug.LogWarning($"CreateClipFromSamples: Invalid sample count {sampleCount}");
                return null;
            }

            // Write to temp WAV file and load via AudioManager
            string tempPath = Path.Combine(Application.temporaryCachePath, "AudioToolPreview.wav");
            try
            {
                ExportToWav(tempPath, samples, channels, frequency, false);
                return await AudioManager.LoadAudioFromFile(tempPath, false);
            }
            catch (Exception e)
            {
                Debug.LogError($"CreateClipFromSamples: Exception: {e.Message}");
            }

            return null;
        }

        /// <summary>
        /// Converts time in seconds to sample index.
        /// </summary>
        public static int TimeToSample(float time, int frequency)
        {
            return Mathf.RoundToInt(time * frequency);
        }

        /// <summary>
        /// Converts sample index to time in seconds.
        /// </summary>
        public static float SampleToTime(int sample, int frequency)
        {
            return (float)sample / frequency;
        }

        /// <summary>
        /// Gets all samples from an AudioClip.
        /// </summary>
        public static float[] GetSamples(AudioClip clip)
        {
            if (clip == null) return null;

            float[] samples = new float[clip.samples * clip.channels];
            clip.GetData(samples, 0);
            return samples;
        }
    }
}
