using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ImpossibleRobert.Common;
using Unity.EditorCoroutines.Editor;
using UnityEditor;
#if UNITY_6000_0_OR_NEWER
using UnityEditor.PackageManager;
#endif
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor.SceneManagement;
#if USE_VFX
using UnityEngine.VFX;
#endif
using UnityEngine.Video;

namespace AssetInventory
{
    /// <summary>
    /// Flags to track which preview types need regeneration
    /// </summary>
    [System.Flags]
    public enum PreviewTypeFlags
    {
        None = 0,
        Model3D = 1 << 0,
        FBX = 1 << 1,
        UI = 1 << 2,
        Particle = 1 << 3,
        VFX = 1 << 4,
        Font = 1 << 5,
        Video = 1 << 6,
        Material = 1 << 7,
        Anim = 1 << 8,
        All = Model3D | FBX | UI | Particle | VFX | Font | Video | Material | Anim,
        AllRendered = Model3D | FBX | UI | Particle | VFX | Font | Material | Anim, // Types that use the preview scene
        AllAnimated = Model3D | FBX | Particle | VFX | Video | Anim // Types that have animated variants
    }

    public class CustomPreviewSettingsUI : BasicEditorUI
    {
        private Vector2 _scrollPosition;
        private Vector2 _previewScrollPosition;

        // Preview objects for each type (kept in memory, not persisted to disk)
        private GameObject _preview3DObject;
        private string _sampleFBXPath;
        private GameObject _previewUIObject;
        private GameObject _previewParticleObject;
#pragma warning disable 0414
        private GameObject _previewVFXObject; // Only used when USE_VFX is defined
#pragma warning restore 0414

        // Storage preview scene to keep preview objects (prevents them from being destroyed)
        private static Scene _storagePreviewScene;

        // Preview textures for each type
        private Texture2D _static3DTexture;
        private Texture2D _animated3DTexture;
        private Texture2D _staticFBXTexture;
        private Texture2D _animatedFBXTexture;
        private Texture2D _staticAnimTexture;
        private Texture2D _animatedAnimTexture;
        private Texture2D _staticUITexture;
        private Texture2D _staticParticleTexture;
        private Texture2D _animatedParticleTexture;
        private Texture2D _staticVFXTexture;
        private Texture2D _animatedVFXTexture;
        private Texture2D _previewFontTexture;
        private Texture2D _staticVideoTexture;
        private Texture2D _animatedVideoTexture;
        private Texture2D _staticMaterialTexture;

        // Video preview
        private VideoClip _sampleVideoClip;

        // Material preview
        private Material _sampleMaterial;

        private bool _isGeneratingPreview;
        private PreviewTypeFlags _previewTypesToUpdate = PreviewTypeFlags.All;
        private float _lastUpdateTime;

        // Animation playback toggle and players
        private bool _playAnimatedPreviews;
        private AnimationPlayer _anim3DPlayer;
        private AnimationPlayer _animFBXPlayer;
        private AnimationPlayer _animAnimPlayer;
        private AnimationPlayer _animParticlePlayer;
        private AnimationPlayer _animVFXPlayer;
        private AnimationPlayer _animVideoPlayer;

        private const int PREVIEW_SIZE = 200;
        private const float UPDATE_THROTTLE = 0.5f; // Seconds between preview updates

        /// <summary>
        /// Get the appropriate built-in font based on Unity version
        /// </summary>
        private static Font GetBuiltinFont()
        {
#if UNITY_2022_3_OR_NEWER
            return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
#else
            return Resources.GetBuiltinResource<Font>("Arial.ttf");
#endif
        }

        /// <summary>
        /// Mark specific preview types as needing regeneration
        /// </summary>
        private void MarkPreviewsDirty(PreviewTypeFlags flags)
        {
            _previewTypesToUpdate |= flags;
        }

        public static void ShowWindow()
        {
            CustomPreviewSettingsUI window = GetWindow<CustomPreviewSettingsUI>("Custom Preview Settings");
            window.minSize = new Vector2(1000, 500);
            window.Show();
        }

        private void OnEnable()
        {
            Cleanup();

            // Delay initialization to avoid issues during assembly reloading
            EditorApplication.delayCall += InitializeDelayed;
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.delayCall -= InitializeDelayed;
            EditorApplication.update -= OnEditorUpdate;

            DisposeAnimationPlayers();
            Cleanup();
        }

        private void OnEditorUpdate()
        {
            // Repaint while animations are playing to show frame updates
            if (_playAnimatedPreviews)
            {
                Repaint();
            }
        }

        private void InitializeDelayed()
        {
            InitializePreviewObjects();
        }

        private void OnDestroy()
        {
            Cleanup();
        }

        private void InitializePreviewObjects()
        {
            if (_preview3DObject != null) return;

            // Don't create during compilation or play mode
            if (EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode) return;

            // Create or get the persistent storage scene for preview objects
            if (!_storagePreviewScene.IsValid())
            {
                _storagePreviewScene = EditorSceneManager.NewPreviewScene();
                _storagePreviewScene.name = "AssetInventory_PreviewStorage";
            }

            // Create 3D preview objects (primitives) - kept in memory only
            _preview3DObject = new GameObject("Preview3D");
            _preview3DObject.hideFlags = HideFlags.HideAndDontSave; // Hide from hierarchy and don't save
            _preview3DObject.SetActive(false); // Keep inactive until needed for preview

            // Determine proper shader for current render pipeline
            Shader objectShader;
            if (AssetUtils.IsOnURP())
            {
                objectShader = Shader.Find("Universal Render Pipeline/Lit");
            }
            else if (AssetUtils.IsOnHDRP())
            {
                objectShader = Shader.Find("HDRP/Lit");
            }
            else
            {
                objectShader = Shader.Find("Standard");
            }

            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.parent = _preview3DObject.transform;
            cube.transform.localPosition = new Vector3(-0.6f, 0, 0);
            cube.transform.localScale = Vector3.one * 0.8f;
            if (objectShader != null)
            {
                MeshRenderer cubeRenderer = cube.GetComponent<MeshRenderer>();
                if (cubeRenderer != null)
                {
                    cubeRenderer.material = new Material(objectShader);
                }
            }

            GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.transform.parent = _preview3DObject.transform;
            sphere.transform.localPosition = new Vector3(0.6f, 0, 0);
            sphere.transform.localScale = Vector3.one * 0.8f;
            if (objectShader != null)
            {
                MeshRenderer sphereRenderer = sphere.GetComponent<MeshRenderer>();
                if (sphereRenderer != null)
                {
                    sphereRenderer.material = new Material(objectShader);
                }
            }

            GameObject capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            capsule.transform.parent = _preview3DObject.transform;
            capsule.transform.localPosition = new Vector3(0, -0.8f, 0);
            capsule.transform.localScale = Vector3.one * 0.6f;
            if (objectShader != null)
            {
                MeshRenderer capsuleRenderer = capsule.GetComponent<MeshRenderer>();
                if (capsuleRenderer != null)
                {
                    capsuleRenderer.material = new Material(objectShader);
                }
            }

            SceneManager.MoveGameObjectToScene(_preview3DObject, _storagePreviewScene);

            // Get sample FBX path for preview generation
            _sampleFBXPath = AssetDatabase.GUIDToAssetPath("8353e897096600b4ab25a4ff0d0db42f");

            // Create UI preview object (Canvas with UI elements only, no 3D meshes) - kept in memory only
            _previewUIObject = new GameObject("PreviewUI");
            _previewUIObject.hideFlags = HideFlags.HideAndDontSave;
            _previewUIObject.SetActive(false); // Keep inactive until needed for preview
            Canvas canvas = _previewUIObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            RectTransform canvasRect = canvas.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(200, 200);
            canvasRect.localScale = Vector3.one * 0.01f; // Scale down to fit

            // Add CanvasScaler for proper scaling
            UnityEngine.UI.CanvasScaler scaler = _previewUIObject.AddComponent<UnityEngine.UI.CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 10;

            // Create background panel
            GameObject panel = new GameObject("Panel");
            panel.transform.SetParent(canvas.transform, false);
            UnityEngine.UI.Image panelImage = panel.AddComponent<UnityEngine.UI.Image>();
            panelImage.color = new Color(0.9f, 0.9f, 0.9f, 1f);
            RectTransform panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0.1f, 0.1f);
            panelRect.anchorMax = new Vector2(0.9f, 0.9f);
            panelRect.offsetMin = Vector2.zero;
            panelRect.offsetMax = Vector2.zero;

            // Get built-in font based on Unity version
            Font uiFont = GetBuiltinFont();

            // Add title label
            GameObject titleObj = new GameObject("Title");
            titleObj.transform.SetParent(panel.transform, false);
            UnityEngine.UI.Text titleText = titleObj.AddComponent<UnityEngine.UI.Text>();
            titleText.text = "UI Preview";
            titleText.font = uiFont;
            titleText.fontSize = 18;
            titleText.color = Color.black;
            titleText.alignment = TextAnchor.MiddleCenter;
            RectTransform titleRect = titleObj.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0, 0.7f);
            titleRect.anchorMax = new Vector2(1, 0.95f);
            titleRect.offsetMin = Vector2.zero;
            titleRect.offsetMax = Vector2.zero;

            // Add description label
            GameObject labelObj = new GameObject("Label");
            labelObj.transform.SetParent(panel.transform, false);
            UnityEngine.UI.Text labelText = labelObj.AddComponent<UnityEngine.UI.Text>();
            labelText.text = "This is a sample\nUI canvas with\ntext and buttons";
            labelText.font = uiFont;
            labelText.fontSize = 12;
            labelText.color = new Color(0.3f, 0.3f, 0.3f, 1f);
            labelText.alignment = TextAnchor.MiddleCenter;
            RectTransform labelRect = labelObj.GetComponent<RectTransform>();
            labelRect.anchorMin = new Vector2(0.1f, 0.4f);
            labelRect.anchorMax = new Vector2(0.9f, 0.65f);
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;

            // Add button 1
            GameObject button1 = new GameObject("Button1");
            button1.transform.SetParent(panel.transform, false);
            UnityEngine.UI.Image button1Image = button1.AddComponent<UnityEngine.UI.Image>();
            button1Image.color = new Color(0.2f, 0.6f, 0.9f, 1f);
            UnityEngine.UI.Button button1Comp = button1.AddComponent<UnityEngine.UI.Button>();
            RectTransform button1Rect = button1.GetComponent<RectTransform>();
            button1Rect.anchorMin = new Vector2(0.1f, 0.15f);
            button1Rect.anchorMax = new Vector2(0.45f, 0.32f);
            button1Rect.offsetMin = Vector2.zero;
            button1Rect.offsetMax = Vector2.zero;

            // Button 1 text
            GameObject button1TextObj = new GameObject("Text");
            button1TextObj.transform.SetParent(button1.transform, false);
            UnityEngine.UI.Text button1Text = button1TextObj.AddComponent<UnityEngine.UI.Text>();
            button1Text.text = "Start";
            button1Text.font = uiFont;
            button1Text.fontSize = 14;
            button1Text.color = Color.white;
            button1Text.alignment = TextAnchor.MiddleCenter;
            RectTransform button1TextRect = button1TextObj.GetComponent<RectTransform>();
            button1TextRect.anchorMin = Vector2.zero;
            button1TextRect.anchorMax = Vector2.one;
            button1TextRect.offsetMin = Vector2.zero;
            button1TextRect.offsetMax = Vector2.zero;

            // Add button 2
            GameObject button2 = new GameObject("Button2");
            button2.transform.SetParent(panel.transform, false);
            UnityEngine.UI.Image button2Image = button2.AddComponent<UnityEngine.UI.Image>();
            button2Image.color = new Color(0.9f, 0.3f, 0.3f, 1f);
            UnityEngine.UI.Button button2Comp = button2.AddComponent<UnityEngine.UI.Button>();
            RectTransform button2Rect = button2.GetComponent<RectTransform>();
            button2Rect.anchorMin = new Vector2(0.55f, 0.15f);
            button2Rect.anchorMax = new Vector2(0.9f, 0.32f);
            button2Rect.offsetMin = Vector2.zero;
            button2Rect.offsetMax = Vector2.zero;

            // Button 2 text
            GameObject button2TextObj = new GameObject("Text");
            button2TextObj.transform.SetParent(button2.transform, false);
            UnityEngine.UI.Text button2Text = button2TextObj.AddComponent<UnityEngine.UI.Text>();
            button2Text.text = "Exit";
            button2Text.font = uiFont;
            button2Text.fontSize = 14;
            button2Text.color = Color.white;
            button2Text.alignment = TextAnchor.MiddleCenter;
            RectTransform button2TextRect = button2TextObj.GetComponent<RectTransform>();
            button2TextRect.anchorMin = Vector2.zero;
            button2TextRect.anchorMax = Vector2.one;
            button2TextRect.offsetMin = Vector2.zero;
            button2TextRect.offsetMax = Vector2.zero;
            SceneManager.MoveGameObjectToScene(_previewUIObject, _storagePreviewScene);

            // Create Particle System preview - kept in memory only
            _previewParticleObject = new GameObject("PreviewParticle");
            _previewParticleObject.hideFlags = HideFlags.HideAndDontSave;
            _previewParticleObject.SetActive(false); // Keep inactive until needed for preview
            ParticleSystem ps = _previewParticleObject.AddComponent<ParticleSystem>();
            ParticleSystem.MainModule main = ps.main;
            main.startLifetime = 2.0f;
            main.startSpeed = 5.0f;
            main.startSize = 0.5f;
            main.startColor = new Color(1f, 0.5f, 0.2f, 1f);
            main.maxParticles = 100;
            main.loop = true;
            ParticleSystem.EmissionModule emission = ps.emission;
            emission.rateOverTime = 20;
            ParticleSystem.ShapeModule shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 25f;

            // Assign proper material with compatible shader for current render pipeline
            ParticleSystemRenderer psRenderer = ps.GetComponent<ParticleSystemRenderer>();
            if (psRenderer != null)
            {
                Shader particleShader;
                if (AssetUtils.IsOnURP())
                {
                    particleShader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
                }
                else if (AssetUtils.IsOnHDRP())
                {
                    particleShader = Shader.Find("HDRP/Unlit");
                }
                else
                {
                    particleShader = Shader.Find("Particles/Standard Unlit");
                }

                if (particleShader != null)
                {
                    Material particleMaterial = new Material(particleShader);
                    particleMaterial.name = "PreviewParticleMaterial";
                    psRenderer.material = particleMaterial;
                }
            }

            SceneManager.MoveGameObjectToScene(_previewParticleObject, _storagePreviewScene);

#if USE_VFX
            // Create VFX preview (if VFX Graph is available) - kept in memory only
            _previewVFXObject = new GameObject("PreviewVFX");
            _previewVFXObject.hideFlags = HideFlags.HideAndDontSave;
            _previewVFXObject.SetActive(false); // Keep inactive until needed for preview
            VisualEffect vfx = _previewVFXObject.AddComponent<VisualEffect>();

            // Load the sample VFX asset using hardcoded GUID
            const string SAMPLE_VFX_GUID = "8f85dafc94177704b961f051b65397c5";
            string vfxPath = AssetDatabase.GUIDToAssetPath(SAMPLE_VFX_GUID);
            VisualEffectAsset sampleVFX = null;

            if (!string.IsNullOrEmpty(vfxPath))
            {
                sampleVFX = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(vfxPath);
                if (sampleVFX != null)
                {
                    vfx.visualEffectAsset = sampleVFX;
                }
            }

            if (sampleVFX == null)
            {
                Debug.LogWarning("[VFX Preview Window] Could not find Sample.vfx file in Editor/Images/VFX. VFX preview will not work in settings window.");
            }

            SceneManager.MoveGameObjectToScene(_previewVFXObject, _storagePreviewScene);
#endif

#if UNITY_EDITOR_WIN
            // Load sample video for video preview
            const string SAMPLE_VIDEO_GUID = "b4a6b237d9c064624a26357fed218d25";
            string videoPath = AssetDatabase.GUIDToAssetPath(SAMPLE_VIDEO_GUID);
            if (!string.IsNullOrEmpty(videoPath))
            {
                _sampleVideoClip = AssetDatabase.LoadAssetAtPath<VideoClip>(videoPath);
            }

            if (_sampleVideoClip == null)
            {
                Debug.LogWarning("[Custom Preview Window] Sample video 'asset-inventory-greeting.mp4' not found in project. Video preview will not work in settings window.");
            }
#endif

            // Create sample material for material preview (using render pipeline-appropriate shader)
            if (_sampleMaterial == null)
            {
                Shader materialShader;
                if (AssetUtils.IsOnURP())
                {
                    materialShader = Shader.Find("Universal Render Pipeline/Lit");
                }
                else if (AssetUtils.IsOnHDRP())
                {
                    materialShader = Shader.Find("HDRP/Lit");
                }
                else
                {
                    materialShader = Shader.Find("Standard");
                }

                if (materialShader != null)
                {
                    _sampleMaterial = new Material(materialShader);
                    _sampleMaterial.name = "SamplePreviewMaterial";
                    // Set a nice default color for preview
                    // URP and HDRP use _BaseColor, Built-in uses _Color
                    if (AssetUtils.IsOnURP() || AssetUtils.IsOnHDRP())
                    {
                        _sampleMaterial.SetColor("_BaseColor", new Color(0.7f, 0.3f, 0.3f, 1f));
                        _sampleMaterial.SetFloat("_Metallic", 0.5f);
                        _sampleMaterial.SetFloat("_Smoothness", 0.7f);
                    }
                    else
                    {
                        _sampleMaterial.SetColor("_Color", new Color(0.7f, 0.3f, 0.3f, 1f));
                        _sampleMaterial.SetFloat("_Metallic", 0.5f);
                        _sampleMaterial.SetFloat("_Glossiness", 0.7f);
                    }
                }
            }

            _previewTypesToUpdate = PreviewTypeFlags.All;
        }

        private void Cleanup()
        {
            _isGeneratingPreview = false;

            // Clean up all textures
            if (_static3DTexture != null)
            {
                DestroyImmediate(_static3DTexture);
                _static3DTexture = null;
            }
            if (_animated3DTexture != null)
            {
                DestroyImmediate(_animated3DTexture);
                _animated3DTexture = null;
            }
            if (_staticFBXTexture != null)
            {
                DestroyImmediate(_staticFBXTexture);
                _staticFBXTexture = null;
            }
            if (_animatedFBXTexture != null)
            {
                DestroyImmediate(_animatedFBXTexture);
                _animatedFBXTexture = null;
            }
            if (_staticAnimTexture != null)
            {
                DestroyImmediate(_staticAnimTexture);
                _staticAnimTexture = null;
            }
            if (_animatedAnimTexture != null)
            {
                DestroyImmediate(_animatedAnimTexture);
                _animatedAnimTexture = null;
            }
            if (_staticUITexture != null)
            {
                DestroyImmediate(_staticUITexture);
                _staticUITexture = null;
            }
            if (_staticParticleTexture != null)
            {
                DestroyImmediate(_staticParticleTexture);
                _staticParticleTexture = null;
            }
            if (_animatedParticleTexture != null)
            {
                DestroyImmediate(_animatedParticleTexture);
                _animatedParticleTexture = null;
            }
            if (_staticVFXTexture != null)
            {
                DestroyImmediate(_staticVFXTexture);
                _staticVFXTexture = null;
            }
            if (_animatedVFXTexture != null)
            {
                DestroyImmediate(_animatedVFXTexture);
                _animatedVFXTexture = null;
            }
            if (_previewFontTexture != null)
            {
                DestroyImmediate(_previewFontTexture);
                _previewFontTexture = null;
            }
            if (_staticVideoTexture != null)
            {
                DestroyImmediate(_staticVideoTexture);
                _staticVideoTexture = null;
            }
            if (_animatedVideoTexture != null)
            {
                DestroyImmediate(_animatedVideoTexture);
                _animatedVideoTexture = null;
            }
            if (_staticMaterialTexture != null)
            {
                DestroyImmediate(_staticMaterialTexture);
                _staticMaterialTexture = null;
            }
            if (_sampleMaterial != null)
            {
                DestroyImmediate(_sampleMaterial);
                _sampleMaterial = null;
            }

            // Clean up all game objects
            // Note: GameObjects are in the storage preview scene and will be cleaned up when it closes
            _preview3DObject = null;
            _sampleFBXPath = null;
            _previewUIObject = null;
            _previewParticleObject = null;
            _previewVFXObject = null;

            // Close the storage preview scene (this will clean up all preview GameObjects)
            if (_storagePreviewScene.IsValid())
            {
                EditorSceneManager.ClosePreviewScene(_storagePreviewScene);
                _storagePreviewScene = default(Scene);
            }
        }



        private new void OnGUI()
        {
            if (_preview3DObject == null)
            {
                InitializePreviewObjects();
                if (_preview3DObject == null)
                {
                    EditorGUILayout.LabelField("Initializing preview...");
                    return;
                }
            }

            int boxSpace = 10;

            EditorGUILayout.BeginHorizontal();

            // Left side - Settings
            EditorGUILayout.BeginVertical("box", GUILayout.Width(330));
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            EditorGUILayout.LabelField("Custom Preview Pipeline Settings", EditorStyles.boldLabel);
            EditorGUILayout.Space(10);

            bool settingsChanged = false;

            // Upscaling settings - don't affect preview window, only actual preview generation
            GUILayout.BeginVertical(CommonUIStyles.sectionBox);
            EditorGUI.BeginChangeCheck();
            AI.Config.upscalePreviews = EditorGUILayout.Toggle(CommonUIStyles.Content("Upscale Preview Images", "Resize preview images to make them fill a bigger area of the tiles."), AI.Config.upscalePreviews);

            if (AI.Config.upscalePreviews)
            {
                EditorGUI.indentLevel++;
                if (ShowAdvanced())
                {
                    AI.Config.upscaleLossless = EditorGUILayout.Toggle(CommonUIStyles.Content("Lossless" + (Application.platform == RuntimePlatform.WindowsEditor ? " (Windows)" : ""), "Only create upscaled versions if base resolution is bigger. This will then mostly only affect images which can be previewed at a higher scale but leave prefab previews at the resolution they have through Unity, avoiding scaling artifacts."), AI.Config.upscaleLossless);
                }

                EditorGUILayout.BeginHorizontal();
                AI.Config.upscaleSize = EditorGUILayout.DelayedIntField(CommonUIStyles.Content(AI.Config.upscaleLossless ? "Target Size" : "Minimum Size", "Minimum size the preview image should have. Bigger images are not changed."), AI.Config.upscaleSize);
                EditorGUILayout.LabelField("px", EditorStyles.miniLabel, GUILayout.Width(40));
                EditorGUILayout.EndHorizontal();
                EditorGUI.indentLevel--;
            }
            if (EditorGUI.EndChangeCheck())
            {
                settingsChanged = true;
            }
            GUILayout.EndVertical();

            // Camera Settings - 3D Models Only
            EditorGUILayout.Space(boxSpace);
            GUILayout.BeginVertical(CommonUIStyles.sectionBox);
            EditorGUILayout.LabelField("3D Models", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            AI.Config.generateCustomModelPreviews = EditorGUILayout.Toggle(
                new GUIContent("Enable Custom Previews", "Use custom preview pipeline for 3D models/prefabs. If disabled, uses Unity's default low quality asset preview system."),
                AI.Config.generateCustomModelPreviews);
            if (AI.Config.generateCustomModelPreviews)
            {
                EditorGUI.indentLevel++;
                AI.Config.generateAnimatedModelPreviews = EditorGUILayout.Toggle(
                    new GUIContent("Animated (360°)", "Creates an animated preview rotating around the object."),
                    AI.Config.generateAnimatedModelPreviews);
                EditorGUI.indentLevel--;
            }

            // Sub-option for rotating light with camera during 360° rotation
            if (AI.Config.generateAnimatedModelPreviews)
            {
                EditorGUI.indentLevel++;
                AI.Config.cpRotateLightWith360 = EditorGUILayout.Toggle(
                    new GUIContent("Rotate Lights", "Keep lighting consistent by rotating the light source with the camera during 360° rotation. When enabled, the object will be evenly lit from all angles."),
                    AI.Config.cpRotateLightWith360);
                EditorGUI.indentLevel--;
            }

            // Disable camera settings if custom pipeline is not active for 3D models
            if (AI.Config.generateCustomModelPreviews)
            {
                EditorGUILayout.Space(5);
                AI.Config.cpCameraFOV = EditorGUILayout.Slider(
                    new GUIContent("Field of View", "Camera FOV in degrees for 3D models. Lower = more zoomed in, Higher = wider view."),
                    AI.Config.cpCameraFOV, 0f, 120f);
                AI.Config.cpCameraAngleX = EditorGUILayout.Slider(
                    new GUIContent("Vertical Angle", "Camera pitch angle for 3D models (looking down at object). 0° = eye level, 90° = top-down."),
                    AI.Config.cpCameraAngleX, 0f, 90f);
                AI.Config.cpCameraAngleY = EditorGUILayout.Slider(
                    new GUIContent("Horizontal Angle", "Camera rotation around 3D models. Changes which side is visible."),
                    AI.Config.cpCameraAngleY, 0f, 360f);
                AI.Config.cpFramingPadding = EditorGUILayout.Slider(
                    new GUIContent("Framing Padding", "Padding around 3D models in % of preview size. Lower = tighter framing, Higher = more border."),
                    AI.Config.cpFramingPadding, 0f, 20f);
            }
            if (EditorGUI.EndChangeCheck())
            {
                // Mark both Model3D and Material dirty since they share camera settings (FOV, angles, padding)
                MarkPreviewsDirty(PreviewTypeFlags.Model3D | PreviewTypeFlags.Material);
                settingsChanged = true;
            }
            GUILayout.EndVertical();

            EditorGUILayout.Space(boxSpace);

            // FBX Models & Animations Settings
            GUILayout.BeginVertical(CommonUIStyles.sectionBox);
            EditorGUILayout.LabelField("FBX Models & Animations", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            AI.Config.generateFBXPreviews = EditorGUILayout.Toggle(
                new GUIContent("Enable FBX Previews", "Generate previews for FBX files including animations and models."),
                AI.Config.generateFBXPreviews);

            if (AI.Config.generateFBXPreviews)
            {
                EditorGUI.indentLevel++;
                AI.Config.generateAnimatedFBXPreviews = EditorGUILayout.Toggle(
                    new GUIContent("Animated (Playback)", "Create animated preview when FBX contains animation clips."),
                    AI.Config.generateAnimatedFBXPreviews);
                AI.Config.generate360FBXPreviews = EditorGUILayout.Toggle(
                    new GUIContent("360° Rotation", "Rotate camera around FBX for animated previews (works with or without animations)."),
                    AI.Config.generate360FBXPreviews);
                AI.Config.fbxAnimationPreviewMode = (FBXAnimationPreviewMode)EditorGUILayout.EnumPopup(
                    new GUIContent("Without Avatar", "How to visualize animation-only FBX files without geometry or avatar. Bone Visualization shows skeleton structure, Unity Humanoid Model uses Unity's built-in humanoid."),
                    AI.Config.fbxAnimationPreviewMode);
                EditorGUI.indentLevel--;
            }
            if (EditorGUI.EndChangeCheck())
            {
                MarkPreviewsDirty(PreviewTypeFlags.FBX);
                settingsChanged = true;
            }
            GUILayout.EndVertical();

            EditorGUILayout.Space(boxSpace);

            // Standalone Animation Files Settings
            GUILayout.BeginVertical(CommonUIStyles.sectionBox);
            EditorGUILayout.LabelField("Animation Files (.anim)", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            AI.Config.generateAnimPreviews = EditorGUILayout.Toggle(
                new GUIContent("Enable Anim Previews", "Generate previews for standalone .anim (AnimationClip) files."),
                AI.Config.generateAnimPreviews);

            if (AI.Config.generateAnimPreviews)
            {
                EditorGUI.indentLevel++;
                AI.Config.generateAnimatedAnimPreviews = EditorGUILayout.Toggle(
                    new GUIContent("Animated (Playback)", "Create animated preview spritesheets for .anim files."),
                    AI.Config.generateAnimatedAnimPreviews);
                EditorGUI.indentLevel--;
            }
            if (EditorGUI.EndChangeCheck())
            {
                MarkPreviewsDirty(PreviewTypeFlags.Anim);
                settingsChanged = true;
            }
            GUILayout.EndVertical();

            EditorGUILayout.Space(boxSpace);

            // Materials Settings
            GUILayout.BeginVertical(CommonUIStyles.sectionBox);
            EditorGUILayout.LabelField("Materials", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            AI.Config.generateMaterialPreviews = EditorGUILayout.Toggle(
                new GUIContent("Enable Custom Previews", "Generate custom previews for material files using a 3D mesh."),
                AI.Config.generateMaterialPreviews);

            if (AI.Config.generateMaterialPreviews)
            {
                EditorGUI.indentLevel++;
                AI.Config.materialPreviewMesh = (int)(CustomMaterialPreviewGenerator.PreviewMeshType)EditorGUILayout.EnumPopup(
                    new GUIContent("Preview Mesh", "The mesh shape to use for rendering material previews. Sphere is best for PBR materials."),
                    (CustomMaterialPreviewGenerator.PreviewMeshType)AI.Config.materialPreviewMesh);
                EditorGUI.indentLevel--;
            }
            if (EditorGUI.EndChangeCheck())
            {
                MarkPreviewsDirty(PreviewTypeFlags.Material);
                settingsChanged = true;
            }
            GUILayout.EndVertical();

            EditorGUILayout.Space(boxSpace);

            // UI Previews Settings
            GUILayout.BeginVertical(CommonUIStyles.sectionBox);
            EditorGUILayout.LabelField("UI Previews", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            AI.Config.generateUIPreviews = EditorGUILayout.Toggle(
                new GUIContent("Enable UI Previews", "Generate custom previews for UI/Canvas prefabs. If disabled, no preview will be generated."),
                AI.Config.generateUIPreviews);
            if (EditorGUI.EndChangeCheck())
            {
                MarkPreviewsDirty(PreviewTypeFlags.UI);
                settingsChanged = true;
            }
            GUILayout.EndVertical();

            EditorGUILayout.Space(boxSpace);

            // Font Settings
            GUILayout.BeginVertical(CommonUIStyles.sectionBox);
            EditorGUILayout.LabelField("Fonts", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            AI.Config.generateFontPreviews = EditorGUILayout.Toggle(
                new GUIContent("Enable Font Previews", "Generate custom previews for font files."),
                AI.Config.generateFontPreviews);

            if (AI.Config.generateFontPreviews)
            {
                Color fontColor = Color.black;
                if (!string.IsNullOrEmpty(AI.Config.cpFontColor) &&
                    ColorUtility.TryParseHtmlString("#" + AI.Config.cpFontColor, out Color fc))
                {
                    fontColor = fc;
                }
                Color newFontColor = EditorGUILayout.ColorField(
                    new GUIContent("Font Color", "Color of the text in font previews."),
                    fontColor);
                if (newFontColor != fontColor)
                {
                    AI.Config.cpFontColor = ColorUtility.ToHtmlStringRGBA(newFontColor);
                }
            }
            if (EditorGUI.EndChangeCheck())
            {
                MarkPreviewsDirty(PreviewTypeFlags.Font);
                settingsChanged = true;
            }
            GUILayout.EndVertical();

            EditorGUILayout.Space(boxSpace);

            // Particle Systems Settings
            GUILayout.BeginVertical(CommonUIStyles.sectionBox);
            EditorGUILayout.LabelField("Particle Systems", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            AI.Config.generateParticlePreviews = EditorGUILayout.Toggle(
                new GUIContent("Enable Custom Previews", "Generate custom previews for Particle System prefabs."),
                AI.Config.generateParticlePreviews);

            if (AI.Config.generateParticlePreviews)
            {
                EditorGUI.indentLevel++;
                AI.Config.generateAnimatedParticlePreviews = EditorGUILayout.Toggle(
                    new GUIContent("Animated", "Create animated preview showing particle system over time."),
                    AI.Config.generateAnimatedParticlePreviews);
                EditorGUILayout.BeginHorizontal();
                AI.Config.cpParticleMinimumVisibleDuration = Mathf.Max(0f, EditorGUILayout.DelayedFloatField(
                    CommonUIStyles.Content("Min Visible Duration", "If above 0, animated particle previews play for at least this long by increasing their per-frame interval."),
                    AI.Config.cpParticleMinimumVisibleDuration));
                EditorGUILayout.LabelField("s", EditorStyles.miniLabel, GUILayout.Width(40));
                EditorGUILayout.EndHorizontal();
                EditorGUI.indentLevel--;
            }
            if (EditorGUI.EndChangeCheck())
            {
                MarkPreviewsDirty(PreviewTypeFlags.Particle);
                settingsChanged = true;
            }
            GUILayout.EndVertical();

            EditorGUILayout.Space(boxSpace);

            // VFX Graph Settings
            GUILayout.BeginVertical(CommonUIStyles.sectionBox);
            EditorGUILayout.LabelField("VFX Graph", EditorStyles.boldLabel);
#if USE_VFX
            if (AssetUtils.IsOnURP() || AssetUtils.IsOnHDRP())
            {
                EditorGUI.BeginChangeCheck();
                AI.Config.generateVFXPreviews = EditorGUILayout.Toggle(
                    new GUIContent("Enable Custom Previews", "Generate custom previews for VFX Graph assets."),
                    AI.Config.generateVFXPreviews);

                if (AI.Config.generateVFXPreviews)
                {
                    EditorGUI.indentLevel++;
                    AI.Config.generateAnimatedVFXPreviews = EditorGUILayout.Toggle(
                        new GUIContent("Animated", "Create animated preview showing VFX over time."),
                        AI.Config.generateAnimatedVFXPreviews);
                    EditorGUILayout.BeginHorizontal();
                    AI.Config.cpVFXMinimumVisibleDuration = Mathf.Max(0f, EditorGUILayout.DelayedFloatField(
                        CommonUIStyles.Content("Min Visible Duration", "If above 0, animated VFX previews play for at least this long by increasing their per-frame interval."),
                        AI.Config.cpVFXMinimumVisibleDuration));
                    EditorGUILayout.LabelField("s", EditorStyles.miniLabel, GUILayout.Width(40));
                    EditorGUILayout.EndHorizontal();
                    EditorGUI.indentLevel--;
                }
                if (EditorGUI.EndChangeCheck())
                {
                    MarkPreviewsDirty(PreviewTypeFlags.VFX);
                    settingsChanged = true;
                }
            }
            else
            {
                EditorGUILayout.HelpBox("VFX Graph requires URP or HDRP to function. Please switch to a Scriptable Render Pipeline to enable VFX preview generation.", MessageType.Info);
            }
#elif UNITY_6000_0_OR_NEWER
            EditorGUILayout.HelpBox("VFX Graph package is not installed. Install 'com.unity.visualeffectgraph' to enable VFX preview generation.", MessageType.Info);
            if (GUILayout.Button("Install Visual Effects Graph Package"))
            {
                Client.Add("com.unity.visualeffectgraph");
            }
#else
            EditorGUILayout.HelpBox("VFX Graph previews require Unity 6 and above.", MessageType.Info);
#endif
            GUILayout.EndVertical();

            EditorGUILayout.Space(boxSpace);

            // Videos Settings
            GUILayout.BeginVertical(CommonUIStyles.sectionBox);
            EditorGUILayout.LabelField("Videos", EditorStyles.boldLabel);
#if UNITY_EDITOR_WIN
            EditorGUI.BeginChangeCheck();
            AI.Config.generateVideoPreviews = EditorGUILayout.Toggle(
                new GUIContent("Enable Video Previews", "Generate previews for video files. Only available on Windows."),
                AI.Config.generateVideoPreviews);

            if (AI.Config.generateVideoPreviews)
            {
                EditorGUI.indentLevel++;
                AI.Config.generateAnimatedVideoPreviews = EditorGUILayout.Toggle(
                    new GUIContent("Animated", "Create multi-frame preview showing video frames over time."),
                    AI.Config.generateAnimatedVideoPreviews);
                EditorGUI.indentLevel--;
            }
            if (EditorGUI.EndChangeCheck())
            {
                MarkPreviewsDirty(PreviewTypeFlags.Video);
                settingsChanged = true;
            }
#else
            EditorGUILayout.HelpBox("Video preview generation is only available on Windows due to platform limitations.", MessageType.Info);
#endif
            GUILayout.EndVertical();

            EditorGUILayout.Space(boxSpace);

            // Scenes Settings
            GUILayout.BeginVertical(CommonUIStyles.sectionBox);
            EditorGUILayout.LabelField("Scenes (Experimental)", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            AI.Config.generateScenePreviews = EditorGUILayout.Toggle(
                new GUIContent("Enable Scene Previews", "Generate custom previews for Unity scene files (.unity)."),
                AI.Config.generateScenePreviews);
            if (EditorGUI.EndChangeCheck())
            {
                settingsChanged = true;
            }
            GUILayout.EndVertical();

            EditorGUILayout.Space(boxSpace);

            // Background Settings
            GUILayout.BeginVertical(CommonUIStyles.sectionBox);
            EditorGUILayout.LabelField("Background", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            CustomPreviewBackgroundType newBackgroundType = (CustomPreviewBackgroundType)EditorGUILayout.EnumPopup(
                new GUIContent("Type", "Background type for the preview."),
                AI.Config.cpBackgroundType);
            if (newBackgroundType != AI.Config.cpBackgroundType)
            {
                AI.Config.cpBackgroundType = newBackgroundType;
            }

            EditorGUILayout.Space(5);

            // Show appropriate color controls based on background type
            switch (AI.Config.cpBackgroundType)
            {
                case CustomPreviewBackgroundType.SolidColor:
                    EditorGUI.indentLevel++;
                    // Background Color with hex conversion
                    Color bgColor = new Color(88f / 255, 88f / 255, 88f / 255);
                    if (!string.IsNullOrEmpty(AI.Config.cpBackgroundColor) &&
                        ColorUtility.TryParseHtmlString("#" + AI.Config.cpBackgroundColor, out Color bc))
                        bgColor = bc;
                    Color newBgColor = EditorGUILayout.ColorField("  Color (BiRP/URP)", bgColor);
                    if (newBgColor != bgColor)
                    {
                        AI.Config.cpBackgroundColor = ColorUtility.ToHtmlStringRGBA(newBgColor);
                    }

                    // Background Color HDRP with hex conversion
                    Color bgColorHDRP = new Color(34f / 255, 34f / 255, 34f / 255);
                    if (!string.IsNullOrEmpty(AI.Config.cpBackgroundColorHDRP) &&
                        ColorUtility.TryParseHtmlString("#" + AI.Config.cpBackgroundColorHDRP, out Color bch))
                        bgColorHDRP = bch;
                    Color newBgColorHDRP = EditorGUILayout.ColorField("  Color (HDRP)", bgColorHDRP);
                    if (newBgColorHDRP != bgColorHDRP)
                    {
                        AI.Config.cpBackgroundColorHDRP = ColorUtility.ToHtmlStringRGBA(newBgColorHDRP);
                    }
                    EditorGUI.indentLevel--;
                    break;

                case CustomPreviewBackgroundType.TwoColorGradient:
                    EditorGUI.indentLevel++;
                    Color grad2Top = new Color(0.5f, 0.5f, 0.5f);
                    if (!string.IsNullOrEmpty(AI.Config.cpGradient2TopColor))
                        ColorUtility.TryParseHtmlString("#" + AI.Config.cpGradient2TopColor, out grad2Top);
                    Color newGrad2Top = EditorGUILayout.ColorField("  Top Color", grad2Top);
                    if (newGrad2Top != grad2Top)
                    {
                        AI.Config.cpGradient2TopColor = ColorUtility.ToHtmlStringRGBA(newGrad2Top);
                    }

                    Color grad2Bottom = new Color(0.25f, 0.25f, 0.25f);
                    if (!string.IsNullOrEmpty(AI.Config.cpGradient2BottomColor))
                        ColorUtility.TryParseHtmlString("#" + AI.Config.cpGradient2BottomColor, out grad2Bottom);
                    Color newGrad2Bottom = EditorGUILayout.ColorField("  Bottom Color", grad2Bottom);
                    if (newGrad2Bottom != grad2Bottom)
                    {
                        AI.Config.cpGradient2BottomColor = ColorUtility.ToHtmlStringRGBA(newGrad2Bottom);
                    }

                    float newRotation2 = EditorGUILayout.Slider("  Rotation", AI.Config.cpGradientRotation, 0f, 360f);
                    if (Mathf.Abs(newRotation2 - AI.Config.cpGradientRotation) > 0.01f)
                    {
                        AI.Config.cpGradientRotation = newRotation2;
                    }
                    EditorGUI.indentLevel--;
                    break;

                case CustomPreviewBackgroundType.FourColorGradient:
                    EditorGUI.indentLevel++;
                    // Top-left corner
                    Color grad4TopLeft = new Color(0.5f, 0.5f, 0.5f);
                    if (!string.IsNullOrEmpty(AI.Config.cpGradient4TopLeftColor))
                        ColorUtility.TryParseHtmlString("#" + AI.Config.cpGradient4TopLeftColor, out grad4TopLeft);
                    Color newGrad4TopLeft = EditorGUILayout.ColorField("  Top-Left", grad4TopLeft);
                    if (newGrad4TopLeft != grad4TopLeft)
                    {
                        AI.Config.cpGradient4TopLeftColor = ColorUtility.ToHtmlStringRGBA(newGrad4TopLeft);
                    }

                    // Top-right corner
                    Color grad4TopRight = new Color(0.375f, 0.375f, 0.375f);
                    if (!string.IsNullOrEmpty(AI.Config.cpGradient4TopRightColor))
                        ColorUtility.TryParseHtmlString("#" + AI.Config.cpGradient4TopRightColor, out grad4TopRight);
                    Color newGrad4TopRight = EditorGUILayout.ColorField("  Top-Right", grad4TopRight);
                    if (newGrad4TopRight != grad4TopRight)
                    {
                        AI.Config.cpGradient4TopRightColor = ColorUtility.ToHtmlStringRGBA(newGrad4TopRight);
                    }

                    // Bottom-left corner
                    Color grad4BottomLeft = new Color(0.25f, 0.25f, 0.25f);
                    if (!string.IsNullOrEmpty(AI.Config.cpGradient4BottomLeftColor))
                        ColorUtility.TryParseHtmlString("#" + AI.Config.cpGradient4BottomLeftColor, out grad4BottomLeft);
                    Color newGrad4BottomLeft = EditorGUILayout.ColorField("  Bottom-Left", grad4BottomLeft);
                    if (newGrad4BottomLeft != grad4BottomLeft)
                    {
                        AI.Config.cpGradient4BottomLeftColor = ColorUtility.ToHtmlStringRGBA(newGrad4BottomLeft);
                    }

                    // Bottom-right corner
                    Color grad4BottomRight = new Color(0.1875f, 0.1875f, 0.1875f);
                    if (!string.IsNullOrEmpty(AI.Config.cpGradient4BottomRightColor))
                        ColorUtility.TryParseHtmlString("#" + AI.Config.cpGradient4BottomRightColor, out grad4BottomRight);
                    Color newGrad4BottomRight = EditorGUILayout.ColorField("  Bottom-Right", grad4BottomRight);
                    if (newGrad4BottomRight != grad4BottomRight)
                    {
                        AI.Config.cpGradient4BottomRightColor = ColorUtility.ToHtmlStringRGBA(newGrad4BottomRight);
                    }

                    float newRotation4 = EditorGUILayout.Slider("  Rotation", AI.Config.cpGradientRotation, 0f, 360f);
                    if (Mathf.Abs(newRotation4 - AI.Config.cpGradientRotation) > 0.01f)
                    {
                        AI.Config.cpGradientRotation = newRotation4;
                    }
                    EditorGUI.indentLevel--;
                    break;
            }
            if (EditorGUI.EndChangeCheck())
            {
                MarkPreviewsDirty(PreviewTypeFlags.AllRendered);
                settingsChanged = true;
            }

            EditorGUILayout.Space(boxSpace);

            // Rendering Quality
            EditorGUILayout.LabelField("Rendering Quality", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            AI.Config.cpSuperSamplingMultiplier = EditorGUILayout.IntSlider(
                new GUIContent("Super-sampling", "Render multiplier for higher quality. 2x = render at 2x resolution then downscale."),
                AI.Config.cpSuperSamplingMultiplier, 1, 4);
            if (ShowAdvanced())
            {
                EditorGUILayout.LabelField($"Native render size: {AI.Config.upscaleSize * AI.Config.cpSuperSamplingMultiplier}px", EditorStyles.miniLabel);
            }

            if (ShowAdvanced())
            {
                // Depth buffer dropdown
                int[] depthOptions = {0, 16, 24, 32};
                string[] depthLabels = {"None (0-bit)", "Low (16-bit)", "Standard (24-bit)", "High (32-bit)"};
                int currentDepthIndex = System.Array.IndexOf(depthOptions, AI.Config.cpDepth);
                if (currentDepthIndex == -1) currentDepthIndex = 2; // Default to 24

                int newDepthIndex = EditorGUILayout.Popup(
                    new GUIContent("Depth Buffer", "Depth buffer precision. 24-bit is recommended. Higher values may help with z-fighting but use more memory."),
                    currentDepthIndex,
                    depthLabels);
                AI.Config.cpDepth = depthOptions[newDepthIndex];
            }
            if (EditorGUI.EndChangeCheck())
            {
                MarkPreviewsDirty(PreviewTypeFlags.AllRendered);
                settingsChanged = true;
            }

            EditorGUILayout.Space(boxSpace);

            // Lighting Settings
            EditorGUILayout.LabelField("Lighting", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            AI.Config.cpUseDirectionalLight = EditorGUILayout.Toggle(
                new GUIContent("Directional Light", "Use directional (sun-like) light. If off, uses point light."),
                AI.Config.cpUseDirectionalLight);

            // Light Color with hex conversion
            Color lightColor = Color.white;
            if (!string.IsNullOrEmpty(AI.Config.cpLightColor) && ColorUtility.TryParseHtmlString("#" + AI.Config.cpLightColor, out Color lc))
                lightColor = lc;
            Color newLightColor = EditorGUILayout.ColorField(new GUIContent("Light Color", "Color tint of the light source."), lightColor);
            if (newLightColor != lightColor)
                AI.Config.cpLightColor = ColorUtility.ToHtmlStringRGBA(newLightColor);

            EditorGUILayout.LabelField("Light Intensity (per render pipeline)", EditorStyles.miniLabel);
            AI.Config.cpLightIntensity = EditorGUILayout.Slider("  Built-in RP", AI.Config.cpLightIntensity, 0f, 2f);
            AI.Config.cpLightIntensityURP = EditorGUILayout.Slider("  URP", AI.Config.cpLightIntensityURP, 0f, 2f);
            AI.Config.cpLightIntensityHDRP = EditorGUILayout.Slider("  HDRP (Lux)", AI.Config.cpLightIntensityHDRP, 0f, 20000f);

            EditorGUILayout.LabelField("Light Rotation", EditorStyles.miniLabel);
            AI.Config.cpLightRotationX = EditorGUILayout.Slider("  Pitch", AI.Config.cpLightRotationX, 0f, 360f);
            AI.Config.cpLightRotationY = EditorGUILayout.Slider("  Yaw", AI.Config.cpLightRotationY, 0f, 360f);

            EditorGUILayout.Space(10);

            // Secondary Light Settings
            AI.Config.cpUseSecondaryLight = EditorGUILayout.Toggle(
                new GUIContent("Use Secondary Light", "Enable a secondary rim/fill light for better depth perception on 3D models. Provides subtle backlighting similar to Unity's default preview system."),
                AI.Config.cpUseSecondaryLight);

            if (AI.Config.cpUseSecondaryLight)
            {
                EditorGUI.indentLevel++;

                // Secondary Light Color
                Color secondaryLightColor = new Color(0.4f, 0.4f, 0.45f);
                if (!string.IsNullOrEmpty(AI.Config.cpSecondaryLightColor) &&
                    ColorUtility.TryParseHtmlString("#" + AI.Config.cpSecondaryLightColor, out Color slc))
                    secondaryLightColor = slc;
                Color newSecondaryLightColor = EditorGUILayout.ColorField(
                    new GUIContent("Light Color", "Color tint of the secondary light source. Default is a subtle blue-grey."),
                    secondaryLightColor);
                if (newSecondaryLightColor != secondaryLightColor)
                    AI.Config.cpSecondaryLightColor = ColorUtility.ToHtmlStringRGBA(newSecondaryLightColor);

                AI.Config.cpSecondaryLightIntensityMultiplier = EditorGUILayout.Slider(
                    new GUIContent("Intensity Multiplier", "Secondary light intensity as a percentage of main light (0.7 = 70% of main light intensity)."),
                    AI.Config.cpSecondaryLightIntensityMultiplier, 0f, 2f);

                EditorGUILayout.LabelField("Secondary Light Rotation", EditorStyles.miniLabel);
                AI.Config.cpSecondaryLightRotationX = EditorGUILayout.Slider("  Pitch", AI.Config.cpSecondaryLightRotationX, 0f, 360f);
                AI.Config.cpSecondaryLightRotationY = EditorGUILayout.Slider("  Yaw", AI.Config.cpSecondaryLightRotationY, 0f, 360f);

                EditorGUI.indentLevel--;
            }
            if (EditorGUI.EndChangeCheck())
            {
                MarkPreviewsDirty(PreviewTypeFlags.AllRendered);
                settingsChanged = true;
            }

            EditorGUILayout.Space(boxSpace);

            // Environment Settings
            EditorGUILayout.LabelField("Environment", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            AI.Config.cpAmbientIntensity = EditorGUILayout.Slider(
                new GUIContent("Ambient Intensity", "Global ambient light intensity multiplier. Affects overall scene brightness."),
                AI.Config.cpAmbientIntensity, 0f, 2f);
            if (EditorGUI.EndChangeCheck())
            {
                MarkPreviewsDirty(PreviewTypeFlags.AllRendered);
                settingsChanged = true;
            }
            EditorGUILayout.Space(10);

            // Animation Settings
            EditorGUILayout.LabelField("Animated Previews", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            AI.Config.animationGrid = EditorGUILayout.IntSlider(CommonUIStyles.Content("Animation Frames", "Number of frames to create for the preview of animated objects (e.g. videos), evenly spread across the animation. Higher frames require more storage space. Recommended are 3 or 4."), AI.Config.animationGrid, 2, 6);
            if (EditorGUI.EndChangeCheck())
            {
                MarkPreviewsDirty(PreviewTypeFlags.AllAnimated);
                settingsChanged = true;
            }
            EditorGUILayout.LabelField("will be squared, e.g. 4 = 16 frames", EditorStyles.miniLabel);

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.BeginHorizontal();
            AI.Config.animationSpeed = EditorGUILayout.DelayedFloatField(CommonUIStyles.Content("Animation Speed", "Time interval until a new frame of the animation is shown in seconds."), AI.Config.animationSpeed);
            EditorGUILayout.LabelField("s", EditorStyles.miniLabel, GUILayout.Width(40));
            EditorGUILayout.EndHorizontal();
            if (EditorGUI.EndChangeCheck())
            {
                settingsChanged = true;
            }
            EditorGUILayout.Space(5);
            EditorGUI.BeginChangeCheck();
            AI.Config.embedAnimatedPreviewIndicator = EditorGUILayout.Toggle(
                new GUIContent("Embed Indicator", "Embed a small play icon in the bottom right corner of static previews when an animated preview exists."),
                AI.Config.embedAnimatedPreviewIndicator);
            if (EditorGUI.EndChangeCheck())
            {
                settingsChanged = true;
            }
            EditorGUILayout.Space(10);

            GUILayout.EndVertical();

            EditorGUILayout.Space();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Reset to Defaults"))
            {
                ResetToDefaults();
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            // Right side - Previews
            EditorGUILayout.BeginVertical();
            _previewScrollPosition = EditorGUILayout.BeginScrollView(_previewScrollPosition, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

            EditorGUILayout.LabelField("Preview Gallery - Custom Types", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            // Static Previews - All in one horizontal row
            EditorGUILayout.LabelField("Static Previews", EditorStyles.miniLabel);
            EditorGUILayout.BeginHorizontal(GUILayout.ExpandWidth(false));
            EditorGUILayout.Space();

            // 3D Model
            EditorGUILayout.BeginVertical(GUILayout.Width(PREVIEW_SIZE));
            EditorGUILayout.LabelField("3D Model", EditorStyles.centeredGreyMiniLabel);
            if (AI.Config.generateCustomModelPreviews)
            {
                DrawPreviewBox(_static3DTexture, PREVIEW_SIZE);
            }
            else
            {
                DrawPreviewBox(null, PREVIEW_SIZE, "Custom previews disabled");
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(10);

            // FBX Model
            EditorGUILayout.BeginVertical(GUILayout.Width(PREVIEW_SIZE));
            EditorGUILayout.LabelField("FBX Model", EditorStyles.centeredGreyMiniLabel);
            if (AI.Config.generateFBXPreviews)
            {
                DrawPreviewBox(_staticFBXTexture, PREVIEW_SIZE, string.IsNullOrEmpty(_sampleFBXPath) ? "No sample FBX" : null);
            }
            else
            {
                DrawPreviewBox(null, PREVIEW_SIZE, "FBX previews disabled");
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(10);

            // Anim File (only shown in debug mode since they are basically identical to FBX from the outcome)
            if (AI.DEBUG_MODE)
            {
                EditorGUILayout.BeginVertical(GUILayout.Width(PREVIEW_SIZE));
                EditorGUILayout.LabelField("Anim File", EditorStyles.centeredGreyMiniLabel);
                if (AI.Config.generateAnimPreviews)
                {
                    DrawPreviewBox(_staticAnimTexture, PREVIEW_SIZE, string.IsNullOrEmpty(_sampleFBXPath) ? "No sample" : null);
                }
                else
                {
                    DrawPreviewBox(null, PREVIEW_SIZE, "Anim previews disabled");
                }
                EditorGUILayout.EndVertical();

                EditorGUILayout.Space(10);
            }

            // Material Preview
            EditorGUILayout.BeginVertical(GUILayout.Width(PREVIEW_SIZE));
            EditorGUILayout.LabelField("Material", EditorStyles.centeredGreyMiniLabel);
            if (AI.Config.generateMaterialPreviews)
            {
                DrawPreviewBox(_staticMaterialTexture, PREVIEW_SIZE);
            }
            else
            {
                DrawPreviewBox(null, PREVIEW_SIZE, "Material previews disabled");
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(10);

            // Particle System
            EditorGUILayout.BeginVertical(GUILayout.Width(PREVIEW_SIZE));
            EditorGUILayout.LabelField("Particle System", EditorStyles.centeredGreyMiniLabel);
            if (AI.Config.generateParticlePreviews)
            {
                DrawPreviewBox(_staticParticleTexture, PREVIEW_SIZE);
            }
            else
            {
                DrawPreviewBox(null, PREVIEW_SIZE, "Particle previews disabled");
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(10);

            // VFX Graph
            EditorGUILayout.BeginVertical(GUILayout.Width(PREVIEW_SIZE));
#if USE_VFX
            if (AssetUtils.IsOnURP() || AssetUtils.IsOnHDRP())
            {
                EditorGUILayout.LabelField("VFX Graph", EditorStyles.centeredGreyMiniLabel);
                if (AI.Config.generateVFXPreviews)
                {
                    DrawPreviewBox(_staticVFXTexture, PREVIEW_SIZE);
                }
                else
                {
                    DrawPreviewBox(null, PREVIEW_SIZE, "VFX previews disabled");
                }
            }
            else
            {
                EditorGUILayout.LabelField("VFX Graph (N/A)", EditorStyles.centeredGreyMiniLabel);
                DrawPreviewBox(null, PREVIEW_SIZE, "VFX requires URP/HDRP");
            }
#else
            EditorGUILayout.LabelField("VFX Graph (N/A)", EditorStyles.centeredGreyMiniLabel);
            DrawPreviewBox(null, PREVIEW_SIZE, "VFX Graph not available");
#endif
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(10);

            // UI Canvas
            EditorGUILayout.BeginVertical(GUILayout.Width(PREVIEW_SIZE));
            EditorGUILayout.LabelField("UI Canvas", EditorStyles.centeredGreyMiniLabel);
            if (AI.Config.generateUIPreviews)
            {
                DrawPreviewBox(_staticUITexture, PREVIEW_SIZE);
            }
            else
            {
                DrawPreviewBox(null, PREVIEW_SIZE, "UI previews disabled");
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(10);

            // Font Preview
            EditorGUILayout.BeginVertical(GUILayout.Width(PREVIEW_SIZE));
#if UNITY_2022_3_OR_NEWER
            EditorGUILayout.LabelField("Font (LegacyRuntime)", EditorStyles.centeredGreyMiniLabel);
#else
            EditorGUILayout.LabelField("Font (Arial)", EditorStyles.centeredGreyMiniLabel);
#endif
            if (AI.Config.generateFontPreviews)
            {
                DrawPreviewBox(_previewFontTexture, PREVIEW_SIZE);
            }
            else
            {
                DrawPreviewBox(null, PREVIEW_SIZE, "Font previews disabled");
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(10);

#if UNITY_EDITOR_WIN
            // Video Preview
            EditorGUILayout.BeginVertical(GUILayout.Width(PREVIEW_SIZE));
            EditorGUILayout.LabelField("Video", EditorStyles.centeredGreyMiniLabel);
            if (AI.Config.generateVideoPreviews)
            {
                DrawPreviewBox(_staticVideoTexture, PREVIEW_SIZE);
            }
            else
            {
                DrawPreviewBox(null, PREVIEW_SIZE, "Video previews disabled");
            }
            EditorGUILayout.EndVertical();
#endif

            EditorGUILayout.Space();

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(15);

            // Animated Previews - Horizontal layout (aligned with static previews above)
            EditorGUILayout.LabelField("Animated Previews", EditorStyles.miniLabel);

            // Toggle between sprite sheet view and animation playback
            GUIContent toggleContent = _playAnimatedPreviews
                ? new GUIContent("Playing", "Click to show sprite sheet")
                : new GUIContent("Sprite Sheet", "Click to play animation");
            if (GUILayout.Button(toggleContent, EditorStyles.miniButton, GUILayout.Width(90)))
            {
                _playAnimatedPreviews = !_playAnimatedPreviews;
                if (_playAnimatedPreviews)
                {
                    InitializeAnimationPlayers();
                }
                else
                {
                    DisposeAnimationPlayers();
                }
            }

            EditorGUILayout.BeginHorizontal(GUILayout.ExpandWidth(false));
            EditorGUILayout.Space();

            // 3D (360°) - aligns with 3D Model above
            EditorGUILayout.BeginVertical(GUILayout.Width(PREVIEW_SIZE));
            if (!AI.Config.generateCustomModelPreviews)
            {
                EditorGUILayout.LabelField("-", EditorStyles.centeredGreyMiniLabel);
                DrawPreviewBox(null, PREVIEW_SIZE, "Custom previews disabled");
            }
            else if (AI.Config.generateAnimatedModelPreviews)
            {
                EditorGUILayout.LabelField("3D (360°)", EditorStyles.centeredGreyMiniLabel);
                DrawAnimatedPreviewBox(_animated3DTexture, _anim3DPlayer, PREVIEW_SIZE);
            }
            else
            {
                EditorGUILayout.LabelField("-", EditorStyles.centeredGreyMiniLabel);
                DrawPreviewBox(null, PREVIEW_SIZE, "Enable 360° rotation");
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(10);

            // FBX (Anim) - aligns with FBX Model above
            EditorGUILayout.BeginVertical(GUILayout.Width(PREVIEW_SIZE));
            if (!AI.Config.generateFBXPreviews)
            {
                EditorGUILayout.LabelField("-", EditorStyles.centeredGreyMiniLabel);
                DrawPreviewBox(null, PREVIEW_SIZE, "FBX previews disabled");
            }
            else if (AI.Config.generateAnimatedFBXPreviews)
            {
                EditorGUILayout.LabelField("FBX (Anim)", EditorStyles.centeredGreyMiniLabel);
                DrawAnimatedPreviewBox(_animatedFBXTexture, _animFBXPlayer, PREVIEW_SIZE, string.IsNullOrEmpty(_sampleFBXPath) ? "No sample FBX" : null);
            }
            else
            {
                EditorGUILayout.LabelField("-", EditorStyles.centeredGreyMiniLabel);
                DrawPreviewBox(null, PREVIEW_SIZE, "Enable animated");
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(10);

            // Anim (Anim) - aligns with Anim File above (only shown in debug mode)
            if (AI.DEBUG_MODE)
            {
                EditorGUILayout.BeginVertical(GUILayout.Width(PREVIEW_SIZE));
                if (!AI.Config.generateAnimPreviews)
                {
                    EditorGUILayout.LabelField("-", EditorStyles.centeredGreyMiniLabel);
                    DrawPreviewBox(null, PREVIEW_SIZE, "Anim previews disabled");
                }
                else if (AI.Config.generateAnimatedAnimPreviews)
                {
                    EditorGUILayout.LabelField("Anim (Anim)", EditorStyles.centeredGreyMiniLabel);
                    DrawAnimatedPreviewBox(_animatedAnimTexture, _animAnimPlayer, PREVIEW_SIZE, string.IsNullOrEmpty(_sampleFBXPath) ? "No sample" : null);
                }
                else
                {
                    EditorGUILayout.LabelField("-", EditorStyles.centeredGreyMiniLabel);
                    DrawPreviewBox(null, PREVIEW_SIZE, "Enable animated");
                }
                EditorGUILayout.EndVertical();

                EditorGUILayout.Space(10);
            }

            EditorGUILayout.Space(10);

            // Material (N/A) - aligns with Material above (no animated version)
            EditorGUILayout.BeginVertical(GUILayout.Width(PREVIEW_SIZE));
            EditorGUILayout.LabelField("-", EditorStyles.centeredGreyMiniLabel);
            DrawPreviewBox(null, PREVIEW_SIZE, "Static only");
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(10);

            // Particles (Anim) - aligns with Particle System above
            EditorGUILayout.BeginVertical(GUILayout.Width(PREVIEW_SIZE));
            if (!AI.Config.generateParticlePreviews)
            {
                EditorGUILayout.LabelField("-", EditorStyles.centeredGreyMiniLabel);
                DrawPreviewBox(null, PREVIEW_SIZE, "Particle previews disabled");
            }
            else if (AI.Config.generateAnimatedParticlePreviews)
            {
                EditorGUILayout.LabelField("Particles (Anim)", EditorStyles.centeredGreyMiniLabel);
                DrawAnimatedPreviewBox(_animatedParticleTexture, _animParticlePlayer, PREVIEW_SIZE);
            }
            else
            {
                EditorGUILayout.LabelField("-", EditorStyles.centeredGreyMiniLabel);
                DrawPreviewBox(null, PREVIEW_SIZE, "Enable animated");
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(10);

            // VFX (Anim) - aligns with VFX Graph above
            EditorGUILayout.BeginVertical(GUILayout.Width(PREVIEW_SIZE));
#if USE_VFX
            if (AssetUtils.IsOnURP() || AssetUtils.IsOnHDRP())
            {
                if (!AI.Config.generateVFXPreviews)
                {
                    EditorGUILayout.LabelField("-", EditorStyles.centeredGreyMiniLabel);
                    DrawPreviewBox(null, PREVIEW_SIZE, "VFX previews disabled");
                }
                else if (AI.Config.generateAnimatedVFXPreviews)
                {
                    EditorGUILayout.LabelField("VFX (Anim)", EditorStyles.centeredGreyMiniLabel);
                    DrawAnimatedPreviewBox(_animatedVFXTexture, _animVFXPlayer, PREVIEW_SIZE);
                }
                else
                {
                    EditorGUILayout.LabelField("-", EditorStyles.centeredGreyMiniLabel);
                    DrawPreviewBox(null, PREVIEW_SIZE, "Enable animated");
                }
            }
            else
            {
                EditorGUILayout.LabelField("VFX (N/A)", EditorStyles.centeredGreyMiniLabel);
                DrawPreviewBox(null, PREVIEW_SIZE, "VFX requires URP/HDRP");
            }
#else
            EditorGUILayout.LabelField("VFX (N/A)", EditorStyles.centeredGreyMiniLabel);
            DrawPreviewBox(null, PREVIEW_SIZE, "VFX Graph not available");
#endif
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(10);

            // UI Canvas - no animation, empty placeholder for alignment
            EditorGUILayout.BeginVertical(GUILayout.Width(PREVIEW_SIZE));
            EditorGUILayout.LabelField("-", EditorStyles.centeredGreyMiniLabel);
            DrawPreviewBox(null, PREVIEW_SIZE, "UI has no animation");
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(10);

            // Font - no animation, empty placeholder for alignment
            EditorGUILayout.BeginVertical(GUILayout.Width(PREVIEW_SIZE));
            EditorGUILayout.LabelField("-", EditorStyles.centeredGreyMiniLabel);
            DrawPreviewBox(null, PREVIEW_SIZE, "Fonts have no animation");
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(10);

#if UNITY_EDITOR_WIN
            // Video (Anim) - aligns with Video above
            EditorGUILayout.BeginVertical(GUILayout.Width(PREVIEW_SIZE));
            if (!AI.Config.generateVideoPreviews)
            {
                EditorGUILayout.LabelField("-", EditorStyles.centeredGreyMiniLabel);
                DrawPreviewBox(null, PREVIEW_SIZE, "Video previews disabled");
            }
            else if (AI.Config.generateAnimatedVideoPreviews)
            {
                EditorGUILayout.LabelField("Video (Anim)", EditorStyles.centeredGreyMiniLabel);
                DrawAnimatedPreviewBox(_animatedVideoTexture, _animVideoPlayer, PREVIEW_SIZE);
            }
            else
            {
                EditorGUILayout.LabelField("-", EditorStyles.centeredGreyMiniLabel);
                DrawPreviewBox(null, PREVIEW_SIZE, "Enable multi-frame");
            }
            EditorGUILayout.EndVertical();
#endif

            EditorGUILayout.Space();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();

            // Save config if settings changed
            if (settingsChanged)
            {
                AI.SaveConfig();
            }

            // Throttled preview update - only regenerate affected preview types
            if (_previewTypesToUpdate != PreviewTypeFlags.None && !_isGeneratingPreview && (Time.realtimeSinceStartup - _lastUpdateTime) > UPDATE_THROTTLE)
            {
                _lastUpdateTime = Time.realtimeSinceStartup;
                PreviewTypeFlags typesToUpdate = _previewTypesToUpdate;
                _previewTypesToUpdate = PreviewTypeFlags.None;
                EditorCoroutineUtility.StartCoroutineOwnerless(UpdatePreviewAsync(typesToUpdate));
            }
        }

        private void DrawPreviewBox(Texture2D texture, int size, string fallbackText = null)
        {
            Rect previewRect = GUILayoutUtility.GetRect(size, size, GUILayout.ExpandWidth(false));

            if (texture != null)
            {
                EditorGUI.DrawPreviewTexture(previewRect, texture);
            }
            else if (_isGeneratingPreview)
            {
                EditorGUI.DrawRect(previewRect, new Color(0.2f, 0.2f, 0.2f));
                EditorGUI.LabelField(previewRect, "Generating...", new GUIStyle(EditorStyles.label) {alignment = TextAnchor.MiddleCenter, normal = {textColor = Color.white}});
            }
            else if (!string.IsNullOrEmpty(fallbackText))
            {
                EditorGUI.DrawRect(previewRect, new Color(0.15f, 0.15f, 0.15f));
                EditorGUI.LabelField(previewRect, fallbackText, new GUIStyle(EditorStyles.label) {alignment = TextAnchor.MiddleCenter, normal = {textColor = Color.gray}, wordWrap = true});
            }
            else
            {
                EditorGUI.DrawRect(previewRect, new Color(0.2f, 0.2f, 0.2f));
            }
        }

        private void DrawAnimatedPreviewBox(Texture2D spriteSheetTexture, AnimationPlayer player, int size, string fallbackText = null)
        {
            Rect previewRect = GUILayoutUtility.GetRect(size, size, GUILayout.ExpandWidth(false));

            Texture2D textureToDraw = null;

            if (_playAnimatedPreviews && player != null && player.IsLoaded)
            {
                // Get current frame from animation player
                textureToDraw = player.GetCurrentFrame();
            }

            // Fall back to sprite sheet if no frame available
            if (textureToDraw == null)
            {
                textureToDraw = spriteSheetTexture;
            }

            if (textureToDraw != null)
            {
                EditorGUI.DrawPreviewTexture(previewRect, textureToDraw);
            }
            else if (_isGeneratingPreview)
            {
                EditorGUI.DrawRect(previewRect, new Color(0.2f, 0.2f, 0.2f));
                EditorGUI.LabelField(previewRect, "Generating...", new GUIStyle(EditorStyles.label) {alignment = TextAnchor.MiddleCenter, normal = {textColor = Color.white}});
            }
            else if (!string.IsNullOrEmpty(fallbackText))
            {
                EditorGUI.DrawRect(previewRect, new Color(0.15f, 0.15f, 0.15f));
                EditorGUI.LabelField(previewRect, fallbackText, new GUIStyle(EditorStyles.label) {alignment = TextAnchor.MiddleCenter, normal = {textColor = Color.gray}, wordWrap = true});
            }
            else
            {
                EditorGUI.DrawRect(previewRect, new Color(0.2f, 0.2f, 0.2f));
            }
        }

        internal static bool HasRenderableVFXSettingsPreview(Texture2D texture)
        {
            if (texture == null) return false;

            Color[] pixels = texture.GetPixels();
            if (pixels.Length == 0) return false;

            int vividPixels = 0;
            float minLuminance = 1f;
            float maxLuminance = 0f;

            for (int i = 0; i < pixels.Length; i++)
            {
                Color pixel = pixels[i];
                float brightest = Mathf.Max(pixel.r, Mathf.Max(pixel.g, pixel.b));
                float darkest = Mathf.Min(pixel.r, Mathf.Min(pixel.g, pixel.b));
                float luminance = pixel.r * 0.2126f + pixel.g * 0.7152f + pixel.b * 0.0722f;

                minLuminance = Mathf.Min(minLuminance, luminance);
                maxLuminance = Mathf.Max(maxLuminance, luminance);

                if (brightest > 0.45f && brightest - darkest > 0.08f)
                {
                    vividPixels++;
                }
            }

            int minimumVividPixels = Mathf.Max(8, Mathf.CeilToInt(pixels.Length * 0.001f));
            return vividPixels >= minimumVividPixels || maxLuminance - minLuminance > 0.25f;
        }

        private static Texture2D ResolveVFXSettingsPreviewTexture(Texture2D generatedTexture)
        {
            if (HasRenderableVFXSettingsPreview(generatedTexture))
            {
                return generatedTexture;
            }

            if (generatedTexture != null)
            {
                DestroyImmediate(generatedTexture);
            }

            return null;
        }

        private bool HasPreviewVFXAsset()
        {
#if USE_VFX
            if (_previewVFXObject == null) return false;

            VisualEffect vfx = _previewVFXObject.GetComponent<VisualEffect>();
            return vfx != null && vfx.visualEffectAsset != null;
#else
            return false;
#endif
        }

        private void InitializeAnimationPlayers()
        {
            int frameGrid = AI.Config.animationGrid;
            int frameCount = frameGrid * frameGrid;

            // Initialize 3D animation player
            if (_animated3DTexture != null)
            {
                _anim3DPlayer?.Dispose();
                _anim3DPlayer = new AnimationPlayer("preview_3d");
                _anim3DPlayer.LoadFromTexture(_animated3DTexture, frameGrid);
            }

            // Initialize FBX animation player
            if (_animatedFBXTexture != null)
            {
                _animFBXPlayer?.Dispose();
                _animFBXPlayer = new AnimationPlayer("preview_fbx");
                _animFBXPlayer.LoadFromTexture(_animatedFBXTexture, frameGrid);
            }

            // Initialize Anim animation player
            if (_animatedAnimTexture != null)
            {
                _animAnimPlayer?.Dispose();
                _animAnimPlayer = new AnimationPlayer("preview_anim");
                _animAnimPlayer.LoadFromTexture(_animatedAnimTexture, frameGrid);
            }

            // Initialize Particle animation player
            if (_animatedParticleTexture != null)
            {
                _animParticlePlayer?.Dispose();
                _animParticlePlayer = new AnimationPlayer("preview_particle");
                float frameInterval = AnimationPlayer.ResolveFrameInterval(AI.Config.animationSpeed, AI.Config.cpParticleMinimumVisibleDuration, frameCount);
                _animParticlePlayer.LoadFromTexture(_animatedParticleTexture, frameGrid, frameInterval);
            }

            // Initialize VFX animation player
            if (_animatedVFXTexture != null)
            {
                _animVFXPlayer?.Dispose();
                _animVFXPlayer = new AnimationPlayer("preview_vfx");
                float frameInterval = AnimationPlayer.ResolveFrameInterval(AI.Config.animationSpeed, AI.Config.cpVFXMinimumVisibleDuration, frameCount);
                _animVFXPlayer.LoadFromTexture(_animatedVFXTexture, frameGrid, frameInterval);
            }

            // Initialize Video animation player
            if (_animatedVideoTexture != null)
            {
                _animVideoPlayer?.Dispose();
                _animVideoPlayer = new AnimationPlayer("preview_video");
                _animVideoPlayer.LoadFromTexture(_animatedVideoTexture, frameGrid);
            }
        }

        private void DisposeAnimationPlayers()
        {
            _anim3DPlayer?.Dispose();
            _anim3DPlayer = null;

            _animFBXPlayer?.Dispose();
            _animFBXPlayer = null;

            _animAnimPlayer?.Dispose();
            _animAnimPlayer = null;

            _animParticlePlayer?.Dispose();
            _animParticlePlayer = null;

            _animVFXPlayer?.Dispose();
            _animVFXPlayer = null;

            _animVideoPlayer?.Dispose();
            _animVideoPlayer = null;
        }

        private IEnumerator UpdatePreviewAsync(PreviewTypeFlags typesToUpdate)
        {
            if (_preview3DObject == null)
                yield break;

            _isGeneratingPreview = true;
            Repaint();

            int frameCount = AI.Config.animationGrid * AI.Config.animationGrid;

            // Generate 3D previews (only if custom pipeline is enabled for 3D models)
            if ((typesToUpdate & PreviewTypeFlags.Model3D) != 0)
            {
                if (_preview3DObject != null && AI.Config.generateCustomModelPreviews)
                {
                    Task<Texture2D> static3DTask = CustomPrefabPreviewGenerator.Create(_preview3DObject, PREVIEW_SIZE, 1);
                    while (!static3DTask.IsCompleted) yield return null;
                    if (static3DTask.IsCompletedSuccessfully && static3DTask.Result != null)
                    {
                        if (_static3DTexture != null) DestroyImmediate(_static3DTexture);
                        _static3DTexture = static3DTask.Result;
                    }

                    if (AI.Config.generateAnimatedModelPreviews && _preview3DObject != null)
                    {
                        Task<Texture2D> animated3DTask = CustomPrefabPreviewGenerator.Create(_preview3DObject, PREVIEW_SIZE, frameCount);
                        while (!animated3DTask.IsCompleted) yield return null;
                        if (animated3DTask.IsCompletedSuccessfully && animated3DTask.Result != null)
                        {
                            if (_animated3DTexture != null) DestroyImmediate(_animated3DTexture);
                            _animated3DTexture = animated3DTask.Result;
                        }
                    }
                    else if (_animated3DTexture != null)
                    {
                        DestroyImmediate(_animated3DTexture);
                        _animated3DTexture = null;
                    }
                }
                else if (!AI.Config.generateCustomModelPreviews)
                {
                    // Clear 3D previews if custom pipeline is disabled
                    if (_static3DTexture != null)
                    {
                        DestroyImmediate(_static3DTexture);
                        _static3DTexture = null;
                    }
                    if (_animated3DTexture != null)
                    {
                        DestroyImmediate(_animated3DTexture);
                        _animated3DTexture = null;
                    }
                }
            }

            // Generate FBX previews using sample FBX with animation
            if ((typesToUpdate & PreviewTypeFlags.FBX) != 0)
            {
                if (!string.IsNullOrEmpty(_sampleFBXPath) && AI.Config.generateFBXPreviews)
                {
                    // Generate static preview
                    Task<Texture2D> staticFBXTask = CustomPrefabPreviewGenerator.CreateFBX(_sampleFBXPath, PREVIEW_SIZE, 1, 1);
                    while (!staticFBXTask.IsCompleted) yield return null;
                    if (staticFBXTask.IsCompletedSuccessfully && staticFBXTask.Result != null)
                    {
                        if (_staticFBXTexture != null) DestroyImmediate(_staticFBXTexture);
                        _staticFBXTexture = staticFBXTask.Result;
                    }

                    // Generate animated preview with animation playback
                    if (AI.Config.generateAnimatedFBXPreviews)
                    {
                        Task<Texture2D> animatedFBXTask = CustomPrefabPreviewGenerator.CreateFBX(_sampleFBXPath, PREVIEW_SIZE, frameCount, 1);
                        while (!animatedFBXTask.IsCompleted) yield return null;
                        if (animatedFBXTask.IsCompletedSuccessfully && animatedFBXTask.Result != null)
                        {
                            if (_animatedFBXTexture != null) DestroyImmediate(_animatedFBXTexture);
                            _animatedFBXTexture = animatedFBXTask.Result;
                        }
                    }
                    else if (_animatedFBXTexture != null)
                    {
                        DestroyImmediate(_animatedFBXTexture);
                        _animatedFBXTexture = null;
                    }
                }
                else
                {
                    if (_staticFBXTexture != null)
                    {
                        DestroyImmediate(_staticFBXTexture);
                        _staticFBXTexture = null;
                    }
                    if (_animatedFBXTexture != null)
                    {
                        DestroyImmediate(_animatedFBXTexture);
                        _animatedFBXTexture = null;
                    }
                }
            }

            // Generate Anim previews using extracted clip from sample FBX (only in debug mode)
            if (AI.DEBUG_MODE && (typesToUpdate & PreviewTypeFlags.Anim) != 0)
            {
                if (!string.IsNullOrEmpty(_sampleFBXPath) && AI.Config.generateAnimPreviews)
                {
                    // For UI sample, we extract a clip from the sample FBX and use the FBX model
                    // This demonstrates .anim preview without needing a separate sample file
                    AnimationClip sampleClip = null;
                    Object[] fbxAssets = AssetDatabase.LoadAllAssetsAtPath(_sampleFBXPath);
                    AnimationClip[] clips = fbxAssets
                        .OfType<AnimationClip>()
                        .Where(c => !c.name.StartsWith("__preview__") && !c.empty)
                        .ToArray();

                    if (clips.Length > 0)
                    {
                        sampleClip = clips[0];
                    }

                    if (sampleClip != null)
                    {
                        // Create a list of fake dependencies pointing to the FBX
                        List<AssetFile> fakeDeps = new List<AssetFile>
                        {
                            new AssetFile
                            {
                                Type = "fbx",
                                Guid = AssetDatabase.AssetPathToGUID(_sampleFBXPath),
                                ProjectPath = _sampleFBXPath
                            }
                        };

                        // Generate static preview - use clip path (we'll use the FBX path for sample)
                        Task<Texture2D> staticAnimTask = CustomPrefabPreviewGenerator.CreateAnim(_sampleFBXPath, PREVIEW_SIZE, 1, null, fakeDeps);
                        while (!staticAnimTask.IsCompleted) yield return null;
                        if (staticAnimTask.IsCompletedSuccessfully && staticAnimTask.Result != null)
                        {
                            if (_staticAnimTexture != null) DestroyImmediate(_staticAnimTexture);
                            _staticAnimTexture = staticAnimTask.Result;
                        }

                        // Generate animated preview
                        if (AI.Config.generateAnimatedAnimPreviews)
                        {
                            Task<Texture2D> animatedAnimTask = CustomPrefabPreviewGenerator.CreateAnim(_sampleFBXPath, PREVIEW_SIZE, frameCount, null, fakeDeps);
                            while (!animatedAnimTask.IsCompleted) yield return null;
                            if (animatedAnimTask.IsCompletedSuccessfully && animatedAnimTask.Result != null)
                            {
                                if (_animatedAnimTexture != null) DestroyImmediate(_animatedAnimTexture);
                                _animatedAnimTexture = animatedAnimTask.Result;
                            }
                        }
                        else if (_animatedAnimTexture != null)
                        {
                            DestroyImmediate(_animatedAnimTexture);
                            _animatedAnimTexture = null;
                        }
                    }
                }
                else
                {
                    if (_staticAnimTexture != null)
                    {
                        DestroyImmediate(_staticAnimTexture);
                        _staticAnimTexture = null;
                    }
                    if (_animatedAnimTexture != null)
                    {
                        DestroyImmediate(_animatedAnimTexture);
                        _animatedAnimTexture = null;
                    }
                }
            }

            // Generate UI preview
            if ((typesToUpdate & PreviewTypeFlags.UI) != 0)
            {
                if (_previewUIObject != null && AI.Config.generateUIPreviews)
                {
                    Task<Texture2D> staticUITask = CustomPrefabPreviewGenerator.Create(_previewUIObject, PREVIEW_SIZE, 1);
                    while (!staticUITask.IsCompleted) yield return null;
                    if (staticUITask.IsCompletedSuccessfully && staticUITask.Result != null)
                    {
                        if (_staticUITexture != null) DestroyImmediate(_staticUITexture);
                        _staticUITexture = staticUITask.Result;
                    }
                }
                else if (_staticUITexture != null)
                {
                    DestroyImmediate(_staticUITexture);
                    _staticUITexture = null;
                }
            }

            // Generate Particle previews
            if ((typesToUpdate & PreviewTypeFlags.Particle) != 0)
            {
                if (_previewParticleObject != null && AI.Config.generateParticlePreviews)
                {
                    Task<Texture2D> staticParticleTask = CustomPrefabPreviewGenerator.Create(_previewParticleObject, PREVIEW_SIZE, 1);
                    while (!staticParticleTask.IsCompleted) yield return null;
                    if (staticParticleTask.IsCompletedSuccessfully && staticParticleTask.Result != null)
                    {
                        if (_staticParticleTexture != null) DestroyImmediate(_staticParticleTexture);
                        _staticParticleTexture = staticParticleTask.Result;
                    }

                    if (AI.Config.generateAnimatedParticlePreviews && _previewParticleObject != null)
                    {
                        Task<Texture2D> animatedParticleTask = CustomPrefabPreviewGenerator.Create(_previewParticleObject, PREVIEW_SIZE, frameCount);
                        while (!animatedParticleTask.IsCompleted) yield return null;
                        if (animatedParticleTask.IsCompletedSuccessfully && animatedParticleTask.Result != null)
                        {
                            if (_animatedParticleTexture != null) DestroyImmediate(_animatedParticleTexture);
                            _animatedParticleTexture = animatedParticleTask.Result;
                        }
                    }
                    else if (_animatedParticleTexture != null)
                    {
                        DestroyImmediate(_animatedParticleTexture);
                        _animatedParticleTexture = null;
                    }
                }
                else
                {
                    if (_staticParticleTexture != null)
                    {
                        DestroyImmediate(_staticParticleTexture);
                        _staticParticleTexture = null;
                    }
                    if (_animatedParticleTexture != null)
                    {
                        DestroyImmediate(_animatedParticleTexture);
                        _animatedParticleTexture = null;
                    }
                }
            }

#if USE_VFX
            // Generate VFX previews (if available)
            if ((typesToUpdate & PreviewTypeFlags.VFX) != 0)
            {
                if (_previewVFXObject != null && AI.Config.generateVFXPreviews && HasPreviewVFXAsset())
                {
                    Task<Texture2D> staticVFXTask = CustomPrefabPreviewGenerator.Create(_previewVFXObject, PREVIEW_SIZE, 1);
                    while (!staticVFXTask.IsCompleted) yield return null;
                    if (staticVFXTask.IsCompletedSuccessfully)
                    {
                        Texture2D staticVFXTexture = ResolveVFXSettingsPreviewTexture(staticVFXTask.Result);
                        if (_staticVFXTexture != null) DestroyImmediate(_staticVFXTexture);
                        _staticVFXTexture = staticVFXTexture;
                    }

                    if (AI.Config.generateAnimatedVFXPreviews && _previewVFXObject != null)
                    {
                        Task<Texture2D> animatedVFXTask = CustomPrefabPreviewGenerator.Create(_previewVFXObject, PREVIEW_SIZE, frameCount);
                        while (!animatedVFXTask.IsCompleted) yield return null;
                        if (animatedVFXTask.IsCompletedSuccessfully)
                        {
                            Texture2D animatedVFXTexture = ResolveVFXSettingsPreviewTexture(animatedVFXTask.Result);
                            if (_animatedVFXTexture != null) DestroyImmediate(_animatedVFXTexture);
                            _animatedVFXTexture = animatedVFXTexture;
                        }
                    }
                    else if (_animatedVFXTexture != null)
                    {
                        DestroyImmediate(_animatedVFXTexture);
                        _animatedVFXTexture = null;
                    }
                }
                else
                {
                    if (_staticVFXTexture != null)
                    {
                        DestroyImmediate(_staticVFXTexture);
                        _staticVFXTexture = null;
                    }
                    if (_animatedVFXTexture != null)
                    {
                        DestroyImmediate(_animatedVFXTexture);
                        _animatedVFXTexture = null;
                    }
                }
            }
#endif

            // Generate Font preview using the same font as UI sample
            if ((typesToUpdate & PreviewTypeFlags.Font) != 0)
            {
                if (AI.Config.generateFontPreviews)
                {
                    Font previewFont = GetBuiltinFont();
                    if (previewFont != null)
                    {
                        if (_previewFontTexture != null) DestroyImmediate(_previewFontTexture);
                        _previewFontTexture = FontPreviewGenerator.Create(previewFont, PREVIEW_SIZE);
                    }
                }
                else if (_previewFontTexture != null)
                {
                    DestroyImmediate(_previewFontTexture);
                    _previewFontTexture = null;
                }
            }

#if UNITY_EDITOR_WIN
            // Generate Video previews
            if ((typesToUpdate & PreviewTypeFlags.Video) != 0)
            {
                if (_sampleVideoClip != null && AI.Config.generateVideoPreviews)
                {
                    // Static video preview
                    Task<Texture2D> staticVideoTask = VideoPreviewGenerator.Create(_sampleVideoClip, PREVIEW_SIZE, 1);
                    while (!staticVideoTask.IsCompleted) yield return null;
                    if (staticVideoTask.IsCompletedSuccessfully && staticVideoTask.Result != null)
                    {
                        if (_staticVideoTexture != null) DestroyImmediate(_staticVideoTexture);
                        _staticVideoTexture = staticVideoTask.Result;
                    }

                    // Animated video preview
                    if (AI.Config.generateAnimatedVideoPreviews)
                    {
                        Task<Texture2D> animatedVideoTask = VideoPreviewGenerator.Create(_sampleVideoClip, PREVIEW_SIZE, frameCount);
                        while (!animatedVideoTask.IsCompleted) yield return null;
                        if (animatedVideoTask.IsCompletedSuccessfully && animatedVideoTask.Result != null)
                        {
                            if (_animatedVideoTexture != null) DestroyImmediate(_animatedVideoTexture);
                            _animatedVideoTexture = animatedVideoTask.Result;
                        }
                    }
                    else if (_animatedVideoTexture != null)
                    {
                        DestroyImmediate(_animatedVideoTexture);
                        _animatedVideoTexture = null;
                    }
                }
                else
                {
                    if (_staticVideoTexture != null)
                    {
                        DestroyImmediate(_staticVideoTexture);
                        _staticVideoTexture = null;
                    }
                    if (_animatedVideoTexture != null)
                    {
                        DestroyImmediate(_animatedVideoTexture);
                        _animatedVideoTexture = null;
                    }
                }
            }
#endif

            // Generate Material previews
            if ((typesToUpdate & PreviewTypeFlags.Material) != 0)
            {
                if (_sampleMaterial != null && AI.Config.generateMaterialPreviews)
                {
                    // Static material preview
                    Task<Texture2D> staticMaterialTask = CustomMaterialPreviewGenerator.Create(_sampleMaterial, PREVIEW_SIZE);
                    while (!staticMaterialTask.IsCompleted) yield return null;
                    if (staticMaterialTask.IsCompletedSuccessfully && staticMaterialTask.Result != null)
                    {
                        if (_staticMaterialTexture != null) DestroyImmediate(_staticMaterialTexture);
                        _staticMaterialTexture = staticMaterialTask.Result;
                    }
                }
                else
                {
                    if (_staticMaterialTexture != null)
                    {
                        DestroyImmediate(_staticMaterialTexture);
                        _staticMaterialTexture = null;
                    }
                }
            }

            _isGeneratingPreview = false;

            // Reinitialize animation players if playing, since textures may have changed
            if (_playAnimatedPreviews)
            {
                InitializeAnimationPlayers();
            }

            Repaint();
        }

        private void ResetToDefaults()
        {
            // Reset preview type flags
            AI.Config.generateCustomModelPreviews = true;
            AI.Config.generateAnimatedModelPreviews = false;
            AI.Config.generateFBXPreviews = true;
            AI.Config.generateAnimatedFBXPreviews = true;
            AI.Config.generate360FBXPreviews = false;
            AI.Config.fbxAnimationPreviewMode = FBXAnimationPreviewMode.BoneVisualization;
            AI.Config.generateAnimPreviews = true;
            AI.Config.generateAnimatedAnimPreviews = true;
            AI.Config.generateUIPreviews = true;
            AI.Config.generateParticlePreviews = true;
            AI.Config.generateAnimatedParticlePreviews = true;
            AI.Config.generateVFXPreviews = true;
            AI.Config.generateAnimatedVFXPreviews = true;
            AI.Config.generateFontPreviews = true;
            AI.Config.generateVideoPreviews = true;
            AI.Config.generateAnimatedVideoPreviews = true;
            AI.Config.generateMaterialPreviews = true;
            AI.Config.materialPreviewMesh = 0; // Sphere
            AI.Config.generateScenePreviews = false;

            AI.Config.cpSuperSamplingMultiplier = 4;
            AI.Config.cpDepth = 24;
            AI.Config.cpCameraFOV = 30f;
            AI.Config.cpRotateLightWith360 = true;
            AI.Config.cpCameraAngleX = 70f;
            AI.Config.cpCameraAngleY = 240f;
            AI.Config.cpFramingPadding = 0f;
            AI.Config.cpUseDirectionalLight = true;
            AI.Config.cpLightColor = "FFFFFFFF"; // White
            AI.Config.cpLightIntensity = 0.8f;
            AI.Config.cpLightIntensityURP = 0.5f;
            AI.Config.cpLightIntensityHDRP = 5000f;
            AI.Config.cpLightRotationX = 58f;
            AI.Config.cpLightRotationY = 249f;
            AI.Config.cpUseSecondaryLight = false;
            AI.Config.cpSecondaryLightColor = "6666FFFF"; // Subtle blue-grey
            AI.Config.cpSecondaryLightIntensityMultiplier = 0.7f;
            AI.Config.cpSecondaryLightRotationX = 340f;
            AI.Config.cpSecondaryLightRotationY = 341f;
            AI.Config.cpBackgroundType = CustomPreviewBackgroundType.SolidColor;
            AI.Config.cpBackgroundColor = "525252FF";
            AI.Config.cpBackgroundColorHDRP = "222222FF";
            AI.Config.cpGradient2TopColor = "808080FF";
            AI.Config.cpGradient2BottomColor = "404040FF";
            AI.Config.cpGradient4TopLeftColor = "808080FF";
            AI.Config.cpGradient4TopRightColor = "606060FF";
            AI.Config.cpGradient4BottomLeftColor = "404040FF";
            AI.Config.cpGradient4BottomRightColor = "303030FF";
            AI.Config.cpGradientRotation = 0f;
            AI.Config.cpAmbientIntensity = 0.25f;
            AI.Config.cpUseCustomSkybox = false;
            AI.Config.cpSkyboxPath = "";
            AI.Config.cpVFXMaxDuration = 5f;
            AI.Config.cpParticleSeed = 1;
            AI.Config.cpParticleSimulateTime = 10f;
            AI.Config.cpParticleMinimumVisibleDuration = 0f;
            AI.Config.cpVFXMinimumVisibleDuration = 0f;
            AI.Config.cpFontColor = "FFFFFFFF"; // White

            _previewTypesToUpdate = PreviewTypeFlags.All;
            AI.SaveConfig();
        }
    }
}
