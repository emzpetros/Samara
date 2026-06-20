using ImpossibleRobert.Common;
using System;
#if UNITY_EDITOR_WIN && NET_4_6
using System.Drawing;
#else
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
#endif
using UnityEditor;
using UnityEngine;

namespace AssetInventory
{
    /// <summary>
    /// Validation and error detection utilities for preview generation.
    /// Consolidates validation logic from PreviewManager and CustomPrefabPreviewGenerator.
    /// </summary>
    public static class PreviewValidation
    {
        private const byte AlphaVisibleThreshold = 8;
        private const int BackgroundTolerance = 12;
        private const int MinimumPinkVisiblePixels = 4;
        private const float PinkVisibleMajorityThreshold = 0.5f;

        private static ulong _textureIconHash;
        private static ulong _audioIconHash;

#if UNITY_EDITOR_WIN && NET_4_6
        public static bool IsErrorShader(Bitmap image)
        {
            if (image == null) return false;

            ColorRange background = FindBackgroundRange(image);
            int visiblePixels = 0;
            int pinkPixels = 0;

            int width = image.Width;
            int height = image.Height;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    System.Drawing.Color color = image.GetPixel(x, y);
                    if (IsBackground(color.R, color.G, color.B, color.A, background)) continue;

                    visiblePixels++;
                    if (ImageUtils.IsMagentaPixel(color.R, color.G, color.B)) pinkPixels++;
                }
            }

            return IsPinkErrorForeground(visiblePixels, pinkPixels);
        }
        
        public static bool IsDefaultIcon(Bitmap image)
        {
            if (_textureIconHash == 0)
            {
                Bitmap textureIcon = ((Texture2D)EditorGUIUtility.IconContent("d_texture icon").image).MakeReadable().ToImage();
                _textureIconHash = ImageUtils.ComputePerceptualHash(textureIcon);
            }
            if (_audioIconHash == 0)
            {
                Bitmap audioIcon = ((Texture2D)EditorGUIUtility.IconContent("audioclip icon").image).MakeReadable().ToImage();
                _audioIconHash = ImageUtils.ComputePerceptualHash(audioIcon);
            }
#else
        public static bool IsErrorShader(Image<Rgba32> image)
        {
            if (image == null) return false;

            ColorRange background = FindBackgroundRange(image);
            int visiblePixels = 0;
            int pinkPixels = 0;

            int width = image.Width;
            int height = image.Height;
            image.ProcessPixelRows(pixelAccessor =>
            {
                for (int y = 0; y < height; y++)
                {
                    Span<Rgba32> row = pixelAccessor.GetRowSpan(y);
                    for (int x = 0; x < width; x++)
                    {
                        Rgba32 color = row[x];
                        if (IsBackground(color.R, color.G, color.B, color.A, background)) continue;

                        visiblePixels++;
                        if (ImageUtils.IsMagentaPixel(color.R, color.G, color.B)) pinkPixels++;
                    }
                }
            });

            return IsPinkErrorForeground(visiblePixels, pinkPixels);
        }
        
        public static bool IsDefaultIcon(Image<Rgba32> image)
        {
            if (_textureIconHash == 0)
            {
                Image<Rgba32> textureIcon = ((Texture2D)EditorGUIUtility.IconContent("d_texture icon").image).MakeReadable().ToImage();
                _textureIconHash = ImageUtils.ComputePerceptualHash(textureIcon);
            }
            if (_audioIconHash == 0)
            {
                Image<Rgba32> audioIcon = ((Texture2D)EditorGUIUtility.IconContent("audioclip icon").image).MakeReadable().ToImage();
                _audioIconHash = ImageUtils.ComputePerceptualHash(audioIcon);
            }
#endif
            return ImageUtils.HasDominantColor(image, new UnityEngine.Color(128f / 255f, 216f / 255f, 255f / 255f))
                || ImageUtils.AreSimilar(image, _textureIconHash)
                || ImageUtils.AreSimilar(image, _audioIconHash);
        }

        private static bool IsPinkErrorForeground(int visiblePixels, int pinkPixels)
        {
            if (visiblePixels <= 0 || pinkPixels < MinimumPinkVisiblePixels) return false;
            if (pinkPixels == visiblePixels) return true;

            return (float)pinkPixels / visiblePixels > PinkVisibleMajorityThreshold;
        }

        private static bool IsBackground(byte r, byte g, byte b, byte a, ColorRange background)
        {
            if (a <= AlphaVisibleThreshold) return true;
            return background.HasSamples && background.Contains(r, g, b, BackgroundTolerance);
        }

#if UNITY_EDITOR_WIN && NET_4_6
        private static ColorRange FindBackgroundRange(Bitmap image)
        {
            ColorRange range = new ColorRange();
            int width = image.Width;
            int height = image.Height;
            if (width == 0 || height == 0) return range;

            for (int x = 0; x < width; x++)
            {
                IncludeBackgroundSample(image.GetPixel(x, 0), ref range);
                if (height > 1) IncludeBackgroundSample(image.GetPixel(x, height - 1), ref range);
            }
            for (int y = 1; y < height - 1; y++)
            {
                IncludeBackgroundSample(image.GetPixel(0, y), ref range);
                if (width > 1) IncludeBackgroundSample(image.GetPixel(width - 1, y), ref range);
            }

            return range;
        }

        private static void IncludeBackgroundSample(System.Drawing.Color color, ref ColorRange range)
        {
            if (color.A <= AlphaVisibleThreshold || ImageUtils.IsMagentaPixel(color.R, color.G, color.B)) return;
            range.Include(color.R, color.G, color.B);
        }
#else
        private static ColorRange FindBackgroundRange(Image<Rgba32> image)
        {
            ColorRange range = new ColorRange();
            int width = image.Width;
            int height = image.Height;
            if (width == 0 || height == 0) return range;

            for (int x = 0; x < width; x++)
            {
                IncludeBackgroundSample(image[x, 0], ref range);
                if (height > 1) IncludeBackgroundSample(image[x, height - 1], ref range);
            }
            for (int y = 1; y < height - 1; y++)
            {
                IncludeBackgroundSample(image[0, y], ref range);
                if (width > 1) IncludeBackgroundSample(image[width - 1, y], ref range);
            }

            return range;
        }

        private static void IncludeBackgroundSample(Rgba32 color, ref ColorRange range)
        {
            if (color.A <= AlphaVisibleThreshold || ImageUtils.IsMagentaPixel(color.R, color.G, color.B)) return;
            range.Include(color.R, color.G, color.B);
        }
#endif

        private struct ColorRange
        {
            private byte _minR;
            private byte _maxR;
            private byte _minG;
            private byte _maxG;
            private byte _minB;
            private byte _maxB;

            public bool HasSamples { get; private set; }

            public void Include(byte r, byte g, byte b)
            {
                if (!HasSamples)
                {
                    _minR = r;
                    _maxR = r;
                    _minG = g;
                    _maxG = g;
                    _minB = b;
                    _maxB = b;
                    HasSamples = true;
                    return;
                }

                if (r < _minR) _minR = r;
                if (r > _maxR) _maxR = r;
                if (g < _minG) _minG = g;
                if (g > _maxG) _maxG = g;
                if (b < _minB) _minB = b;
                if (b > _maxB) _maxB = b;
            }

            public bool Contains(byte r, byte g, byte b, int tolerance)
            {
                return r >= _minR - tolerance && r <= _maxR + tolerance
                    && g >= _minG - tolerance && g <= _maxG + tolerance
                    && b >= _minB - tolerance && b <= _maxB + tolerance;
            }
        }
    }
}
