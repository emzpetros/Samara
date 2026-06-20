using ImpossibleRobert.Common;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AssetInventory
{
    public class AnimationPlayer : IDisposable
    {
        private Texture2D _animTexture;
        private List<Rect> _animFrames;
        private int _curAnimFrame;
        private float _nextAnimTime;
        private float _frameInterval;
        private Texture2D[] _allFrameTextures; // Pre-extracted frames for zero-allocation playback
        private readonly string _guid;
        private bool _disposed;
        private bool _loadedFromTexture; // True if loaded from texture directly (don't destroy on dispose)

        public string Guid => _guid;
        public bool IsLoaded => _allFrameTextures != null && _allFrameTextures.Length > 0;

        public AnimationPlayer(string guid)
        {
            _guid = guid;
            _curAnimFrame = 1;
            _nextAnimTime = 0f;
            _frameInterval = AI.Config.animationSpeed;
        }

        public static float ResolveFrameInterval(float defaultFrameInterval, float minimumVisibleDuration, int frameCount)
        {
            if (minimumVisibleDuration <= 0f || frameCount <= 0)
            {
                return defaultFrameInterval;
            }

            return Mathf.Max(defaultFrameInterval, minimumVisibleDuration / frameCount);
        }

        public async Task<bool> LoadAnimation(AssetInfo info, string previewFolder)
        {
            if (info == null) return false;

            string animPreviewFile = info.GetPreviewFile(previewFolder, true);
            if (!File.Exists(animPreviewFile)) return false;

            // Read frame grid from PNG metadata, fallback to config if not found (legacy files)
            int frameGrid = AI.Config.animationGrid;
            string metadataValue = ImageUtils.ReadPngMetadata(animPreviewFile, "AssetInventory:FrameGrid");
            if (!string.IsNullOrEmpty(metadataValue) && int.TryParse(metadataValue, out int parsedGrid))
            {
                frameGrid = parsedGrid;
            }

            _frameInterval = AI.Config.animationSpeed;
            string frameIntervalMetadata = ImageUtils.ReadPngMetadata(animPreviewFile, "AssetInventory:FrameInterval");
            if (!string.IsNullOrEmpty(frameIntervalMetadata) &&
                float.TryParse(frameIntervalMetadata, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedFrameInterval) &&
                parsedFrameInterval > 0f)
            {
                _frameInterval = parsedFrameInterval;
            }

            _animTexture = await AssetUtils.LoadLocalTexture(animPreviewFile, false, (AI.Config.upscalePreviews && !AI.Config.upscaleLossless) ? AI.Config.upscaleSize : 0);
            if (_animTexture == null) return false;

            _animFrames = AssetUtils.CreateUVs(frameGrid, frameGrid);
            ExtractAllFrames();
            
            // Release the sprite sheet texture now that frames are extracted (we own it)
            Object.DestroyImmediate(_animTexture);
            _animTexture = null;

            return true;
        }

        /// <summary>
        /// Load animation directly from an existing sprite sheet texture.
        /// The texture is NOT owned by this player and will NOT be destroyed on dispose.
        /// </summary>
        public bool LoadFromTexture(Texture2D spriteSheet, int frameGrid)
        {
            return LoadFromTexture(spriteSheet, frameGrid, AI.Config.animationSpeed);
        }

        public bool LoadFromTexture(Texture2D spriteSheet, int frameGrid, float frameInterval)
        {
            if (spriteSheet == null) return false;

            _animTexture = spriteSheet;
            _animFrames = AssetUtils.CreateUVs(frameGrid, frameGrid);
            _loadedFromTexture = true;
            _frameInterval = frameInterval > 0f ? frameInterval : AI.Config.animationSpeed;
            
            ExtractAllFrames();

            return true;
        }
        
        /// <summary>
        /// Pre-extract all frames from the sprite sheet to avoid per-frame allocations during playback.
        /// </summary>
        private void ExtractAllFrames()
        {
            _allFrameTextures = new Texture2D[_animFrames.Count];
            for (int i = 0; i < _animFrames.Count; i++)
            {
                _allFrameTextures[i] = AssetUtils.ExtractFrame(_animTexture, _animFrames[i]);
            }
            
            _curAnimFrame = 0;
            _nextAnimTime = Time.realtimeSinceStartup;
        }

        public Texture2D GetCurrentFrame()
        {
            if (!IsLoaded) return null;

            // Check if it's time to advance to the next frame
            if (Time.realtimeSinceStartup >= _nextAnimTime)
            {
                _nextAnimTime = Time.realtimeSinceStartup + _frameInterval;
                _curAnimFrame = (_curAnimFrame + 1) % _allFrameTextures.Length;
            }

            // Return the pre-extracted frame (zero allocation)
            return _allFrameTextures[_curAnimFrame];
        }

        public void Dispose()
        {
            if (_disposed) return;

            // Only destroy the sprite sheet texture if we loaded it from file (we own it)
            // If loaded from texture directly, the caller owns it
            if (_animTexture != null && !_loadedFromTexture)
            {
                Object.DestroyImmediate(_animTexture);
            }
            _animTexture = null;

            // Destroy all pre-extracted frame textures
            if (_allFrameTextures != null)
            {
                for (int i = 0; i < _allFrameTextures.Length; i++)
                {
                    if (_allFrameTextures[i] != null)
                    {
                        Object.DestroyImmediate(_allFrameTextures[i]);
                        _allFrameTextures[i] = null;
                    }
                }
                _allFrameTextures = null;
            }

            _animFrames = null;
            _disposed = true;
        }
    }
}
