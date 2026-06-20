#if !UNITY_EDITOR_WIN || !NET_4_6
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Color = UnityEngine.Color;

namespace ImpossibleRobert.Common
{
    public static partial class ImageUtils
    {
        /// <summary>
        /// Resizes an image file. On Mac, tries native CoreGraphics first for hardware acceleration,
        /// falls back to ImageSharp on failure.
        /// </summary>
        /// <remarks>
        /// Windows native WIC support is disabled due to COM interop complexities.
        /// Use .NET 4.6 API compatibility level for System.Drawing support instead.
        /// </remarks>
        public static bool ResizeImage(string originalFile, string outputFile, int maxSize, bool scaleBeyondSize = true, string typeOverride = null)
        {
#if UNITY_EDITOR_OSX
            // Try native CoreGraphics first for hardware acceleration
            if (TryResizeImageNative(originalFile, outputFile, maxSize, scaleBeyondSize, typeOverride))
            {
                return true;
            }
            // Fall through to ImageSharp on failure
#endif
            return ResizeImageSharp(originalFile, outputFile, maxSize, scaleBeyondSize, typeOverride);
        }

        /// <summary>
        /// Computes perceptual hash from a file path. On Mac, tries native CoreGraphics first,
        /// falls back to ImageSharp on failure.
        /// </summary>
        public static ulong ComputePerceptualHash(string filePath, int hashSize = 8)
        {
#if UNITY_EDITOR_OSX
            if (TryComputePerceptualHashNative(filePath, out ulong hash, hashSize))
            {
                return hash;
            }
            // Fall through to ImageSharp on failure
#endif
            using Image<Rgba32> image = Image.Load<Rgba32>(filePath);
            return ComputePerceptualHash(image, hashSize);
        }

        public static bool HasDominantColor(Image<Rgba32> image, Color target, float marginPercent = 0.02f, float coverageThreshold = 0.3f)
        {
            int width = image.Width;
            int height = image.Height;
            int total = width * height;
            int matchCount = 0;
            int marginR = (int)Math.Ceiling(target.r * marginPercent);
            int marginG = (int)Math.Ceiling(target.g * marginPercent);
            int marginB = (int)Math.Ceiling(target.b * marginPercent);

            image.ProcessPixelRows(pixelAccessor =>
            {
                for (int y = 0; y < height; y++)
                {
                    Span<Rgba32> row = pixelAccessor.GetRowSpan(y);
                    for (int x = 0; x < width; x++)
                    {
                        Rgba32 p = row[x];
                        if (Math.Abs(p.R - target.r) <= marginR &&
                            Math.Abs(p.G - target.g) <= marginG &&
                            Math.Abs(p.B - target.b) <= marginB)
                        {
                            matchCount++;
                        }
                    }
                }
            });

            return matchCount > total * coverageThreshold;
        }

        public static bool IsErrorPreview(Image<Rgba32> image, float requiredRatio = 0.06f)
        {
            int width = image.Width;
            int height = image.Height;
            int total = width * height;
            int pinkCount = 0;

            image.ProcessPixelRows(pixelAccessor =>
            {
                for (int y = 0; y < height; y++)
                {
                    Span<Rgba32> row = pixelAccessor.GetRowSpan(y);
                    for (int x = 0; x < width; x++)
                    {
                        Rgba32 c = row[x];
                        if (IsMagentaPixel(c.R, c.G, c.B))
                        {
                            pinkCount++;
                        }
                    }
                }
            });

            return ((float)pinkCount / total) >= requiredRatio;
        }

        public static bool IsLowDiversityPinkPreview(Image<Rgba32> image, int maxDistinctColors = 20)
        {
            int width = image.Width;
            int height = image.Height;
            HashSet<(byte, byte, byte)> buckets = new HashSet<(byte, byte, byte)>();
            bool hasPink = false;

            image.ProcessPixelRows(pixelAccessor =>
            {
                for (int y = 0; y < height; y++)
                {
                    Span<Rgba32> row = pixelAccessor.GetRowSpan(y);
                    for (int x = 0; x < width; x++)
                    {
                        Rgba32 c = row[x];
                        buckets.Add(((byte)(c.R >> 3), (byte)(c.G >> 3), (byte)(c.B >> 3)));
                        if (!hasPink && IsMagentaPixel(c.R, c.G, c.B))
                        {
                            hasPink = true;
                        }
                    }
                }
            });

            return hasPink && buckets.Count <= maxDistinctColors;
        }

        public static ulong ComputePerceptualHash(Image<Rgba32> image, int hashSize = 8)
        {
            using Image<Rgba32> clone = image.Clone(ctx => ctx.Resize(hashSize, hashSize).Grayscale());
            ulong hash = 0UL;
            double sum = 0.0;
            double[] pixels = new double[hashSize * hashSize];
            int idx = 0;
            for (int y = 0; y < hashSize; y++)
            {
                for (int x = 0; x < hashSize; x++)
                {
                    double l = clone[x, y].R;
                    pixels[idx++] = l;
                    sum += l;
                }
            }
            double avg = sum / pixels.Length;
            for (int i = 0; i < pixels.Length; i++)
            {
                if (pixels[i] > avg) hash |= 1UL << i;
            }
            return hash;
        }

        public static bool AreSimilar(string fileA, string fileB, double maxFractionDifferent = 0.15)
        {
            using Image<Rgba32> imgA = Image.Load<Rgba32>(fileA);
            using Image<Rgba32> imgB = Image.Load<Rgba32>(fileB);

            return AreSimilar(imgA, imgB, maxFractionDifferent);
        }

        public static bool AreSimilar(Image<Rgba32> imgA, Image<Rgba32> imgB, double maxFractionDifferent = 0.15)
        {
            ulong hashA = ComputePerceptualHash(imgA);
            ulong hashB = ComputePerceptualHash(imgB);
            int dist = HammingDistance(hashA, hashB);
            double fraction = (double)dist / (8 * 8);
            return fraction <= maxFractionDifferent;
        }

        public static bool AreSimilar(Image<Rgba32> imgA, ulong hashB, double maxFractionDifferent = 0.15)
        {
            ulong hashA = ComputePerceptualHash(imgA);
            int dist = HammingDistance(hashA, hashB);
            double fraction = (double)dist / (8 * 8);
            return fraction <= maxFractionDifferent;
        }

        public static Image<Rgba32> ToImage(this Texture2D tex)
        {
            byte[] pngData = tex.EncodeToPNG();
            Image<Rgba32> img = Image.Load<Rgba32>(pngData);
            return img;
        }

        /// <summary>
        /// ImageSharp implementation of image resizing. Used as fallback when native fails.
        /// </summary>
        internal static bool ResizeImageSharp(string originalFile, string outputFile, int maxSize, bool scaleBeyondSize = true, string typeOverride = null)
        {
            Image originalImage = null;
            try
            {
                // Use shared helper for dimension calculation and common logic
                ResizeParams? paramsResult = CalculateResizeParams(
                    originalFile, outputFile, maxSize, scaleBeyondSize, typeOverride,
                    () =>
                    {
                        // Platform-specific fallback: load dimensions via ImageSharp
                        originalImage = Image.Load(IOUtils.ToLongPath(originalFile));
                        return (originalImage.Width, originalImage.Height);
                    },
                    out bool fileCopied);

                if (fileCopied) return true;
                if (paramsResult == null) return false;

                ResizeParams resizeParams = paramsResult.Value;

                // Load image if not already loaded
                originalImage ??= Image.Load(IOUtils.ToLongPath(originalFile));

                originalImage.Mutate(x => x.Resize(resizeParams.NewWidth, resizeParams.NewHeight));
                originalImage.SaveAsPng(IOUtils.ToLongPath(outputFile));
            }
            catch (Exception e)
            {
                if (LogImageOperations)
                {
                    Debug.LogWarning($"Could not resize image '{originalFile}': {e.Message}");
                }
                return false;
            }
            finally
            {
                originalImage?.Dispose();
            }
            return true;
        }
    }
}
#endif
