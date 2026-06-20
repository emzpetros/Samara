// adaptations from
// https://stackoverflow.com/questions/30103425/find-dominant-color-in-an-image

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;
#if UNITY_EDITOR_WIN && NET_4_6
using System.Drawing;
#else
using SixLabors.ImageSharp;
#endif
using Color = UnityEngine.Color;

namespace ImpossibleRobert.Common
{
    // Image utility methods for texture manipulation
    public static partial class ImageUtils
    {
        /// <summary>
        /// Set to true to enable verbose logging for image operations.
        /// </summary>
        public static bool LogImageOperations { get; set; } = false;

        public static readonly List<string> SYSTEM_IMAGE_TYPES = new List<string>
        {
            "jpg", "jpeg", "png", "bmp", "gif", "tiff", "tif"
#if !UNITY_EDITOR_WIN
            , "tga", "webp"
#endif
        };

        // palette adapted from http://eastfarthing.com/blog/2016-05-06-palette/
        private static Color[] PALETTE_32 =
        {
            FromHex("#d6a090"),
            FromHex("#fe3b1e"),
            FromHex("#a12c32"),
            FromHex("#fa2f7a"),
            FromHex("#fb9fda"),
            FromHex("#e61cf7"),
            FromHex("#992f7c"),
            FromHex("#47011f"),
            FromHex("#051155"),
            FromHex("#4f02ec"),
            FromHex("#2d69cb"),
            FromHex("#00a6ee"),
            FromHex("#6febff"),
            FromHex("#08a29a"),
            FromHex("#2a666a"),
            FromHex("#063619"),
            FromHex("#000000"),
            FromHex("#4a4957"),
            FromHex("#8e7ba4"),
            FromHex("#b7c0ff"),
            FromHex("#ffffff"),
            FromHex("#acbe9c"),
            FromHex("#827c70"),
            FromHex("#5a3b1c"),
            FromHex("#ae6507"),
            FromHex("#f7aa30"),
            FromHex("#f4ea5c"),
            FromHex("#9b9500"),
            FromHex("#566204"),
            FromHex("#11963b"),
            FromHex("#51e113"),
            FromHex("#08fdcc")
        };

        public static Texture2D Downscale(this Texture2D source, int size)
        {
            int targetX = size;
            int targetY = size;

            if (source.width > source.height) targetY = (int)(targetX * ((float)source.height / source.width));
            if (source.height > source.width) targetX = (int)(targetY * ((float)source.width / source.height));

            return source.Downscale(targetX, targetY);
        }

        public static Texture2D Downscale(this Texture2D source, int targetWidth, int targetHeight)
        {
            // Use trilinear filtering for high-quality downsampling
            FilterMode originalFilterMode = source.filterMode;
            source.filterMode = FilterMode.Trilinear;

            RenderTexture originalRT = RenderTexture.active;
            RenderTexture currentRT = null;
            RenderTexture previousRT = null;

            try
            {
                // For large downscaling ratios, use multi-pass downsampling for better quality
                // Each pass reduces size by max 2x for smoother results
                int currentWidth = source.width;
                int currentHeight = source.height;

                // Create initial RT from source texture
                currentRT = RenderTexture.GetTemporary(currentWidth, currentHeight, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
                currentRT.filterMode = FilterMode.Bilinear;
                UnityEngine.Graphics.Blit(source, currentRT);

                // Progressive downsampling in steps of 2x
                while (currentWidth > targetWidth * 2 || currentHeight > targetHeight * 2)
                {
                    currentWidth = Mathf.Max(targetWidth, currentWidth / 2);
                    currentHeight = Mathf.Max(targetHeight, currentHeight / 2);

                    previousRT = currentRT;
                    currentRT = RenderTexture.GetTemporary(currentWidth, currentHeight, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
                    currentRT.filterMode = FilterMode.Bilinear;

                    RenderTexture.active = currentRT;
                    UnityEngine.Graphics.Blit(previousRT, currentRT);

                    RenderTexture.ReleaseTemporary(previousRT);
                    previousRT = null;
                }

                // Final pass to exact target size
                if (currentWidth != targetWidth || currentHeight != targetHeight)
                {
                    previousRT = currentRT;
                    currentRT = RenderTexture.GetTemporary(targetWidth, targetHeight, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
                    currentRT.filterMode = FilterMode.Bilinear;

                    RenderTexture.active = currentRT;
                    UnityEngine.Graphics.Blit(previousRT, currentRT);

                    RenderTexture.ReleaseTemporary(previousRT);
                    previousRT = null;
                }

                // Read final result into Texture2D
                Texture2D result = new Texture2D(targetWidth, targetHeight, TextureFormat.RGBA32, false);
                RenderTexture.active = currentRT;
                result.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
                result.filterMode = FilterMode.Trilinear;
                result.wrapMode = TextureWrapMode.Clamp;
                result.hideFlags = source.hideFlags;
                result.Apply();

                return result;
            }
            finally
            {
                if (currentRT != null) RenderTexture.ReleaseTemporary(currentRT);
                if (previousRT != null) RenderTexture.ReleaseTemporary(previousRT);
                RenderTexture.active = originalRT;
                source.filterMode = originalFilterMode; // Restore original filter mode
            }
        }

        public static Texture2D MakeReadable(this Texture2D src)
        {
            RenderTexture rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.Default, RenderTextureReadWrite.Linear);
            UnityEngine.Graphics.Blit(src, rt);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            Texture2D tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            return tex;
        }

        public static Texture2D WithRoundedCorners(this Texture2D src, int radius = 10)
        {
            if (src == null) return null;
            if (radius <= 0) return src;

            Texture2D readable = src;
            bool createdTemporary = false;
            
            if (!src.isReadable)
            {
                readable = src.MakeReadable();
                createdTemporary = true;
            }

            try
            {
                int width = readable.width;
                int height = readable.height;
                int r = Mathf.Clamp(radius, 1, Mathf.Min(width, height) / 2);
                int r2 = r * r;

                Texture2D result = new Texture2D(width, height, TextureFormat.RGBA32, false);
                result.filterMode = readable.filterMode;
                result.wrapMode = TextureWrapMode.Clamp;
                result.hideFlags = readable.hideFlags;

                Color32[] pixels = readable.GetPixels32();
                Color32[] outPixels = new Color32[pixels.Length];

                // copy all first
                Array.Copy(pixels, outPixels, pixels.Length);

                // helper to clear outside a quarter circle
                void ClearCorner(int startX, int startY, int cx, int cy)
                {
                    for (int y = 0; y < r; y++)
                    {
                        int py = startY + y;
                        int dy = py - cy;
                        for (int x = 0; x < r; x++)
                        {
                            int px = startX + x;
                            int dx = px - cx;
                            if (dx * dx + dy * dy > r2)
                            {
                                int idx = py * width + px;
                                Color32 c = outPixels[idx];
                                c.a = 0;
                                outPixels[idx] = c;
                            }
                        }
                    }
                }

                // top-left
                ClearCorner(0, height - r, r - 1, height - r);
                // top-right
                ClearCorner(width - r, height - r, width - r, height - r);
                // bottom-left
                ClearCorner(0, 0, r - 1, r - 1);
                // bottom-right
                ClearCorner(width - r, 0, width - r, r - 1);

                result.SetPixels32(outPixels);
                result.Apply();

                return result;
            }
            finally
            {
                // Dispose of the temporary texture if we created one
                if (createdTemporary)
                {
                    UnityEngine.Object.DestroyImmediate(readable);
                }
            }
        }

        public static int HammingDistance(ulong a, ulong b)
        {
            ulong v = a ^ b;
            int count = 0;
            while (v != 0)
            {
                count++;
                v &= v - 1;
            }
            return count;
        }

        /// <summary>
        /// Detects Unity error-shader magenta pixels. Handles both sRGB (bright ~255,0,255)
        /// and linear-space / tonemapped variants (darker ~160,0,128) that appear across
        /// different Unity versions and render pipelines.
        /// </summary>
        public static bool IsMagentaPixel(byte r, byte g, byte b)
        {
            // R and B must both be present; G must be very low.
            // Thresholds chosen to catch linear-space dark magenta while
            // avoiding false positives on legitimate dark-purple content.
            return r >= 80 && b >= 64
                && g < 50
                && g < r * 0.35f
                && g < b * 0.5f;
        }

        /// <summary>
        /// Parameters for image resize operations, calculated by CalculateResizeParams.
        /// </summary>
        internal struct ResizeParams
        {
            public int OriginalWidth;
            public int OriginalHeight;
            public int NewWidth;
            public int NewHeight;
            public string Extension;
        }

        /// <summary>
        /// Calculates resize parameters for image resizing operations.
        /// Covers dimension detection, ratio calculation, scaleBeyondSize logic, and directory creation.
        /// Returns null if no resize is needed (file was copied directly or dimensions couldn't be determined).
        /// </summary>
        /// <param name="originalFile">Path to the original image file</param>
        /// <param name="outputFile">Path for the output file</param>
        /// <param name="maxSize">Maximum dimension size</param>
        /// <param name="scaleBeyondSize">Whether to scale beyond original size</param>
        /// <param name="typeOverride">Optional file type override</param>
        /// <param name="loadDimensionsFallback">Platform-specific fallback to load dimensions when header parsing fails. Return null to indicate failure.</param>
        /// <param name="fileCopied">Set to true if the file was copied directly (no resize needed)</param>
        /// <returns>ResizeParams if resize is needed, null otherwise</returns>
        internal static ResizeParams? CalculateResizeParams(
            string originalFile,
            string outputFile,
            int maxSize,
            bool scaleBeyondSize,
            string typeOverride,
            Func<(int width, int height)?> loadDimensionsFallback,
            out bool fileCopied)
        {
            fileCopied = false;

            // Determine file type from override or extension
            string ext = !string.IsNullOrEmpty(typeOverride) ? "." + typeOverride : Path.GetExtension(originalFile);

            // Try to get dimensions from header first (fast path)
            Tuple<int, int> dimensions = GetDimensions(originalFile, true, ext);
            int originalWidth, originalHeight;

            if (dimensions != null)
            {
                originalWidth = dimensions.Item1;
                originalHeight = dimensions.Item2;
            }
            else
            {
                // Use platform-specific fallback to load dimensions
                (int width, int height)? fallbackDimensions = loadDimensionsFallback?.Invoke();
                if (fallbackDimensions == null)
                {
                    return null;
                }
                originalWidth = fallbackDimensions.Value.width;
                originalHeight = fallbackDimensions.Value.height;
            }

            // Calculate the scaling
            double ratioX = (double)maxSize / originalWidth;
            double ratioY = (double)maxSize / originalHeight;
            double ratio = Math.Min(ratioX, ratioY);

            int newWidth = Math.Max(1, (int)(originalWidth * ratio));
            int newHeight = Math.Max(1, (int)(originalHeight * ratio));

            if (!scaleBeyondSize && (newWidth > originalWidth || newHeight > originalHeight))
            {
                newWidth = originalWidth;
                newHeight = originalHeight;
            }

            // Ensure output directory exists
            string dir = Path.GetDirectoryName(outputFile);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // If no resize needed and source is PNG, just copy the file
            bool isPng = ext.Equals(".png", StringComparison.OrdinalIgnoreCase);
            if (isPng && newWidth == originalWidth && newHeight == originalHeight)
            {
                File.Copy(IOUtils.ToLongPath(originalFile), IOUtils.ToLongPath(outputFile), true);
                fileCopied = true;
                return null;
            }

            return new ResizeParams
            {
                OriginalWidth = originalWidth,
                OriginalHeight = originalHeight,
                NewWidth = newWidth,
                NewHeight = newHeight,
                Extension = ext
            };
        }

        public static Color FromHex(string hex)
        {
            if (ColorUtility.TryParseHtmlString(hex, out Color result))
            {
                return result;
            }
            return Color.clear;
        }

        public static Color GetNearestColor(Color inputColor)
        {
            float inputRed = inputColor.r;
            float inputGreen = inputColor.g;
            float inputBlue = inputColor.b;

            Color nearestColor = Color.clear;
            float minDistanceSq = float.MaxValue;
            
            foreach (Color color in PALETTE_32)
            {
                // Compute squared Euclidean distance (no sqrt needed for comparison)
                float dr = color.r - inputRed;
                float dg = color.g - inputGreen;
                float db = color.b - inputBlue;
                float distanceSq = dr * dr + dg * dg + db * db;
                
                if (distanceSq < 0.0001f) return color; // essentially zero distance
                if (distanceSq < minDistanceSq)
                {
                    minDistanceSq = distanceSq;
                    nearestColor = color;
                }
            }
            return nearestColor;
        }

        public static float GetHue(Texture2D source)
        {
            if (source == null) return -1f;

            Color32[] texColors = source.GetPixels32();
            int total = texColors.Length;
            int r = 0;
            int g = 0;
            int b = 0;
            int count = 0;
            byte alphaThreshold = (byte)(0.15f * 255f);

            for (int i = 0; i < total; i++)
            {
                Color32 pixelColor = texColors[i];
                if (pixelColor.a > alphaThreshold)
                {
                    count++;
                    r += pixelColor.r;
                    g += pixelColor.g;
                    b += pixelColor.b;
                }
            }
            if (count == 0) return 0f;

            float inverseCount255 = 1f / (count * 255f);
            float avgR = r * inverseCount255;
            float avgG = g * inverseCount255;
            float avgB = b * inverseCount255;

            return RGBToHue(avgR, avgG, avgB);
        }

        public static float ToHue(this Color color) => RGBToHue(color.r, color.g, color.b);

        // adapted from https://stackoverflow.com/questions/23090019/fastest-formula-to-get-hue-from-rgb
        public static float RGBToHue(float r, float g, float b)
        {
            float min = Mathf.Min(Mathf.Min(r, g), b);
            float max = Mathf.Max(Mathf.Max(r, g), b);
            float delta = max - min;
            if (delta == 0) return 0;

            float hue = 0;
            if (r == max)
            {
                hue = (g - b) / delta;
            }
            else if (g == max)
            {
                hue = (b - r) / delta + 2f;
            }
            else if (b == max)
            {
                hue = (r - g) / delta + 4f;
            }
            hue *= 60f;

            if (hue < 0.0f) hue += 360f;

            return hue;
        }

        public static Texture FillTexture(this Texture2D texture, Color color)
        {
            Color32 color32 = color;
            int pixelCount = texture.width * texture.height;
            Color32[] pixels = new Color32[pixelCount];

            for (int i = 0; i < pixelCount; i++)
            {
                pixels[i] = color32;
            }

            texture.SetPixels32(pixels);
            texture.Apply();

            return texture;
        }

        /// <summary>
        /// Analyzes a frame's quality based on luminance variance and brightness.
        /// Used for selecting the best frame from multiple captures.
        /// Higher scores indicate frames with more visual content and appropriate brightness.
        /// </summary>
        /// <param name="frame">The texture to analyze</param>
        /// <returns>Quality score (higher is better). Returns 0 for null/empty frames.</returns>
        public static float AnalyzeFrameQuality(Texture2D frame)
        {
            if (frame == null) return 0f;

            Color[] pixels = frame.GetPixels();
            if (pixels.Length == 0) return 0f;

            // Calculate average brightness (luminance)
            float totalBrightness = 0f;
            float[] luminances = new float[pixels.Length];

            for (int i = 0; i < pixels.Length; i++)
            {
                // Calculate luminance using standard weights (ITU-R BT.709)
                float luminance = 0.2126f * pixels[i].r + 0.7152f * pixels[i].g + 0.0722f * pixels[i].b;
                luminances[i] = luminance;
                totalBrightness += luminance;
            }

            float avgBrightness = totalBrightness / pixels.Length;

            // Calculate variance to detect content richness
            float variance = 0f;
            for (int i = 0; i < luminances.Length; i++)
            {
                float diff = luminances[i] - avgBrightness;
                variance += diff * diff;
            }
            variance /= pixels.Length;

            // Apply brightness penalty to avoid too dark or too bright frames
            float brightnessPenalty = 1f;
            if (avgBrightness < 0.1f) // Too dark
            {
                brightnessPenalty = avgBrightness / 0.1f; // Scale from 0 to 1
            }
            else if (avgBrightness > 0.95f) // Too bright
            {
                brightnessPenalty = (1f - avgBrightness) / 0.05f; // Scale from 1 to 0
            }

            // Quality score combines variance (content richness) with brightness penalty
            float qualityScore = variance * brightnessPenalty;

            return qualityScore;
        }

        public static bool HasVisibleContent(this Texture2D texture, Color backgroundColor, out float detectionPercentage)
        {
            detectionPercentage = 0f;

            if (texture == null) return false;

            // Sample pixels in a grid pattern for performance (don't need to check every pixel)
            // Check every 4th pixel in both dimensions for a 16x smaller sample set
            int sampleStep = 4;
            int totalSamples = 0;
            int differentPixels = 0;

            // Color difference threshold - account for anti-aliasing and compression artifacts
            float colorThreshold = 0.1f;

            for (int y = 0; y < texture.height; y += sampleStep)
            {
                for (int x = 0; x < texture.width; x += sampleStep)
                {
                    Color pixel = texture.GetPixel(x, y);
                    totalSamples++;

                    // Calculate color difference (Manhattan distance in RGB space)
                    float diff = Mathf.Abs(pixel.r - backgroundColor.r) +
                        Mathf.Abs(pixel.g - backgroundColor.g) +
                        Mathf.Abs(pixel.b - backgroundColor.b);

                    if (diff > colorThreshold)
                    {
                        differentPixels++;
                    }
                }
            }

            // If more than x% of sampled pixels are different, consider it has content.
            // Low to detect very small objects (like mechanical parts, pulleys, gears).
            // This threshold accounts for edge anti-aliasing while filtering out empty scenes.
            float differentPercentage = (float)differentPixels / totalSamples;
            detectionPercentage = differentPercentage * 100f; // Convert to percentage for logging
            float threshold = 0.003f; // 0.3% to cater for really thin objects
            if (differentPercentage > threshold) return true;

            // Sparse lines or bone visualizations can fall between the sampled grid points
            // or occupy less than 0.3% of the frame. Run an exact fallback before declaring
            // the render empty so valid thin content is not discarded.
            Color[] pixels = texture.GetPixels();
            int exactDifferentPixels = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                Color pixel = pixels[i];
                float diff = Mathf.Abs(pixel.r - backgroundColor.r) +
                    Mathf.Abs(pixel.g - backgroundColor.g) +
                    Mathf.Abs(pixel.b - backgroundColor.b);

                if (diff > colorThreshold)
                {
                    exactDifferentPixels++;
                }
            }

            float exactDifferentPercentage = pixels.Length > 0 ? (float)exactDifferentPixels / pixels.Length : 0f;
            if (exactDifferentPercentage > differentPercentage)
            {
                detectionPercentage = exactDifferentPercentage * 100f;
            }

            const float sparseThreshold = 0.001f; // 0.1% exact coverage for sparse but real content
            int minimumSparsePixels = Mathf.Max(4, Mathf.CeilToInt(pixels.Length * sparseThreshold));
            return exactDifferentPixels >= minimumSparsePixels;
        }

        public static Texture2D AssembleTextureSheet(List<Texture2D> frames)
        {
            if (frames == null || frames.Count == 0) return null;

            // Determine grid size (e.g., number of columns and rows)
            int frameCount = frames.Count;
            int columns = Mathf.CeilToInt(Mathf.Sqrt(frameCount));
            int rows = Mathf.CeilToInt((float)frameCount / columns);

            // Get frame dimensions (assuming all frames have the same dimensions)
            int frameWidth = frames[0].width;
            int frameHeight = frames[0].height;

            // Create a new Texture2D to hold all frames
            Texture2D textureSheet = new Texture2D(frameWidth * columns, frameHeight * rows, TextureFormat.RGBA32, false);

            // Copy frames into the texture sheet
            for (int i = 0; i < frameCount; i++)
            {
                int x = (i % columns) * frameWidth;
                int y = ((rows - 1) - (i / columns)) * frameHeight; // Start from bottom

                textureSheet.SetPixels(x, y, frameWidth, frameHeight, frames[i].GetPixels());
            }

            textureSheet.Apply();

            return textureSheet;
        }

        public static Tuple<int, int> GetDimensions(string file, bool ignoreErrors = false, string extOverride = null)
        {
            try
            {
                string path = IOUtils.ToLongPath(file);

                string ext = extOverride != null ? extOverride : Path.GetExtension(file).ToLowerInvariant();
                if (ext == ".png")
                {
                    using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        // PNG header: 8 bytes signature, 4 bytes length, 4 bytes "IHDR"
                        // Then the IHDR chunk: width (4 bytes, big-endian) and height (4 bytes, big-endian)
                        fs.Position = 16;
                        Span<byte> buffer = stackalloc byte[8];
                        if (fs.Read(buffer) == 8)
                        {
                            int width = (buffer[0] << 24) | (buffer[1] << 16) | (buffer[2] << 8) | buffer[3];
                            int height = (buffer[4] << 24) | (buffer[5] << 16) | (buffer[6] << 8) | buffer[7];
                            return Tuple.Create(width, height);
                        }
                    }
                }
                else if (ext == ".jpg" || ext == ".jpeg")
                {
                    using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        // Validate JPEG SOI marker (0xFFD8)
                        if (fs.ReadByte() != 0xFF || fs.ReadByte() != 0xD8)
                        {
                            Debug.LogWarning($"Not a valid JPEG file: {file}. Trying fallback.");
                        }
                        else
                        {
                            while (fs.Position < fs.Length)
                            {
                                if (fs.ReadByte() != 0xFF) break;
                                int marker = fs.ReadByte();
                                int length = (fs.ReadByte() << 8) | fs.ReadByte();
                                // SOF markers: 0xC0 to 0xC3
                                if (marker >= 0xC0 && marker <= 0xC3)
                                {
                                    fs.ReadByte(); // sample precision
                                    int height = (fs.ReadByte() << 8) | fs.ReadByte();
                                    int width = (fs.ReadByte() << 8) | fs.ReadByte();
                                    return Tuple.Create(width, height);
                                }
                                fs.Position += length - 2;
                            }
                        }
                    }
                }

#if UNITY_EDITOR_WIN && NET_4_6
                using (Image originalImage = Image.FromFile(path))
                {
                    return Tuple.Create(originalImage.Width, originalImage.Height);
                }
#else
                // fallback to ImageSharp for other formats
                IImageInfo imageInfo = Image.Identify(path);
                return new Tuple<int, int>(imageInfo.Width, imageInfo.Height);
#endif
            }
            catch (Exception e)
            {
                if (!ignoreErrors && LogImageOperations)
                {
                    Debug.LogWarning($"Could not determine image dimensions for '{file}': {e.Message}");
                }
                return null;
            }
        }

        /// <summary>
        /// Injects a PNG tEXt metadata chunk into PNG byte array.
        /// Inserts before the IEND chunk as per PNG specification.
        /// </summary>
        /// <param name="pngBytes">Original PNG file bytes from EncodeToPNG()</param>
        /// <param name="keyword">Metadata keyword (e.g., "AssetInventory:FrameGrid")</param>
        /// <param name="value">Metadata value (e.g., "4x4")</param>
        /// <returns>New PNG bytes with metadata chunk inserted</returns>
        public static byte[] InjectPngMetadata(byte[] pngBytes, string keyword, string value)
        {
            if (pngBytes == null || pngBytes.Length < 12) return pngBytes;

            try
            {
                // Find IEND chunk (last 12 bytes: 4 length + 4 type + 4 CRC)
                int iendPosition = pngBytes.Length - 12;
                
                // Verify it's actually IEND
                if (pngBytes[iendPosition + 4] != 'I' || 
                    pngBytes[iendPosition + 5] != 'E' || 
                    pngBytes[iendPosition + 6] != 'N' || 
                    pngBytes[iendPosition + 7] != 'D')
                {
                    Debug.LogWarning("Could not find IEND chunk in PNG");
                    return pngBytes;
                }

                // Build tEXt chunk data
                byte[] keywordBytes = System.Text.Encoding.GetEncoding("ISO-8859-1").GetBytes(keyword);
                byte[] valueBytes = System.Text.Encoding.GetEncoding("ISO-8859-1").GetBytes(value);
                
                // Chunk data: keyword + null separator + value
                int dataLength = keywordBytes.Length + 1 + valueBytes.Length;
                byte[] chunkData = new byte[dataLength];
                Array.Copy(keywordBytes, 0, chunkData, 0, keywordBytes.Length);
                chunkData[keywordBytes.Length] = 0; // null separator
                Array.Copy(valueBytes, 0, chunkData, keywordBytes.Length + 1, valueBytes.Length);

                // Build complete tEXt chunk: length + type + data + CRC
                byte[] chunkType = System.Text.Encoding.ASCII.GetBytes("tEXt");
                byte[] lengthBytes = BitConverter.GetBytes(dataLength);
                if (BitConverter.IsLittleEndian) Array.Reverse(lengthBytes); // Convert to big-endian

                // Calculate CRC32 for type + data
                byte[] crcInput = new byte[4 + dataLength];
                Array.Copy(chunkType, 0, crcInput, 0, 4);
                Array.Copy(chunkData, 0, crcInput, 4, dataLength);
                uint crc = CalculateCRC32(crcInput);
                byte[] crcBytes = BitConverter.GetBytes(crc);
                if (BitConverter.IsLittleEndian) Array.Reverse(crcBytes); // Convert to big-endian

                // Assemble new PNG: original up to IEND + tEXt chunk + IEND chunk
                int newLength = pngBytes.Length + 4 + 4 + dataLength + 4; // +length +type +data +CRC
                byte[] newPngBytes = new byte[newLength];
                
                // Copy everything before IEND
                Array.Copy(pngBytes, 0, newPngBytes, 0, iendPosition);
                
                int pos = iendPosition;
                // Write tEXt chunk
                Array.Copy(lengthBytes, 0, newPngBytes, pos, 4); pos += 4;
                Array.Copy(chunkType, 0, newPngBytes, pos, 4); pos += 4;
                Array.Copy(chunkData, 0, newPngBytes, pos, dataLength); pos += dataLength;
                Array.Copy(crcBytes, 0, newPngBytes, pos, 4); pos += 4;
                
                // Copy IEND chunk
                Array.Copy(pngBytes, iendPosition, newPngBytes, pos, 12);

                return newPngBytes;
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to inject PNG metadata: {e.Message}");
                return pngBytes; // Return original on error
            }
        }

        /// <summary>
        /// Reads PNG tEXt metadata chunk without loading the full image.
        /// Only reads file headers for optimal performance (~0.5-2ms).
        /// </summary>
        /// <param name="pngFilePath">Path to PNG file</param>
        /// <param name="keyword">Metadata keyword to search for</param>
        /// <returns>Metadata value if found, null otherwise</returns>
        public static string ReadPngMetadata(string pngFilePath, string keyword)
        {
            try
            {
                using (FileStream fs = new FileStream(IOUtils.ToLongPath(pngFilePath), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    // Verify PNG signature (8 bytes: 137 80 78 71 13 10 26 10)
                    byte[] signature = new byte[8];
                    if (fs.Read(signature, 0, 8) != 8) return null;
                    if (signature[0] != 137 || signature[1] != 80 || signature[2] != 78 || signature[3] != 71 ||
                        signature[4] != 13 || signature[5] != 10 || signature[6] != 26 || signature[7] != 10)
                    {
                        return null; // Not a valid PNG
                    }

                    byte[] keywordBytes = System.Text.Encoding.GetEncoding("ISO-8859-1").GetBytes(keyword);
                    byte[] lengthBuffer = new byte[4];
                    byte[] typeBuffer = new byte[4];

                    // Iterate through chunks
                    while (fs.Position < fs.Length - 12) // Need at least 12 bytes for a chunk
                    {
                        // Read chunk length (4 bytes, big-endian)
                        if (fs.Read(lengthBuffer, 0, 4) != 4) break;
                        if (BitConverter.IsLittleEndian) Array.Reverse(lengthBuffer);
                        int chunkLength = BitConverter.ToInt32(lengthBuffer, 0);

                        // Read chunk type (4 bytes)
                        if (fs.Read(typeBuffer, 0, 4) != 4) break;

                        // Check if this is a tEXt chunk
                        if (typeBuffer[0] == 't' && typeBuffer[1] == 'E' && typeBuffer[2] == 'X' && typeBuffer[3] == 't')
                        {
                            // Read chunk data
                            byte[] chunkData = new byte[chunkLength];
                            if (fs.Read(chunkData, 0, chunkLength) != chunkLength) break;

                            // Skip CRC (4 bytes)
                            fs.Position += 4;

                            // Parse tEXt data: keyword + null + value
                            int nullIndex = Array.IndexOf(chunkData, (byte)0);
                            if (nullIndex > 0)
                            {
                                byte[] foundKeyword = new byte[nullIndex];
                                Array.Copy(chunkData, 0, foundKeyword, 0, nullIndex);

                                // Check if this is the keyword we're looking for
                                if (foundKeyword.Length == keywordBytes.Length)
                                {
                                    bool match = true;
                                    for (int i = 0; i < foundKeyword.Length; i++)
                                    {
                                        if (foundKeyword[i] != keywordBytes[i])
                                        {
                                            match = false;
                                            break;
                                        }
                                    }

                                    if (match)
                                    {
                                        // Extract value (everything after null byte)
                                        int valueLength = chunkData.Length - nullIndex - 1;
                                        if (valueLength > 0)
                                        {
                                            byte[] valueBytes = new byte[valueLength];
                                            Array.Copy(chunkData, nullIndex + 1, valueBytes, 0, valueLength);
                                            return System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(valueBytes);
                                        }
                                    }
                                }
                            }
                        }
                        else if (typeBuffer[0] == 'I' && typeBuffer[1] == 'E' && typeBuffer[2] == 'N' && typeBuffer[3] == 'D')
                        {
                            // Reached end of PNG
                            break;
                        }
                        else
                        {
                            // Skip this chunk's data and CRC
                            fs.Position += chunkLength + 4;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                if (LogImageOperations)
                {
                    Debug.LogWarning($"Failed to read PNG metadata from '{pngFilePath}': {e.Message}");
                }
            }

            return null;
        }

        /// <summary>
        /// Calculates CRC32 checksum for PNG chunks.
        /// </summary>
        private static uint CalculateCRC32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            
            for (int i = 0; i < data.Length; i++)
            {
                crc ^= data[i];
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 1) != 0)
                        crc = (crc >> 1) ^ 0xEDB88320;
                    else
                        crc >>= 1;
                }
            }
            
            return crc ^ 0xFFFFFFFF;
        }

        /// <summary>
        /// Embeds an icon indicator in the bottom right corner of a preview texture.
        /// Used to indicate that an animated preview exists for this asset.
        /// </summary>
        /// <param name="texture">The preview texture to modify</param>
        /// <param name="iconName">Name of the Unity icon to embed (default: "d_PlayButton")</param>
        /// <param name="iconSize">Size of the icon in pixels (default: 24)</param>
        /// <param name="margin">Margin from the corner in pixels (default: 4)</param>
        /// <returns>The modified texture with the icon embedded</returns>
        public static Texture2D EmbedPlayIconIndicator(Texture2D texture, string iconName = "d_PlayButton", int iconSize = 24, int margin = 4)
        {
            if (texture == null) return texture;

            // Get the icon from Unity
            GUIContent iconContent = EditorGUIUtility.IconContent(iconName);
            if (iconContent == null || iconContent.image == null)
            {
                return texture; // Return original if icon can't be loaded
            }

            Texture2D icon = iconContent.image as Texture2D;
            if (icon == null) return texture;

            // Ensure the preview texture is readable
            Texture2D readableTexture = texture;
            bool createdTemporary = false;
            if (!texture.isReadable)
            {
                readableTexture = texture.MakeReadable();
                createdTemporary = true;
            }

            try
            {
                // Calculate icon size proportional to preview size (but with min/max bounds)
                int previewSize = Mathf.Min(texture.width, texture.height);
                int scaledIconSize = Mathf.Clamp(iconSize, 16, Mathf.Min(48, previewSize / 8));
                
                // Scale the icon to desired size using RenderTexture
                Texture2D scaledIcon = null;
                bool createdScaledIcon = false;
                bool iconNeedsScaling = icon.width != scaledIconSize || icon.height != scaledIconSize;
                if (iconNeedsScaling)
                {
                    RenderTexture iconRT = RenderTexture.GetTemporary(scaledIconSize, scaledIconSize, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
                    iconRT.filterMode = FilterMode.Bilinear;
                    RenderTexture previousActive = RenderTexture.active;
                    RenderTexture.active = iconRT;
                    UnityEngine.Graphics.Blit(icon, iconRT);
                    
                    scaledIcon = new Texture2D(scaledIconSize, scaledIconSize, TextureFormat.RGBA32, false);
                    scaledIcon.ReadPixels(new Rect(0, 0, scaledIconSize, scaledIconSize), 0, 0);
                    scaledIcon.Apply();
                    RenderTexture.active = previousActive;
                    RenderTexture.ReleaseTemporary(iconRT);
                    createdScaledIcon = true;
                }
                else
                {
                    // Make icon readable if needed
                    if (!icon.isReadable)
                    {
                        scaledIcon = icon.MakeReadable();
                        createdScaledIcon = true;
                    }
                    else
                    {
                        scaledIcon = icon;
                    }
                }

                // Get all pixels from the preview texture
                Color[] previewPixels = readableTexture.GetPixels();
                Color[] iconPixels = scaledIcon.GetPixels();
                
                // Calculate position for icon (bottom right corner)
                int iconX = readableTexture.width - scaledIconSize - margin;
                int iconY = margin;
                
                // Composite icon onto preview using alpha blending
                for (int iconY_local = 0; iconY_local < scaledIconSize; iconY_local++)
                {
                    int previewY = iconY + iconY_local;
                    if (previewY < 0 || previewY >= readableTexture.height) continue;
                    
                    for (int iconX_local = 0; iconX_local < scaledIconSize; iconX_local++)
                    {
                        int previewX = iconX + iconX_local;
                        if (previewX < 0 || previewX >= readableTexture.width) continue;
                        
                        int iconIndex = iconY_local * scaledIconSize + iconX_local;
                        int previewIndex = previewY * readableTexture.width + previewX;
                        
                        if (iconIndex >= 0 && iconIndex < iconPixels.Length && 
                            previewIndex >= 0 && previewIndex < previewPixels.Length)
                        {
                            Color iconColor = iconPixels[iconIndex];
                            Color previewColor = previewPixels[previewIndex];
                            
                            // Alpha blend: result = icon * icon.alpha + preview * (1 - icon.alpha)
                            float alpha = iconColor.a;
                            previewPixels[previewIndex] = new Color(
                                iconColor.r * alpha + previewColor.r * (1f - alpha),
                                iconColor.g * alpha + previewColor.g * (1f - alpha),
                                iconColor.b * alpha + previewColor.b * (1f - alpha),
                                Mathf.Max(iconColor.a, previewColor.a)
                            );
                        }
                    }
                }
                
                // Create result texture with composited pixels
                Texture2D result = new Texture2D(readableTexture.width, readableTexture.height, TextureFormat.RGBA32, false);
                result.SetPixels(previewPixels);
                result.filterMode = readableTexture.filterMode;
                result.wrapMode = readableTexture.wrapMode;
                result.hideFlags = readableTexture.hideFlags;
                result.Apply();
                
                // Clean up temporary textures
                if (createdScaledIcon)
                {
                    UnityEngine.Object.DestroyImmediate(scaledIcon);
                }
                
                // Clean up temporary readable texture if we created one
                if (createdTemporary)
                {
                    UnityEngine.Object.DestroyImmediate(readableTexture);
                }
                
                return result;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Failed to embed icon indicator: {e.Message}");
                
                // Clean up temporary texture if we created one
                if (createdTemporary)
                {
                    UnityEngine.Object.DestroyImmediate(readableTexture);
                }
                
                return texture; // Return original on error
            }
        }
    }
}
