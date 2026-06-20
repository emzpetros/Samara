using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace AssetInventory
{
    /// <summary>
    /// Centralized render texture management and utilities for preview generation.
    /// </summary>
    public static class PreviewRenderUtilities
    {
        /// <summary>
        /// Render camera to texture with proper MSAA and depth handling
        /// </summary>
        public static Texture2D RenderCameraToTexture(Camera camera, int size)
        {
            bool useLinearColorSpace = QualitySettings.activeColorSpace == ColorSpace.Linear;

            RenderTextureDescriptor rtd = new RenderTextureDescriptor(size, size)
            {
                depthBufferBits = AI.Config.cpDepth,
                msaaSamples = 4,
                useMipMap = false,
                sRGB = useLinearColorSpace, // Only use sRGB in Linear color space
                graphicsFormat = useLinearColorSpace
                    ? GraphicsFormat.R8G8B8A8_SRGB
                    : GraphicsFormat.R8G8B8A8_UNorm
            };

            RenderTexture rt = new RenderTexture(rtd);
            rt.Create();

            camera.targetTexture = rt;
            camera.Render();
            camera.targetTexture = null;

            // Read pixels from render texture
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            RenderTexture oldActive = RenderTexture.active;
            RenderTexture.active = rt;
            texture.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            texture.Apply();
            RenderTexture.active = oldActive;

            rt.Release();
            Object.DestroyImmediate(rt);

            return texture;
        }

        /// <summary>
        /// Create render texture descriptor with standard preview settings
        /// </summary>
        public static RenderTextureDescriptor CreateStandardDescriptor(int width, int height)
        {
            bool useLinearColorSpace = QualitySettings.activeColorSpace == ColorSpace.Linear;

            return new RenderTextureDescriptor(width, height)
            {
                depthBufferBits = AI.Config.cpDepth,
                msaaSamples = 4,
                useMipMap = false,
                sRGB = useLinearColorSpace,
                graphicsFormat = useLinearColorSpace
                    ? GraphicsFormat.R8G8B8A8_SRGB
                    : GraphicsFormat.R8G8B8A8_UNorm
            };
        }

        /// <summary>
        /// Convert render texture to Texture2D
        /// </summary>
        public static Texture2D RenderTextureToTexture2D(RenderTexture rt)
        {
            Texture2D texture = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, false);
            RenderTexture oldActive = RenderTexture.active;
            RenderTexture.active = rt;
            texture.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            texture.Apply();
            RenderTexture.active = oldActive;
            return texture;
        }

        /// <summary>
        /// Extract alpha from luminance for transparent backgrounds with particles
        /// Particles don't render correctly against Color.clear, so we render against black
        /// and extract alpha from brightness
        /// </summary>
        public static void ExtractAlphaFromLuminance(Texture2D texture)
        {
            Color[] pixels = texture.GetPixels();
            for (int i = 0; i < pixels.Length; i++)
            {
                Color pixel = pixels[i];
                // Calculate luminance as alpha (grayscale brightness = opacity)
                float alpha = (pixel.r + pixel.g + pixel.b) / 3f;
                // If pixel has any brightness, keep it and set proper alpha
                if (alpha > 0.01f)
                {
                    // Keep original color but set alpha from luminance
                    pixels[i] = new Color(pixel.r, pixel.g, pixel.b, alpha);
                }
                else
                {
                    // Fully transparent for black pixels
                    pixels[i] = Color.clear;
                }
            }
            texture.SetPixels(pixels);
            texture.Apply();
        }
    }
}

