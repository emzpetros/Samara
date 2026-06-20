#if UNITY_EDITOR_OSX
using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace ImpossibleRobert.Common
{
    /// <summary>
    /// Native Mac image processing using CoreGraphics and ImageIO APIs.
    /// Hardware-accelerated image operations optimized for Apple Silicon.
    /// Falls back to ImageSharp on any failure.
    /// </summary>
    public static partial class ImageUtils
    {
        // P/Invoke Structs

        [StructLayout(LayoutKind.Sequential)]
        private struct CGPoint
        {
            public double x;
            public double y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CGSize
        {
            public double width;
            public double height;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CGRect
        {
            public CGPoint origin;
            public CGSize size;

            public static CGRect Make(double x, double y, double width, double height)
            {
                return new CGRect
                {
                    origin = new CGPoint { x = x, y = y },
                    size = new CGSize { width = width, height = height }
                };
            }
        }

        // P/Invoke Constants

        private const uint kCGImageAlphaPremultipliedLast = 1;
        private const uint kCGBitmapByteOrder32Big = 4 << 12;
        private const uint kCGImageAlphaNoneSkipLast = 5;
        private const int kCFStringEncodingUTF8 = 0x08000100;

        // CoreFoundation P/Invoke

        private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [DllImport(CoreFoundationLib)]
        private static extern void CFRelease(IntPtr cf);

        [DllImport(CoreFoundationLib)]
        private static extern IntPtr CFURLCreateFromFileSystemRepresentation(IntPtr allocator, byte[] buffer, long bufferLength, bool isDirectory);

        [DllImport(CoreFoundationLib)]
        private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string cStr, int encoding);

        // CoreGraphics P/Invoke

        private const string CoreGraphicsLib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

        [DllImport(CoreGraphicsLib)]
        private static extern IntPtr CGColorSpaceCreateDeviceRGB();

        [DllImport(CoreGraphicsLib)]
        private static extern void CGColorSpaceRelease(IntPtr space);

        [DllImport(CoreGraphicsLib)]
        private static extern IntPtr CGBitmapContextCreate(IntPtr data, uint width, uint height, uint bitsPerComponent, uint bytesPerRow, IntPtr space, uint bitmapInfo);

        [DllImport(CoreGraphicsLib)]
        private static extern void CGContextRelease(IntPtr context);

        [DllImport(CoreGraphicsLib)]
        private static extern void CGContextDrawImage(IntPtr context, CGRect rect, IntPtr image);

        [DllImport(CoreGraphicsLib)]
        private static extern IntPtr CGBitmapContextCreateImage(IntPtr context);

        [DllImport(CoreGraphicsLib)]
        private static extern IntPtr CGBitmapContextGetData(IntPtr context);

        [DllImport(CoreGraphicsLib)]
        private static extern void CGImageRelease(IntPtr image);

        [DllImport(CoreGraphicsLib)]
        private static extern uint CGImageGetWidth(IntPtr image);

        [DllImport(CoreGraphicsLib)]
        private static extern uint CGImageGetHeight(IntPtr image);

        [DllImport(CoreGraphicsLib)]
        private static extern uint CGImageGetBytesPerRow(IntPtr image);

        [DllImport(CoreGraphicsLib)]
        private static extern void CGContextSetInterpolationQuality(IntPtr context, int quality);

        // libSystem P/Invoke

        [DllImport("libSystem.dylib")]
        private static extern void bzero(IntPtr dest, uint n);

        // ImageIO P/Invoke

        private const string ImageIOLib = "/System/Library/Frameworks/ImageIO.framework/ImageIO";

        [DllImport(ImageIOLib)]
        private static extern IntPtr CGImageSourceCreateWithURL(IntPtr url, IntPtr options);

        [DllImport(ImageIOLib)]
        private static extern IntPtr CGImageSourceCreateImageAtIndex(IntPtr source, uint index, IntPtr options);

        [DllImport(ImageIOLib)]
        private static extern IntPtr CGImageDestinationCreateWithURL(IntPtr url, IntPtr type, uint count, IntPtr options);

        [DllImport(ImageIOLib)]
        private static extern void CGImageDestinationAddImage(IntPtr destination, IntPtr image, IntPtr properties);

        [DllImport(ImageIOLib)]
        private static extern bool CGImageDestinationFinalize(IntPtr destination);

        /// <summary>
        /// Safe wrapper for native CoreFoundation/CoreGraphics handles with automatic cleanup.
        /// </summary>
        private readonly struct SafeHandle : IDisposable
        {
            public readonly IntPtr Ptr;
            private readonly Action<IntPtr> _release;

            public SafeHandle(IntPtr ptr, Action<IntPtr> release)
            {
                Ptr = ptr;
                _release = release;
            }

            public bool IsValid => Ptr != IntPtr.Zero;

            public void Dispose()
            {
                if (Ptr != IntPtr.Zero && _release != null)
                {
                    _release(Ptr);
                }
            }
        }

        /// <summary>
        /// Creates a CoreFoundation URL from a file path.
        /// </summary>
        private static SafeHandle CreateCFURL(string path)
        {
            byte[] pathBytes = System.Text.Encoding.UTF8.GetBytes(path);
            IntPtr url = CFURLCreateFromFileSystemRepresentation(IntPtr.Zero, pathBytes, pathBytes.Length, false);
            return new SafeHandle(url, CFRelease);
        }

        /// <summary>
        /// Creates a CoreFoundation string for UTI type identifiers.
        /// </summary>
        private static SafeHandle CreateCFString(string str)
        {
            IntPtr cfStr = CFStringCreateWithCString(IntPtr.Zero, str, kCFStringEncodingUTF8);
            return new SafeHandle(cfStr, CFRelease);
        }

        /// <summary>
        /// Loads a CGImage from a file path using ImageIO.
        /// </summary>
        private static SafeHandle LoadCGImageFromFile(string filePath)
        {
            using (SafeHandle url = CreateCFURL(filePath))
            {
                if (!url.IsValid) return new SafeHandle(IntPtr.Zero, null);

                IntPtr imageSource = CGImageSourceCreateWithURL(url.Ptr, IntPtr.Zero);
                if (imageSource == IntPtr.Zero) return new SafeHandle(IntPtr.Zero, null);

                try
                {
                    IntPtr image = CGImageSourceCreateImageAtIndex(imageSource, 0, IntPtr.Zero);
                    return new SafeHandle(image, CGImageRelease);
                }
                finally
                {
                    CFRelease(imageSource);
                }
            }
        }

        /// <summary>
        /// Saves a CGImage to a file as PNG using ImageIO.
        /// </summary>
        private static bool SaveCGImageToFile(IntPtr image, string outputPath)
        {
            using (SafeHandle url = CreateCFURL(outputPath))
            using (SafeHandle pngType = CreateCFString("public.png"))
            {
                if (!url.IsValid || !pngType.IsValid) return false;

                IntPtr destination = CGImageDestinationCreateWithURL(url.Ptr, pngType.Ptr, 1, IntPtr.Zero);
                if (destination == IntPtr.Zero) return false;

                try
                {
                    CGImageDestinationAddImage(destination, image, IntPtr.Zero);
                    return CGImageDestinationFinalize(destination);
                }
                finally
                {
                    CFRelease(destination);
                }
            }
        }

        /// <summary>
        /// Creates a bitmap context with the specified dimensions.
        /// Returns the context handle and allocated data buffer.
        /// </summary>
        private static SafeHandle CreateBitmapContext(uint width, uint height, out IntPtr dataBuffer, out uint bytesPerRow)
        {
            bytesPerRow = width * 4;
            int bufferSize = (int)(height * bytesPerRow);
            dataBuffer = Marshal.AllocHGlobal(bufferSize);

            if (dataBuffer == IntPtr.Zero)
            {
                bytesPerRow = 0;
                return new SafeHandle(IntPtr.Zero, null);
            }

            // Clear the buffer to prevent artifacts from uninitialized memory
            bzero(dataBuffer, (uint)bufferSize);

            IntPtr colorSpace = CGColorSpaceCreateDeviceRGB();
            if (colorSpace == IntPtr.Zero)
            {
                Marshal.FreeHGlobal(dataBuffer);
                dataBuffer = IntPtr.Zero;
                bytesPerRow = 0;
                return new SafeHandle(IntPtr.Zero, null);
            }

            try
            {
                IntPtr context = CGBitmapContextCreate(
                    dataBuffer,
                    width,
                    height,
                    8, // bits per component
                    bytesPerRow,
                    colorSpace,
                    kCGImageAlphaPremultipliedLast | kCGBitmapByteOrder32Big
                );

                if (context == IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(dataBuffer);
                    dataBuffer = IntPtr.Zero;
                    bytesPerRow = 0;
                    return new SafeHandle(IntPtr.Zero, null);
                }

                // Set high quality interpolation for resizing
                CGContextSetInterpolationQuality(context, 3); // kCGInterpolationHigh = 3

                return new SafeHandle(context, CGContextRelease);
            }
            finally
            {
                CGColorSpaceRelease(colorSpace);
            }
        }

        /// <summary>
        /// Attempts to resize an image using native CoreGraphics.
        /// Returns true on success, false if fallback should be used.
        /// </summary>
        internal static bool TryResizeImageNative(string originalFile, string outputFile, int maxSize, bool scaleBeyondSize, string typeOverride)
        {
            IntPtr dataBuffer = IntPtr.Zero;

            try
            {
                // Use shared helper for dimension calculation and common logic
                ResizeParams? paramsResult = CalculateResizeParams(
                    originalFile, outputFile, maxSize, scaleBeyondSize, typeOverride,
                    () =>
                    {
                        // Platform-specific fallback: load dimensions via CoreGraphics
                        using (SafeHandle sourceImage = LoadCGImageFromFile(originalFile))
                        {
                            if (!sourceImage.IsValid) return null;
                            return ((int)CGImageGetWidth(sourceImage.Ptr), (int)CGImageGetHeight(sourceImage.Ptr));
                        }
                    },
                    out bool fileCopied);

                if (fileCopied) return true;
                if (paramsResult == null) return false;

                ResizeParams resizeParams = paramsResult.Value;

                // Load full image for processing
                using (SafeHandle sourceImage = LoadCGImageFromFile(originalFile))
                {
                    if (!sourceImage.IsValid) return false;

                    // Create bitmap context for the new size
                    uint bytesPerRow;
                    using (SafeHandle context = CreateBitmapContext((uint)resizeParams.NewWidth, (uint)resizeParams.NewHeight, out dataBuffer, out bytesPerRow))
                    {
                        if (!context.IsValid) return false;

                        // Draw the source image scaled into the context
                        CGRect drawRect = CGRect.Make(0, 0, resizeParams.NewWidth, resizeParams.NewHeight);
                        CGContextDrawImage(context.Ptr, drawRect, sourceImage.Ptr);

                        // Create output image from context
                        IntPtr outputImage = CGBitmapContextCreateImage(context.Ptr);
                        if (outputImage == IntPtr.Zero) return false;

                        try
                        {
                            // Save to file
                            return SaveCGImageToFile(outputImage, IOUtils.ToLongPath(outputFile));
                        }
                        finally
                        {
                            CGImageRelease(outputImage);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                if (LogImageOperations)
                {
                    Debug.LogWarning($"Native Mac resize failed for '{originalFile}', falling back to ImageSharp: {e.Message}");
                }
                return false;
            }
            finally
            {
                if (dataBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(dataBuffer);
                }
            }
        }

        /// <summary>
        /// Attempts to compute perceptual hash using native CoreGraphics.
        /// Returns true on success with the hash value, false if fallback should be used.
        /// </summary>
        internal static bool TryComputePerceptualHashNative(string filePath, out ulong hash, int hashSize = 8)
        {
            hash = 0;
            IntPtr dataBuffer = IntPtr.Zero;

            try
            {
                using (SafeHandle sourceImage = LoadCGImageFromFile(filePath))
                {
                    if (!sourceImage.IsValid) return false;

                    // Create a small bitmap context for the hash
                    uint bytesPerRow;
                    using (SafeHandle context = CreateBitmapContext((uint)hashSize, (uint)hashSize, out dataBuffer, out bytesPerRow))
                    {
                        if (!context.IsValid) return false;

                        // Draw the source image scaled down to hash size
                        CGRect drawRect = CGRect.Make(0, 0, hashSize, hashSize);
                        CGContextDrawImage(context.Ptr, drawRect, sourceImage.Ptr);

                        // Calculate hash from pixel data using unsafe for performance
                        hash = ComputeHashFromBuffer(dataBuffer, hashSize, (int)bytesPerRow);
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                if (LogImageOperations)
                {
                    Debug.LogWarning($"Native Mac hash computation failed for '{filePath}', falling back to ImageSharp: {e.Message}");
                }
                return false;
            }
            finally
            {
                if (dataBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(dataBuffer);
                }
            }
        }

        /// <summary>
        /// Computes perceptual hash from raw RGBA buffer using managed byte array access.
        /// </summary>
        private static ulong ComputeHashFromBuffer(IntPtr buffer, int hashSize, int bytesPerRow)
        {
            int totalSize = hashSize * bytesPerRow;
            byte[] managedBuffer = new byte[totalSize];
            Marshal.Copy(buffer, managedBuffer, 0, totalSize);

            double sum = 0.0;
            double[] pixels = new double[hashSize * hashSize];
            int idx = 0;

            // CoreGraphics bitmap context has origin at bottom-left, so we read bottom-to-top
            // to match the expected pixel order
            for (int y = hashSize - 1; y >= 0; y--)
            {
                int rowOffset = y * bytesPerRow;
                for (int x = 0; x < hashSize; x++)
                {
                    int offset = rowOffset + (x * 4);
                    byte r = managedBuffer[offset + 0];
                    byte g = managedBuffer[offset + 1];
                    byte b = managedBuffer[offset + 2];

                    // Convert to grayscale using standard luminance formula
                    double luminance = (r * 0.299) + (g * 0.587) + (b * 0.114);
                    pixels[idx++] = luminance;
                    sum += luminance;
                }
            }

            double avg = sum / pixels.Length;
            ulong hash = 0UL;

            for (int i = 0; i < pixels.Length; i++)
            {
                if (pixels[i] > avg)
                {
                    hash |= 1UL << i;
                }
            }

            return hash;
        }

        /// <summary>
        /// Attempts to compute perceptual hash from raw PNG bytes using native CoreGraphics.
        /// Returns true on success with the hash value, false if fallback should be used.
        /// </summary>
        internal static bool TryComputePerceptualHashFromBytesNative(byte[] pngData, out ulong hash, int hashSize = 8)
        {
            hash = 0;
            IntPtr dataBuffer = IntPtr.Zero;
            string tempFile = null;

            try
            {
                // Write PNG data to a temporary file (CoreGraphics needs file URLs)
                tempFile = Path.Combine(Path.GetTempPath(), $"ai_hash_{Guid.NewGuid():N}.png");
                File.WriteAllBytes(tempFile, pngData);

                return TryComputePerceptualHashNative(tempFile, out hash, hashSize);
            }
            catch (Exception e)
            {
                if (LogImageOperations)
                {
                    Debug.LogWarning($"Native Mac hash from bytes failed, falling back to ImageSharp: {e.Message}");
                }
                return false;
            }
            finally
            {
                if (dataBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(dataBuffer);
                }

                // Clean up temp file
                if (tempFile != null && File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
        }
    }
}
#endif
