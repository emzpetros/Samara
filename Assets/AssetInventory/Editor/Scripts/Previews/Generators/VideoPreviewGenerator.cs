using ImpossibleRobert.Common;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Video;
using System.Threading.Tasks;
using Unity.EditorCoroutines.Editor;
using Object = UnityEngine.Object;

namespace AssetInventory
{
    public static class VideoPreviewGenerator
    {
        private const float TIME_OUT = 7f;

        public static Task<Texture2D> Create(VideoClip videoClip, int size = PreviewManager.DEFAULT_PREVIEW_SIZE, int frameCount = 4, Action<VideoClip> onSuccess = null)
        {
            TaskCompletionSource<Texture2D> tcs = new TaskCompletionSource<Texture2D>();

            if (videoClip == null)
            {
                Debug.LogError("VideoClip is null.");
                tcs.SetResult(null);
                return tcs.Task;
            }

            // Create a new GameObject to hold the VideoPlayer
            GameObject tempGo = new GameObject("TempVideoPlayer");
            VideoPlayer videoPlayer = tempGo.AddComponent<VideoPlayer>();

            // Configure the VideoPlayer
            videoPlayer.renderMode = VideoRenderMode.APIOnly; // Use APIOnly render mode
            videoPlayer.source = VideoSource.VideoClip;
            videoPlayer.clip = videoClip;
            videoPlayer.playOnAwake = false;
            videoPlayer.isLooping = false;
            videoPlayer.skipOnDrop = false;
            videoPlayer.sendFrameReadyEvents = true;

            // Start the coroutine to handle the video processing
            EditorCoroutineUtility.StartCoroutineOwnerless(ProcessVideoPlayer(videoPlayer, tempGo, size, frameCount, tcs, onSuccess));

            return tcs.Task;
        }

        private static IEnumerator ProcessVideoPlayer(VideoPlayer videoPlayer, GameObject tempGo, int size, int frameCount, TaskCompletionSource<Texture2D> tcs, Action<VideoClip> onSuccess = null)
        {
            // Prepare the video and wait until it's prepared
            bool isPrepared = false;
            videoPlayer.prepareCompleted += (vp) => { isPrepared = true; };
            videoPlayer.Prepare();

            while (!isPrepared)
            {
                EditorApplication.QueuePlayerLoopUpdate();
                yield return null;
            }

            // Get video duration and calculate frame times
            double duration = videoPlayer.clip.length;
            List<double> frameTimes = new List<double>();
            int samplesToCapture = frameCount;
            bool isSingleFrameMode = (frameCount == 1);

            if (isSingleFrameMode)
            {
                // For single frame preview, sample multiple frames to find the best one
                samplesToCapture = 7;
                double[] samplePositions = { 0.10, 0.25, 0.40, 0.50, 0.60, 0.75, 0.90 }; // Percentages of video duration
                for (int i = 0; i < samplesToCapture; i++)
                {
                    double time = duration * samplePositions[i];
                    frameTimes.Add(time);
                }
            }
            else
            {
                // Multi-frame mode: use original evenly spaced logic
                for (int i = 0; i < frameCount; i++)
                {
                    double time = (duration * i) / Mathf.Max(frameCount - 1, 1); // Evenly spaced times
                    frameTimes.Add(time);
                }

                // Adjust first and last frames to ensure they are within bounds
                frameTimes[0] = Math.Max(frameTimes[0], 0);
                frameTimes[frameCount - 1] = Math.Min(frameTimes[frameCount - 1], duration - 0.1);
            }

            // List to hold captured frames
            List<Texture2D> frames = new List<Texture2D>();

            // Capture frames at specified times
            for (int i = 0; i < samplesToCapture; i++)
            {
                double time = frameTimes[i];

                // Seek to the target time
                bool isSeeking = true;
                bool frameReady = false;

                // Use stored delegate references to ensure proper removal (lambdas use reference equality)
                VideoPlayer.EventHandler seekHandler = (vp) => { isSeeking = false; };
                VideoPlayer.FrameReadyEventHandler frameHandler = (vp, frameIdx) => { frameReady = true; };

                videoPlayer.seekCompleted += seekHandler;
                videoPlayer.frameReady += frameHandler;

                // videoPlayer.Stop();  // required on Mac
                videoPlayer.time = time;
                videoPlayer.Play();

                // If time is 0, manually set isSeeking to false since event will not trigger on Mac
                // if (time == 0) isSeeking = false;

                // Wait for seek to complete
                while (isSeeking) // Mac: && !videoPlayer.isPaused) 
                {
                    EditorApplication.QueuePlayerLoopUpdate();
                    yield return null;
                }

                // Wait for frame to be ready
                float endTime = Time.realtimeSinceStartup + TIME_OUT;
                while (!frameReady)
                {
                    EditorApplication.QueuePlayerLoopUpdate();
                    yield return null;

                    if (Time.realtimeSinceStartup > endTime)
                    {
                        Debug.LogWarning($"Timeout waiting for frame {i}. Final image might not resemble the correct frame.");
                        break;
                    }
                }

                // Allow GPU to complete texture blit after frameReady event
                yield return null;

                // Extract the texture from VideoPlayer
                Texture texture = videoPlayer.texture;
                if (texture != null)
                {
                    int originalWidth = texture.width;
                    int originalHeight = texture.height;

                    // Handle invalid dimensions
                    if (originalWidth == 0 || originalHeight == 0)
                    {
                        Object.DestroyImmediate(texture);
                        Debug.LogError("VideoPlayer texture has invalid dimensions.");
                        videoPlayer.frameReady -= frameHandler;
                        videoPlayer.seekCompleted -= seekHandler;
                        continue;
                    }

                    // Calculate the scale factor to maintain aspect ratio
                    float widthScale = (float)size / originalWidth;
                    float heightScale = (float)size / originalHeight;
                    float scale = Mathf.Min(widthScale, heightScale, 1.0f); // Ensure scale is not greater than 1

                    int newWidth = Mathf.RoundToInt(originalWidth * scale);
                    int newHeight = Mathf.RoundToInt(originalHeight * scale);

                    // Create a RenderTexture with the new dimensions
                    RenderTexture renderTexture = RenderTexture.GetTemporary(newWidth, newHeight, 0, RenderTextureFormat.ARGB32);
                    RenderTexture.active = renderTexture;

                    // Copy the VideoPlayer texture to the RenderTexture with scaling
                    Graphics.Blit(texture, renderTexture);

                    // Read the pixels from the RenderTexture into a Texture2D
                    Texture2D frameTexture = new Texture2D(newWidth, newHeight, TextureFormat.RGBA32, false);
                    frameTexture.ReadPixels(new Rect(0, 0, newWidth, newHeight), 0, 0);
                    frameTexture.Apply();

                    // Add the frame to the list
                    frames.Add(frameTexture);

                    // Cleanup
                    // Object.DestroyImmediate(texture); // TODO: will lead to Destroying object "TempBuffer 22 1920x1080" is not allowed at this time. 
                    RenderTexture.active = null;
                    RenderTexture.ReleaseTemporary(renderTexture);
                }
                else
                {
                    Debug.LogError("VideoPlayer texture is null.");
                }

                // Remove event handlers (single cleanup point)
                videoPlayer.frameReady -= frameHandler;
                videoPlayer.seekCompleted -= seekHandler;

                videoPlayer.Pause();

                // Yield to ensure the Editor updates
                EditorApplication.QueuePlayerLoopUpdate();
                yield return null;
            }

            // For single frame mode, analyze and select the best frame
            if (isSingleFrameMode && frames.Count > 0)
            {
                float bestScore = -1f;
                int bestFrameIndex = 0;

                for (int i = 0; i < frames.Count; i++)
                {
                    float score = ImageUtils.AnalyzeFrameQuality(frames[i]);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestFrameIndex = i;
                    }
                }

                // Keep only the best frame
                Texture2D bestFrame = frames[bestFrameIndex];
                
                // Cleanup other frames
                for (int i = 0; i < frames.Count; i++)
                {
                    if (i != bestFrameIndex)
                    {
                        Object.DestroyImmediate(frames[i]);
                    }
                }
                
                frames.Clear();
                frames.Add(bestFrame);
            }

            // Assemble frames into a texture sheet
            if (frames.Count > 0)
            {
                Texture2D textureSheet = ImageUtils.AssembleTextureSheet(frames);

                onSuccess?.Invoke(videoPlayer.clip);

                // Cleanup
                Cleanup(videoPlayer, tempGo, frames);

                tcs.SetResult(textureSheet);
            }
            else
            {
                // Cleanup
                Cleanup(videoPlayer, tempGo, frames);
                tcs.SetResult(null);
                Debug.LogError("No frames were captured.");
            }
        }

        private static void Cleanup(VideoPlayer videoPlayer, GameObject tempGo, List<Texture2D> frames)
        {
            if (videoPlayer != null)
            {
                videoPlayer.Stop();
                Object.DestroyImmediate(videoPlayer);
            }

            if (tempGo != null)
            {
                Object.DestroyImmediate(tempGo);
            }

            if (frames != null)
            {
                for (int i = 0; i < frames.Count; i++)
                {
                    Object.DestroyImmediate(frames[i]);
                }
            }
        }

        public static Task<Texture2D> Create(string file, int size = PreviewManager.DEFAULT_PREVIEW_SIZE, int frameCount = 4, Action<VideoClip> onSuccess = null)
        {
            VideoClip clip = AssetDatabase.LoadAssetAtPath<VideoClip>(file);
            if (clip == null)
            {
                Debug.LogError($"Failed to load video clip from: {file}");
                return Task.FromResult<Texture2D>(null);
            }

            return Create(clip, size, frameCount, onSuccess);
        }
    }
}
