using System.Collections.Generic;
using ImpossibleRobert.Common;
using UnityEngine;
using UnityEditor;
using Object = UnityEngine.Object;

namespace AssetInventory
{
    public static class FontPreviewGenerator
    {
        private const string PREVIEW_TEXT = "123 Quick\nBrown Fox\nJumps Over\nlazy Dog\nToday Now";

        public static Texture2D Create(Font font, int textureSize = PreviewManager.DEFAULT_PREVIEW_SIZE)
        {
            // Step 1: Set a higher rendering resolution
            int renderResolution = textureSize * 4; // Increase resolution (e.g., 512 for 128 texture size)

            // Step 2: Generate Mesh Data for the Text at higher resolution
            // Get font color from settings
            Color fontColor = Color.black;
            if (!string.IsNullOrEmpty(AI.Config.cpFontColor) &&
                ColorUtility.TryParseHtmlString("#" + AI.Config.cpFontColor, out Color fc))
            {
                fontColor = fc;
            }

            TextGenerator textGen = new TextGenerator();
            TextGenerationSettings textSettings = new TextGenerationSettings()
            {
                textAnchor = TextAnchor.MiddleCenter,
                generateOutOfBounds = true,
                generationExtents = new Vector2(renderResolution, renderResolution),
                pivot = new Vector2(0.5f, 0.5f),
                richText = false,
                font = font,
                fontSize = renderResolution, // Use higher font size
                fontStyle = FontStyle.Normal,
                verticalOverflow = VerticalWrapMode.Overflow,
                horizontalOverflow = HorizontalWrapMode.Overflow,
                color = fontColor,
                scaleFactor = 1.0f,
                lineSpacing = 1.0f,
            };

            textGen.Populate(PREVIEW_TEXT, textSettings);

            // Step 3: Create a Mesh from the Generated Data
            Mesh mesh = new Mesh();
            mesh.name = "TextMesh";
            IList<UIVertex> verts = textGen.verts;
            int vertCount = verts.Count;

            Vector3[] vertices = new Vector3[vertCount];
            Vector2[] uv = new Vector2[vertCount];
            Color32[] colors = new Color32[vertCount];
            int[] triangles = new int[(vertCount / 4) * 6];

            for (int i = 0; i < vertCount; i++)
            {
                vertices[i] = verts[i].position;
                uv[i] = verts[i].uv0;
                colors[i] = verts[i].color; // Copy vertex colors for proper text coloring
            }

            for (int i = 0, t = 0; i < vertCount; i += 4, t += 6)
            {
                triangles[t + 0] = i + 0;
                triangles[t + 1] = i + 1;
                triangles[t + 2] = i + 2;
                triangles[t + 3] = i + 2;
                triangles[t + 4] = i + 3;
                triangles[t + 5] = i + 0;
            }

            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.colors32 = colors;
            mesh.triangles = triangles;

            // Step 4: Calculate the Mesh Bounds and Adjust Scaling
            Bounds textBounds = mesh.bounds;
            float maxDimension = Mathf.Max(textBounds.size.x, textBounds.size.y);
            float scaleFactor = (renderResolution / maxDimension) * 0.9f; // 0.9 to add some padding

            // Step 5: Create a GameObject with MeshFilter and MeshRenderer
            GameObject tempGO = new GameObject("TempTextMesh");
            MeshFilter meshFilter = tempGO.AddComponent<MeshFilter>();
            MeshRenderer meshRenderer = tempGO.AddComponent<MeshRenderer>();
            meshFilter.sharedMesh = mesh;

            // Create a Material for font rendering
            // The font's material uses GUI/Text Shader which requires vertex colors for tinting
            // and texture alpha for glyph visibility
            Material material = new Material(font.material);
            // Note: fontColor is already baked into vertex colors via TextGenerationSettings.color
            // We set material.color as backup for shaders that don't use vertex colors
            material.color = fontColor;
            meshRenderer.sharedMaterial = material;

            // Step 6: Position the GameObject
            tempGO.transform.position = new Vector3(0, 0, 0);
            tempGO.transform.localScale = Vector3.one * scaleFactor;

            // Step 7: Render using hidden Scene view (no layer management needed!)
            Texture2D highResTexture = SceneViewCameraRenderer.CaptureWithSceneCamera(
                tempGO,
                renderResolution,
                renderResolution,
                (camera, renderScene, sceneContext) =>
                {
                    camera.orthographic = true;
                    camera.orthographicSize = renderResolution / 2f;
                    camera.transform.position = new Vector3(0, 0, -100);
                    // No cullingMask needed - dedicated scene!
                }
            );

            if (highResTexture == null)
            {
                Debug.LogError("[FontPreviewGenerator] Failed to render font preview");
                Object.DestroyImmediate(tempGO);
                return null;
            }

            // Step 8: Downscale using ImageUtils
            Texture2D finalTexture = highResTexture.Downscale(textureSize, textureSize);

            // Step 9: Clean Up
            Object.DestroyImmediate(highResTexture);
            Object.DestroyImmediate(tempGO);

            return finalTexture;
        }

        public static Texture2D Create(string file, int textureSize = 128)
        {
            Font font = AssetDatabase.LoadAssetAtPath<Font>(file);
            if (font == null)
            {
                Debug.LogError($"Failed to load font from: {file}");
                return null;
            }

            return Create(font, textureSize);
        }
    }
}
