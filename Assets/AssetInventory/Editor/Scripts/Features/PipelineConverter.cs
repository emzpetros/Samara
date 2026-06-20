// ConverterContainerId, ConverterId, ConverterFilter are deprecated but the new string-based API is not yet stable
#pragma warning disable CS0618
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
#if USE_URP
using UnityEditor.Rendering.Universal;
#endif
using UnityEngine;
using UnityEngine.Rendering;


namespace AssetInventory
{
    /// <summary>
    /// Handles conversion of materials from the Built-in Render Pipeline (BIRP) to the
    /// current Scriptable Render Pipeline (URP or HDRP).
    ///
    /// Two conversion mechanisms are available:
    /// 1. Unity's built-in converter (uses the Converters API, URP only)
    /// 2. Custom converter (manual property remapping, works for both URP and HDRP)
    ///
    /// The custom converter acts as a fallback when Unity's converter is unavailable,
    /// disabled, or fails.
    /// </summary>
    public static class PipelineConverter
    {
        private static readonly HashSet<string> LoggedUnsafePreviewShaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal enum MaterialSurfaceMode
        {
            Opaque,
            Cutout,
            Transparent
        }

        internal struct MaterialConversionInfo
        {
            public bool ShouldConvert;
            public bool KnownBirpShader;
            public bool CustomLegacySurface;
            public MaterialSurfaceMode SurfaceMode;
            public bool DoubleSided;

            public MaterialConversionInfo(bool shouldConvert, bool knownBirpShader, bool customLegacySurface, MaterialSurfaceMode surfaceMode, bool doubleSided)
            {
                ShouldConvert = shouldConvert;
                KnownBirpShader = knownBirpShader;
                CustomLegacySurface = customLegacySurface;
                SurfaceMode = surfaceMode;
                DoubleSided = doubleSided;
            }
        }

        internal struct SerializedErrorMaterialSnapshot
        {
            public bool Transparent;
            public bool DoubleSided;
            public bool SerializedParticleShader;
            public bool SingleChannelTexture;
            public int RenderQueue;
            public int BlendMode;
            public int SrcBlend;
            public int DstBlend;
            public int ZWrite;
            public int Cull;
            public string MainTexturePath;
            public Vector2 MainTextureScale;
            public Vector2 MainTextureOffset;
            public Color BaseColor;
        }

        #region Unity Converter (URP only)

        /// <summary>
        /// Runs the URP material converter if available (fire-and-forget).
        /// </summary>
        /// <returns>True if the Unity converter ran without error, false otherwise.</returns>
        public static bool RunUnityConverter()
        {
#if USE_URP
            if (AssetUtils.IsOnURP())
            {
                try
                {
                    Converters.RunInBatchMode(
                        ConverterContainerId.BuiltInToURP
                        , new List<ConverterId>
                        {
                            ConverterId.Material
                        }
                        , ConverterFilter.Inclusive
                    );
                    return true;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"Could not run URP converter: {e.Message}");
                    return false;
                }
            }
#endif
            return false;
        }

        /// <summary>
        /// Runs the URP material converter and waits for it to complete.
        /// Uses reflection to await the callback-based Scan method which fires asynchronously.
        /// Use this instead of RunUnityConverter when subsequent operations (e.g. preview generation)
        /// depend on the materials being fully converted.
        /// </summary>
        /// <returns>True if the Unity converter ran successfully, false otherwise.</returns>
        public static async Task<bool> RunUnityConverterAsync()
        {
#if USE_URP
            if (!AssetUtils.IsOnURP()) return false;

            try
            {
                // Get converter types via reflection since FilterConverters is internal
                MethodInfo filterMethod = typeof(Converters).GetMethod("FilterConverters", BindingFlags.NonPublic | BindingFlags.Static);
                if (filterMethod == null) return false;

                List<Type> converterTypes = (List<Type>)filterMethod.Invoke(null, new object[]
                {
                    ConverterContainerId.BuiltInToURP,
                    new List<ConverterId> {ConverterId.Material},
                    ConverterFilter.Inclusive
                });

                bool anyConverted = false;
                foreach (Type converterType in converterTypes)
                {
                    object converter = Activator.CreateInstance(converterType);
                    if (converter == null) continue;

                    // Get the Scan method: void Scan(Action<List<IRenderPipelineConverterItem>>)
                    MethodInfo scanMethod = converter.GetType().GetMethod("Scan");
                    if (scanMethod == null) continue;

                    ParameterInfo[] scanParams = scanMethod.GetParameters();
                    if (scanParams.Length != 1) continue;

                    // Extract the callback delegate type: Action<List<IRenderPipelineConverterItem>>
                    Type callbackType = scanParams[0].ParameterType;

                    // Extract the item type from the generic arguments: List<T> -> T
                    Type listType = callbackType.GetGenericArguments()[0]; // List<IRenderPipelineConverterItem>
                    Type itemType = listType.GetGenericArguments()[0]; // IRenderPipelineConverterItem

                    // Get BeforeConvert, Convert, AfterConvert methods
                    MethodInfo beforeConvert = converter.GetType().GetMethod("BeforeConvert");
                    MethodInfo convertMethod = converter.GetType().GetMethod("Convert");
                    MethodInfo afterConvert = converter.GetType().GetMethod("AfterConvert");

                    TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();

                    // Create callback state and get the generic instance method specialized to the item type
                    ConverterCallbackState state = new ConverterCallbackState
                    {
                        Converter = converter, BeforeConvert = beforeConvert,
                        ConvertMethod = convertMethod, AfterConvert = afterConvert, Tcs = tcs
                    };
                    MethodInfo helperMethod = typeof(ConverterCallbackState)
                        .GetMethod(nameof(ConverterCallbackState.OnScanFinished))
                        .MakeGenericMethod(itemType);

                    // Create the delegate that matches Action<List<IRenderPipelineConverterItem>>
                    Delegate callback = Delegate.CreateDelegate(callbackType, state, helperMethod);

                    // Invoke Scan with our callback
                    scanMethod.Invoke(converter, new object[] {callback});

                    // Wait for the callback to fire, with a timeout
                    Task completed = await Task.WhenAny(tcs.Task, Task.Delay(30000));
                    if (completed != tcs.Task)
                    {
                        Debug.LogWarning($"URP converter '{converterType.Name}' timed out after 30 seconds.");
                    }
                    else if (tcs.Task.Result)
                    {
                        anyConverted = true;
                    }
                }

                AssetDatabase.SaveAssets();
                return anyConverted;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Could not run async URP converter: {e.Message}");
                return false;
            }
#else
            await Task.CompletedTask;
            return false;
#endif
        }

        /// <summary>
        /// State object and callback handler for the reflection-based converter scan.
        /// The generic OnScanFinished method is specialized at runtime via MakeGenericMethod
        /// to match the internal IRenderPipelineConverterItem type.
        /// </summary>
        private class ConverterCallbackState
        {
            public object Converter;
            public MethodInfo BeforeConvert;
            public MethodInfo ConvertMethod;
            public MethodInfo AfterConvert;
            public TaskCompletionSource<bool> Tcs;

            public void OnScanFinished<T>(List<T> items)
            {
                try
                {
                    BeforeConvert?.Invoke(Converter, null);
                    foreach (T item in items)
                    {
                        object[] args = {item, null};
                        ConvertMethod.Invoke(Converter, args);
                    }
                    AfterConvert?.Invoke(Converter, null);
                    Tcs.TrySetResult(true);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"URP converter callback failed: {e.Message}");
                    Tcs.TrySetResult(false);
                }
            }
        }

        #endregion

        #region Conversion Analysis

        internal static bool CanSafelyAnalyzeMaterialForConversion(string shaderName)
        {
            return CanSafelyAnalyzeMaterialForConversion(shaderName, AssetUtils.IsOnURP(), AssetUtils.IsOnHDRP());
        }

        internal static bool CanSafelyAnalyzeMaterialForConversion(string shaderName, bool isOnURP, bool isOnHDRP)
        {
            if (string.IsNullOrWhiteSpace(shaderName)) return true;

            bool urpCompatible = AssetUtils.ShouldBeURPCompatible(shaderName);
            bool hdrpCompatible = AssetUtils.ShouldBeHDRPCompatible(shaderName);
            bool birpCompatible = AssetUtils.ShouldBeBIRPCompatible(shaderName);

            if (isOnURP) return !(hdrpCompatible && !urpCompatible);
            if (isOnHDRP) return !(urpCompatible && !hdrpCompatible);
            return !((urpCompatible || hdrpCompatible) && !birpCompatible);
        }

        internal static MaterialConversionInfo AnalyzeMaterialForConversion(Material mat, string shaderName)
        {
            if (mat == null) return new MaterialConversionInfo(false, false, false, MaterialSurfaceMode.Opaque, false);
            if (!CanSafelyAnalyzeMaterialForConversion(shaderName))
            {
                LogSkippedPreviewMaterialConversion(shaderName);
                return new MaterialConversionInfo(false, false, false, MaterialSurfaceMode.Opaque, false);
            }

            string renderType = "";
            string queueTag = "";
            Shader shader = mat.shader;
            if (IsShaderGraphAsset(shader))
            {
                return new MaterialConversionInfo(false, false, false, MaterialSurfaceMode.Opaque, false);
            }

            if (shader != null)
            {
                renderType = mat.GetTag("RenderType", true, "");
                queueTag = mat.GetTag("Queue", true, "");
            }

            bool hasMainTexture = mat.HasProperty("_MainTex");
            bool hasColor = mat.HasProperty("_Color");
            bool doubleSided = IsDoubleSidedMaterial(mat);
            MaterialConversionInfo info = AnalyzeShaderForConversion(shaderName, renderType, queueTag, hasMainTexture, hasColor, doubleSided);

            if (mat.HasProperty("_Mode"))
            {
                int mode = Mathf.RoundToInt(mat.GetFloat("_Mode"));
                if (mode == 1) info.SurfaceMode = MaterialSurfaceMode.Cutout;
                else if (mode >= 2) info.SurfaceMode = MaterialSurfaceMode.Transparent;
            }
            else if (mat.HasProperty("_AlphaClip") && mat.GetFloat("_AlphaClip") > 0.5f)
            {
                info.SurfaceMode = MaterialSurfaceMode.Cutout;
            }

            return info;
        }

        internal static void LogSkippedPreviewMaterialConversion(string shaderName)
        {
            if (string.IsNullOrWhiteSpace(shaderName)) return;
            if (LoggedUnsafePreviewShaders.Add(shaderName))
            {
                Debug.LogWarning($"Asset Inventory skipped preview material conversion for incompatible shader '{shaderName}'. A fallback preview material will be used.");
            }
        }

        internal static bool IsErrorShaderMaterial(Material mat)
        {
            return mat != null && mat.shader != null && IsErrorShaderName(mat.shader.name);
        }

        internal static bool TryCreateErrorShaderPreviewMaterial(Material source, bool preferParticleShader, bool isOnURP, bool isOnHDRP, out Material fallback)
        {
            fallback = null;
            if (!TryReadSerializedErrorMaterialSnapshot(source, out SerializedErrorMaterialSnapshot snapshot)) return false;

            Shader shader = ResolveSerializedErrorFallbackShader(preferParticleShader, isOnURP, isOnHDRP);
            if (shader == null) return false;

            fallback = new Material(shader);
            fallback.hideFlags = HideFlags.HideAndDontSave;
            fallback.name = $"{source.name} Preview Fallback";
            ApplySerializedErrorMaterialSnapshot(fallback, snapshot, isOnURP, isOnHDRP, true);
            return true;
        }

        private static bool TryReadSerializedErrorMaterialSnapshot(Material mat, out SerializedErrorMaterialSnapshot snapshot)
        {
            snapshot = default;
            if (!IsErrorShaderMaterial(mat)) return false;

            string path = AssetDatabase.GetAssetPath(mat);
            if (string.IsNullOrEmpty(path)) return false;

            string fullPath = AssetUtils.AddProjectRoot(path);
            if (!File.Exists(fullPath)) return false;

            try
            {
                return TryReadSerializedErrorMaterialSnapshot(File.ReadAllText(fullPath), out snapshot);
            }
            catch (Exception e)
            {
                if (AI.Config.LogPreviewCreation)
                {
                    Debug.LogWarning($"[Asset Inventory] Could not inspect error-shader material '{path}': {e.Message}");
                }
                return false;
            }
        }

        internal static bool TryReadSerializedErrorMaterialSnapshot(string content, out SerializedErrorMaterialSnapshot snapshot)
        {
            snapshot = new SerializedErrorMaterialSnapshot
            {
                RenderQueue = -1,
                BlendMode = 0,
                SrcBlend = (int)UnityEngine.Rendering.BlendMode.SrcAlpha,
                DstBlend = (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha,
                ZWrite = 0,
                Cull = 2,
                MainTextureScale = Vector2.one,
                MainTextureOffset = Vector2.zero,
                BaseColor = Color.white
            };
            if (string.IsNullOrEmpty(content)) return false;
            if (!content.Contains("m_Shader:")) return false;

            snapshot.RenderQueue = ReadIntProperty(content, "m_CustomRenderQueue", -1);
            bool hasBlendMode = TryReadFloatProperty(content, "_Blend", "_BUILTIN_Blend", out float blendMode);
            bool hasSrcBlend = TryReadFloatProperty(content, "_SrcBlend", "_BUILTIN_SrcBlend", out float srcBlend);
            bool hasDstBlend = TryReadFloatProperty(content, "_DstBlend", "_BUILTIN_DstBlend", out float dstBlend);
            bool hasZWrite = TryReadFloatProperty(content, "_ZWrite", "_BUILTIN_ZWrite", out float zWrite);

            snapshot.BlendMode = hasBlendMode
                ? Mathf.RoundToInt(blendMode)
                : InferParticleBlendModeFromBlendState(Mathf.RoundToInt(hasSrcBlend ? srcBlend : snapshot.SrcBlend), Mathf.RoundToInt(hasDstBlend ? dstBlend : snapshot.DstBlend));
            snapshot.SrcBlend = Mathf.RoundToInt(hasSrcBlend ? srcBlend : ReadFloatProperty(content, "_BUILTIN_SrcBlend", snapshot.SrcBlend));
            snapshot.DstBlend = Mathf.RoundToInt(hasDstBlend ? dstBlend : ReadFloatProperty(content, "_BUILTIN_DstBlend", snapshot.DstBlend));
            snapshot.ZWrite = Mathf.RoundToInt(hasZWrite ? zWrite : ReadFloatProperty(content, "_BUILTIN_ZWrite", snapshot.ZWrite));
            snapshot.Transparent = snapshot.RenderQueue >= (int)RenderQueue.Transparent ||
                content.IndexOf("RenderType: Transparent", StringComparison.OrdinalIgnoreCase) >= 0 ||
                ReadFloatProperty(content, "_Surface", ReadFloatProperty(content, "_BUILTIN_Surface", 0f)) > 0.5f ||
                ((hasSrcBlend || hasDstBlend || hasZWrite) && IsTransparentBlendState(snapshot.SrcBlend, snapshot.DstBlend, snapshot.ZWrite));
            snapshot.SerializedParticleShader = snapshot.Transparent && (hasBlendMode || hasSrcBlend || hasDstBlend || hasZWrite);

            snapshot.Cull = Mathf.RoundToInt(ReadFloatProperty(content, "_Cull", ReadFloatProperty(content, "_BUILTIN_CullMode", snapshot.Cull)));
            snapshot.DoubleSided = snapshot.Cull == 0 ||
                ReadFloatProperty(content, "_DoubleSidedEnable", 0f) > 0.5f ||
                content.IndexOf("_DOUBLESIDED_ON", StringComparison.OrdinalIgnoreCase) >= 0;

            string[] textureProperties = {"_MainTexture", "_MainTex", "_BaseMap", "_AlphaOverride"};
            foreach (string property in textureProperties)
            {
                if (TryReadTextureProperty(content, property, out string texturePath, out Vector2 scale, out Vector2 offset))
                {
                    snapshot.MainTexturePath = texturePath;
                    snapshot.MainTextureScale = scale;
                    snapshot.MainTextureOffset = offset;
                    snapshot.SingleChannelTexture = TextureImporterUsesSingleChannelAlpha(texturePath);
                    break;
                }
            }

            string[] colorProperties = {"_BaseColor", "_Color", "_TintColor", "_EmissionColor"};
            foreach (string property in colorProperties)
            {
                if (TryReadColorProperty(content, property, out Color color))
                {
                    snapshot.BaseColor = color;
                    break;
                }
            }

            return true;
        }

        private static bool IsTransparentBlendState(int srcBlend, int dstBlend, int zWrite)
        {
            if (zWrite == 0) return true;
            return srcBlend != (int)BlendMode.One || dstBlend != (int)BlendMode.Zero;
        }

        private static int InferParticleBlendModeFromBlendState(int srcBlend, int dstBlend)
        {
            if (srcBlend == (int)BlendMode.One && dstBlend == (int)BlendMode.OneMinusSrcAlpha) return 1;
            if (srcBlend == (int)BlendMode.One && dstBlend == (int)BlendMode.One) return 2;
            if (srcBlend == (int)BlendMode.DstColor && dstBlend == (int)BlendMode.Zero) return 3;
            return 0;
        }

        private static bool TextureImporterUsesSingleChannelAlpha(string texturePath)
        {
            if (string.IsNullOrEmpty(texturePath)) return false;

            TextureImporter importer = UnityEditor.AssetImporter.GetAtPath(texturePath) as TextureImporter;
            if (importer == null) return false;
            return importer.textureType == TextureImporterType.SingleChannel ||
                importer.alphaSource == TextureImporterAlphaSource.FromGrayScale;
        }

        private static Shader ResolveSerializedErrorFallbackShader(bool preferParticleShader, bool isOnURP, bool isOnHDRP)
        {
            if (isOnURP)
            {
                Shader shader = preferParticleShader
                    ? Shader.Find("Universal Render Pipeline/Particles/Unlit")
                    : Shader.Find("Universal Render Pipeline/Unlit");
                return shader != null ? shader : Shader.Find("Universal Render Pipeline/Lit");
            }
            if (isOnHDRP)
            {
                Shader shader = preferParticleShader ? Shader.Find("HDRP/Particles/Unlit") : Shader.Find("HDRP/Unlit");
                return shader != null ? shader : Shader.Find("HDRP/Lit");
            }
            return Shader.Find("Unlit/Transparent") ?? Shader.Find("Unlit/Texture");
        }

        private static void ApplySerializedErrorMaterialSnapshot(Material mat, SerializedErrorMaterialSnapshot snapshot, bool isOnURP, bool isOnHDRP, bool useTransientSingleChannelTexture = false)
        {
            if (mat == null) return;

            if (!string.IsNullOrEmpty(snapshot.MainTexturePath))
            {
                Texture texture = useTransientSingleChannelTexture && snapshot.SingleChannelTexture
                    ? CreateSingleChannelAlphaTexture(snapshot.MainTexturePath)
                    : AssetDatabase.LoadAssetAtPath<Texture>(snapshot.MainTexturePath);
                if (texture != null)
                {
                    SetTextureIfPresent(mat, "_BaseMap", texture, snapshot.MainTextureScale, snapshot.MainTextureOffset);
                    SetTextureIfPresent(mat, "_MainTex", texture, snapshot.MainTextureScale, snapshot.MainTextureOffset);
                    SetTextureIfPresent(mat, "_MainTexture", texture, snapshot.MainTextureScale, snapshot.MainTextureOffset);
                }
            }

            SetColorIfPresent(mat, "_BaseColor", snapshot.BaseColor);
            SetColorIfPresent(mat, "_Color", snapshot.BaseColor);
            SetColorIfPresent(mat, "_TintColor", snapshot.BaseColor);

            if (snapshot.Transparent)
            {
                SetFloatIfPresent(mat, "_Surface", 1f);
                SetFloatIfPresent(mat, "_SurfaceType", 1f);
                SetFloatIfPresent(mat, "_Blend", snapshot.BlendMode);
                SetFloatIfPresent(mat, "_BlendMode", snapshot.BlendMode == 2 ? 1f : 0f);
                SetIntIfPresent(mat, "_SrcBlend", snapshot.SrcBlend);
                SetIntIfPresent(mat, "_DstBlend", snapshot.DstBlend);
                SetIntIfPresent(mat, "_SrcBlendAlpha", snapshot.SrcBlend);
                SetIntIfPresent(mat, "_DstBlendAlpha", snapshot.DstBlend);
                SetIntIfPresent(mat, "_ZWrite", snapshot.ZWrite);
                mat.SetOverrideTag("RenderType", "Transparent");
                mat.renderQueue = snapshot.RenderQueue >= 0 ? snapshot.RenderQueue : (int)RenderQueue.Transparent;
                SetKeyword(mat, "_SURFACE_TYPE_TRANSPARENT", true);
                SetKeyword(mat, "_ALPHAPREMULTIPLY_ON", snapshot.BlendMode == 1 || snapshot.BlendMode == 2);
                SetKeyword(mat, "_ALPHAMODULATE_ON", snapshot.BlendMode == 3);
            }
            else
            {
                SetFloatIfPresent(mat, "_Surface", 0f);
                SetFloatIfPresent(mat, "_SurfaceType", 0f);
                SetIntIfPresent(mat, "_ZWrite", 1);
                mat.SetOverrideTag("RenderType", "");
                mat.renderQueue = snapshot.RenderQueue;
                SetKeyword(mat, "_SURFACE_TYPE_TRANSPARENT", false);
            }

            SetFloatIfPresent(mat, "_Cull", snapshot.Cull);
            SetFloatIfPresent(mat, "_CullMode", snapshot.Cull);
            SetFloatIfPresent(mat, "_CullModeForward", snapshot.Cull);
            ApplyDoubleSidedSettings(mat, snapshot.DoubleSided, isOnURP, isOnHDRP);
        }

        private static Texture CreateSingleChannelAlphaTexture(string texturePath)
        {
            if (string.IsNullOrEmpty(texturePath)) return null;

            string fullPath = AssetUtils.AddProjectRoot(texturePath);
            if (!File.Exists(fullPath)) return AssetDatabase.LoadAssetAtPath<Texture>(texturePath);

            Texture source = AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
            try
            {
                Texture2D decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                decoded.hideFlags = HideFlags.HideAndDontSave;
                if (!ImageConversion.LoadImage(decoded, File.ReadAllBytes(fullPath)))
                {
                    UnityEngine.Object.DestroyImmediate(decoded);
                    return source;
                }

                Color32[] pixels = decoded.GetPixels32();
                for (int i = 0; i < pixels.Length; i++)
                {
                    byte alpha = pixels[i].r;
                    pixels[i] = new Color32(255, 255, 255, alpha);
                }
                decoded.SetPixels32(pixels);
                decoded.Apply(false, true);

                if (source != null)
                {
                    decoded.wrapMode = source.wrapMode;
                    decoded.filterMode = source.filterMode;
                    decoded.anisoLevel = source.anisoLevel;
                }
                return decoded;
            }
            catch
            {
                return source;
            }
        }

        private static bool TryReadTextureProperty(string content, string property, out string texturePath, out Vector2 scale, out Vector2 offset)
        {
            texturePath = null;
            scale = Vector2.one;
            offset = Vector2.zero;

            Match match = Regex.Match(content,
                @"-\s+" + Regex.Escape(property) + @":\s*\r?\n\s*m_Texture:\s*\{fileID:\s*-?\d+,\s*guid:\s*([a-f0-9]{32}),\s*type:\s*\d+\}\s*\r?\n\s*m_Scale:\s*\{x:\s*([^,}]+),\s*y:\s*([^,}]+)\}\s*\r?\n\s*m_Offset:\s*\{x:\s*([^,}]+),\s*y:\s*([^,}]+)\}",
                RegexOptions.IgnoreCase);
            if (!match.Success) return false;

            string guid = match.Groups[1].Value;
            if (string.IsNullOrEmpty(guid) || guid == "00000000000000000000000000000000") return false;

            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) return false;

            texturePath = path;
            scale = new Vector2(ParseFloat(match.Groups[2].Value, 1f), ParseFloat(match.Groups[3].Value, 1f));
            offset = new Vector2(ParseFloat(match.Groups[4].Value, 0f), ParseFloat(match.Groups[5].Value, 0f));
            return true;
        }

        private static bool TryReadColorProperty(string content, string property, out Color color)
        {
            color = Color.white;
            Match match = Regex.Match(content,
                @"-\s+" + Regex.Escape(property) + @":\s*\{r:\s*([^,}]+),\s*g:\s*([^,}]+),\s*b:\s*([^,}]+),\s*a:\s*([^,}]+)\}",
                RegexOptions.IgnoreCase);
            if (!match.Success) return false;

            color = new Color(
                ParseFloat(match.Groups[1].Value, 1f),
                ParseFloat(match.Groups[2].Value, 1f),
                ParseFloat(match.Groups[3].Value, 1f),
                ParseFloat(match.Groups[4].Value, 1f));
            return true;
        }

        private static float ReadFloatProperty(string content, string property, float fallback)
        {
            return TryReadFloatProperty(content, property, out float value) ? value : fallback;
        }

        private static bool TryReadFloatProperty(string content, string property, out float value)
        {
            value = 0f;
            Match match = Regex.Match(content, @"-\s+" + Regex.Escape(property) + @":\s*([-+0-9.eE]+)", RegexOptions.IgnoreCase);
            return match.Success && float.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryReadFloatProperty(string content, string primaryProperty, string fallbackProperty, out float value)
        {
            if (TryReadFloatProperty(content, primaryProperty, out value)) return true;
            return TryReadFloatProperty(content, fallbackProperty, out value);
        }

        private static int ReadIntProperty(string content, string property, int fallback)
        {
            Match match = Regex.Match(content, @"^\s*" + Regex.Escape(property) + @":\s*(-?\d+)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (!match.Success) return fallback;
            return int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;
        }

        private static float ParseFloat(string value, float fallback)
        {
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result) ? result : fallback;
        }

        internal static MaterialConversionInfo AnalyzeShaderForConversion(
            string shaderName,
            string renderType,
            string queueTag,
            bool hasMainTexture,
            bool hasColor,
            bool shaderIsDoubleSided)
        {
            shaderName ??= "";
            renderType ??= "";
            queueTag ??= "";

            if (IsErrorShaderName(shaderName))
            {
                return new MaterialConversionInfo(false, false, false, MaterialSurfaceMode.Opaque, false);
            }

            if (IsPipelineShader(shaderName))
            {
                return new MaterialConversionInfo(false, false, false, MaterialSurfaceMode.Opaque, false);
            }

            bool knownBirpShader = PrefabPreviewUtilities.IsBIRPShader(shaderName);
            MaterialSurfaceMode surfaceMode = DetermineSurfaceMode(shaderName, renderType, queueTag);
            bool doubleSided = shaderIsDoubleSided || NameIndicatesDoubleSided(shaderName);

            if (knownBirpShader)
            {
                return new MaterialConversionInfo(true, true, false, surfaceMode, doubleSided);
            }

            bool hasRemappableProperties = hasMainTexture || hasColor;
            bool customLegacySurface = hasRemappableProperties && surfaceMode != MaterialSurfaceMode.Opaque;
            return new MaterialConversionInfo(customLegacySurface, false, customLegacySurface, surfaceMode, doubleSided);
        }

        private static MaterialSurfaceMode DetermineSurfaceMode(string shaderName, string renderType, string queueTag)
        {
            if (ContainsIgnoreCase(renderType, "TransparentCutout") ||
                ContainsIgnoreCase(queueTag, "AlphaTest") ||
                ContainsIgnoreCase(shaderName, "Cutout"))
            {
                return MaterialSurfaceMode.Cutout;
            }

            if (ContainsIgnoreCase(renderType, "Transparent") ||
                ContainsIgnoreCase(queueTag, "Transparent") ||
                ContainsIgnoreCase(shaderName, "Transparent") ||
                ContainsIgnoreCase(shaderName, "Alpha Blended"))
            {
                return MaterialSurfaceMode.Transparent;
            }

            return MaterialSurfaceMode.Opaque;
        }

        private static bool IsPipelineShader(string shaderName)
        {
            return shaderName.StartsWith("Universal Render Pipeline/", StringComparison.OrdinalIgnoreCase) ||
                shaderName.StartsWith("HDRP/", StringComparison.OrdinalIgnoreCase) ||
                shaderName.StartsWith("Hidden/Universal Render Pipeline/", StringComparison.OrdinalIgnoreCase) ||
                shaderName.StartsWith("Shader Graphs/", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsErrorShaderName(string shaderName)
        {
            return string.Equals(shaderName, "Hidden/InternalErrorShader", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsShaderGraphAssetPath(string shaderAssetPath)
        {
            return !string.IsNullOrEmpty(shaderAssetPath) &&
                shaderAssetPath.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsShaderGraphAsset(Shader shader)
        {
            return shader != null && IsShaderGraphAssetPath(AssetDatabase.GetAssetPath(shader));
        }

        private static bool IsDoubleSidedMaterial(Material mat)
        {
            if (mat.HasProperty("_Cull") && Mathf.Approximately(mat.GetFloat("_Cull"), 0f))
            {
                return true;
            }

            return IsDoubleSidedShader(mat.shader);
        }

        private static bool IsDoubleSidedShader(Shader shader)
        {
            if (shader == null) return false;

            string shaderPath = AssetDatabase.GetAssetPath(shader);
            if (string.IsNullOrEmpty(shaderPath) || !File.Exists(shaderPath)) return false;

            try
            {
                string shaderSource = File.ReadAllText(shaderPath);
                return ContainsIgnoreCase(shaderSource, "Cull Off");
            }
            catch (Exception e)
            {
                if (AI.Config.LogPreviewCreation)
                {
                    Debug.LogWarning($"[Asset Inventory] Could not inspect shader '{shader.name}' for double-sided conversion: {e.Message}");
                }
                return false;
            }
        }

        private static bool NameIndicatesDoubleSided(string shaderName)
        {
            return ContainsIgnoreCase(shaderName, "Twosided") ||
                ContainsIgnoreCase(shaderName, "Two Sided") ||
                ContainsIgnoreCase(shaderName, "Two-Sided") ||
                ContainsIgnoreCase(shaderName, "Doublesided") ||
                ContainsIgnoreCase(shaderName, "Double Sided") ||
                ContainsIgnoreCase(shaderName, "2sided");
        }

        private static bool ContainsIgnoreCase(string value, string search)
        {
            return !string.IsNullOrEmpty(value) &&
                value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        #endregion

        #region Custom Converter (URP + HDRP)

        /// <summary>
        /// Converts material assets at the given project-relative paths from BIRP to the current
        /// render pipeline using the custom converter logic. Acts as fallback when Unity's
        /// built-in converter is unavailable or fails.
        /// </summary>
        public static void ConvertImportedMaterials(IEnumerable<string> importedPaths)
        {
            bool isOnURP = AssetUtils.IsOnURP();
            bool isOnHDRP = AssetUtils.IsOnHDRP();
            if (!isOnURP && !isOnHDRP) return;

            List<string> pathList = importedPaths?.Where(path => !string.IsNullOrEmpty(path)).ToList() ?? new List<string>();
            HashSet<string> particleRendererMaterialPaths = CollectParticleRendererMaterialPaths(pathList);
            bool anyChanged = false;
            int scannedMaterials = 0;
            int convertedMaterials = 0;
            foreach (string path in pathList)
            {
                if (path == null || !path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) continue;

                Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null) continue;
                scannedMaterials++;

                string shaderName = mat.shader != null ? mat.shader.name : "";
                MaterialConversionInfo conversionInfo = AnalyzeMaterialForConversion(mat, shaderName);
                bool materialUsedByParticleRenderer = particleRendererMaterialPaths.Contains(NormalizeProjectPath(path));
                if (TryConvertSerializedErrorParticleMaterial(mat, materialUsedByParticleRenderer, isOnURP, isOnHDRP))
                {
                    anyChanged = true;
                    convertedMaterials++;
                    continue;
                }
                if (!conversionInfo.ShouldConvert && !ShouldUseParticleMaterialForImportedRendererConversion(shaderName, conversionInfo, materialUsedByParticleRenderer)) continue;

                if (ShouldUseParticleMaterialForImportedRendererConversion(shaderName, conversionInfo, materialUsedByParticleRenderer))
                {
                    ConvertParticleMaterial(mat, shaderName, isOnURP);
                }
                else if (conversionInfo.CustomLegacySurface)
                {
                    ConvertCustomSurfaceMaterial(mat, shaderName, conversionInfo, isOnURP, isOnHDRP);
                }
                else
                {
                    ConvertMaterial(mat, shaderName, isOnURP, isOnHDRP);
                }
                anyChanged = true;
                convertedMaterials++;
            }

            if (anyChanged) AssetDatabase.SaveAssets();
            if (AI.Config.LogPreviewCreation)
            {
                Debug.Log($"[Asset Inventory] Custom pipeline converter inspected {scannedMaterials} imported material(s), converted {convertedMaterials}.");
            }
        }

        /// <summary>
        /// Scans all materials in the project and converts any remaining BIRP materials
        /// to the current render pipeline. Used after bulk/package imports.
        /// </summary>
        public static void ConvertAllProjectMaterials()
        {
            bool isOnURP = AssetUtils.IsOnURP();
            bool isOnHDRP = AssetUtils.IsOnHDRP();
            if (!isOnURP && !isOnHDRP) return;

            HashSet<string> particleRendererMaterialPaths = CollectAllParticleRendererMaterialPaths();
            string[] materialGuids = AssetDatabase.FindAssets("t:Material");
            bool anyChanged = false;
            int scannedMaterials = 0;
            int convertedMaterials = 0;
            foreach (string guid in materialGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.StartsWith("Assets/")) continue;

                Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null) continue;
                scannedMaterials++;

                string shaderName = mat.shader != null ? mat.shader.name : "";
                MaterialConversionInfo conversionInfo = AnalyzeMaterialForConversion(mat, shaderName);
                bool materialUsedByParticleRenderer = particleRendererMaterialPaths.Contains(NormalizeProjectPath(path));
                if (TryConvertSerializedErrorParticleMaterial(mat, materialUsedByParticleRenderer, isOnURP, isOnHDRP))
                {
                    anyChanged = true;
                    convertedMaterials++;
                    continue;
                }
                if (!conversionInfo.ShouldConvert && !ShouldUseParticleMaterialForImportedRendererConversion(shaderName, conversionInfo, materialUsedByParticleRenderer)) continue;

                if (ShouldUseParticleMaterialForImportedRendererConversion(shaderName, conversionInfo, materialUsedByParticleRenderer))
                {
                    ConvertParticleMaterial(mat, shaderName, isOnURP);
                }
                else if (conversionInfo.CustomLegacySurface)
                {
                    ConvertCustomSurfaceMaterial(mat, shaderName, conversionInfo, isOnURP, isOnHDRP);
                }
                else
                {
                    ConvertMaterial(mat, shaderName, isOnURP, isOnHDRP);
                }
                anyChanged = true;
                convertedMaterials++;
            }

            if (anyChanged) AssetDatabase.SaveAssets();
            if (AI.Config.LogPreviewCreation)
            {
                Debug.Log($"[Asset Inventory] Custom pipeline converter inspected {scannedMaterials} project material(s), converted {convertedMaterials}.");
            }
        }

        internal static bool ShouldUseParticleMaterialForImportedRendererConversion(string shaderName, MaterialConversionInfo conversionInfo, bool materialUsedByParticleRenderer)
        {
            if (!materialUsedByParticleRenderer) return false;
            if (IsLegacyParticleShader(shaderName)) return true;
            if (!conversionInfo.ShouldConvert) return false;
            return PrefabPreviewUtilities.ShouldUseParticleMaterialForRendererConversion(shaderName, conversionInfo);
        }

        internal static bool ShouldUseSerializedErrorParticleMaterialForImportedRendererConversion(SerializedErrorMaterialSnapshot snapshot, bool materialUsedByParticleRenderer)
        {
            return materialUsedByParticleRenderer && snapshot.SerializedParticleShader && snapshot.Transparent;
        }

        private static bool TryConvertSerializedErrorParticleMaterial(Material mat, bool materialUsedByParticleRenderer, bool isOnURP, bool isOnHDRP)
        {
            if (!materialUsedByParticleRenderer) return false;
            if (!IsErrorShaderMaterial(mat)) return false;
            if (!TryReadSerializedErrorMaterialSnapshot(mat, out SerializedErrorMaterialSnapshot snapshot)) return false;
            if (!ShouldUseSerializedErrorParticleMaterialForImportedRendererConversion(snapshot, materialUsedByParticleRenderer)) return false;

            Shader shader = ResolveSerializedErrorFallbackShader(true, isOnURP, isOnHDRP);
            if (shader == null) return false;

            PrepareSingleChannelTextureForParticleMaterial(snapshot.MainTexturePath, snapshot.SingleChannelTexture);
            mat.shader = shader;
            ApplySerializedErrorMaterialSnapshot(mat, snapshot, isOnURP, isOnHDRP);
            ApplyParticleBlendSettings(mat, snapshot.BlendMode);
            ApplyDoubleSidedSettings(mat, snapshot.DoubleSided, isOnURP, isOnHDRP);
            if (snapshot.RenderQueue >= 0) mat.renderQueue = snapshot.RenderQueue;
            EditorUtility.SetDirty(mat);
            return true;
        }

        private static void PrepareSingleChannelTextureForParticleMaterial(string texturePath, bool singleChannelTexture)
        {
            if (!singleChannelTexture || string.IsNullOrEmpty(texturePath)) return;

            TextureImporter importer = UnityEditor.AssetImporter.GetAtPath(texturePath) as TextureImporter;
            if (importer == null) return;

            bool changed = false;
            if (importer.textureType == TextureImporterType.SingleChannel)
            {
                importer.textureType = TextureImporterType.Default;
                changed = true;
            }
            if (importer.alphaSource != TextureImporterAlphaSource.FromGrayScale)
            {
                importer.alphaSource = TextureImporterAlphaSource.FromGrayScale;
                changed = true;
            }
            if (!importer.alphaIsTransparency)
            {
                importer.alphaIsTransparency = true;
                changed = true;
            }

            if (changed) importer.SaveAndReimport();
        }

        private static HashSet<string> CollectParticleRendererMaterialPaths(IEnumerable<string> importedPaths)
        {
            HashSet<string> materialPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in importedPaths)
            {
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) continue;
                CollectParticleRendererMaterialPathsFromPrefab(path, materialPaths);
            }
            return materialPaths;
        }

        private static HashSet<string> CollectAllParticleRendererMaterialPaths()
        {
            HashSet<string> materialPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) continue;
                CollectParticleRendererMaterialPathsFromPrefab(path, materialPaths);
            }
            return materialPaths;
        }

        private static void CollectParticleRendererMaterialPathsFromPrefab(string prefabPath, HashSet<string> materialPaths)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) return;

            Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer renderer in renderers)
            {
                if (!(renderer is ParticleSystemRenderer) && !(renderer is TrailRenderer)) continue;

                Material[] materials = renderer.sharedMaterials;
                foreach (Material material in materials)
                {
                    if (material == null) continue;

                    string materialPath = AssetDatabase.GetAssetPath(material);
                    if (!string.IsNullOrEmpty(materialPath))
                    {
                        materialPaths.Add(NormalizeProjectPath(materialPath));
                    }
                }
            }
        }

        private static string NormalizeProjectPath(string path)
        {
            return string.IsNullOrEmpty(path) ? string.Empty : path.Replace('\\', '/');
        }

        #endregion

        #region Material Conversion

        /// <summary>
        /// Converts a single material asset in-place from BIRP to the appropriate SRP shader.
        /// Mirrors the conversion logic from Unity's built-in MaterialUpgrader providers:
        /// - Standard / Standard (Specular setup) → URP/Lit or HDRP/Lit
        /// - Legacy Shaders/* → URP/Simple Lit or HDRP/Lit
        /// - Particles/Standard Surface, Particles/Standard Unlit → URP/HDRP Particles shaders
        /// - Mobile/* → URP/Simple Lit or HDRP/Lit
        /// Handles rendering mode (opaque/cutout/fade/transparent), specular workflow,
        /// smoothness source, keywords, and blend state.
        /// </summary>
        private static void ConvertMaterial(Material mat, string shaderName, bool isOnURP, bool isOnHDRP)
        {
            // --- Modern BIRP particle shaders (Particles/Standard Surface, Particles/Standard Unlit) ---
            if (shaderName == "Particles/Standard Surface" || shaderName == "Particles/Standard Unlit")
            {
                ConvertParticleMaterial(mat, shaderName, isOnURP);
                return;
            }

            // --- Standard (metallic workflow) ---
            if (shaderName == "Standard")
            {
                ConvertStandardMaterial(mat, isOnURP, isOnHDRP, specularSetup: false);
                return;
            }

            // --- Standard (Specular setup) ---
            if (shaderName == "Standard (Specular setup)")
            {
                ConvertStandardMaterial(mat, isOnURP, isOnHDRP, specularSetup: true);
                return;
            }

            // --- Legacy / Mobile / Sprites shaders → SimpleLit (URP) or Lit (HDRP) ---
            ConvertLegacyMaterial(mat, shaderName, isOnURP, isOnHDRP);
        }

        /// <summary>
        /// Converts a Standard or Standard (Specular setup) material to URP/Lit or HDRP/Lit.
        /// Closely follows Unity's StandardUpgrader logic.
        /// </summary>
        private static void ConvertStandardMaterial(Material mat, bool isOnURP, bool isOnHDRP, bool specularSetup)
        {
            // Cache properties before shader change
            Color color = mat.HasProperty("_Color") ? mat.GetColor("_Color") : Color.white;
            Texture mainTex = mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") : null;
            Vector2 tiling = mat.HasProperty("_MainTex") ? mat.GetTextureScale("_MainTex") : Vector2.one;
            Vector2 offset = mat.HasProperty("_MainTex") ? mat.GetTextureOffset("_MainTex") : Vector2.zero;
            float metallic = mat.HasProperty("_Metallic") ? mat.GetFloat("_Metallic") : 0f;
            float glossiness = mat.HasProperty("_Glossiness") ? mat.GetFloat("_Glossiness") : 0.5f;
            float glossMapScale = mat.HasProperty("_GlossMapScale") ? mat.GetFloat("_GlossMapScale") : 1f;
            Texture metallicGlossMap = mat.HasProperty("_MetallicGlossMap") ? mat.GetTexture("_MetallicGlossMap") : null;
            Texture specGlossMap = mat.HasProperty("_SpecGlossMap") ? mat.GetTexture("_SpecGlossMap") : null;
            Color specColor = mat.HasProperty("_SpecColor") ? mat.GetColor("_SpecColor") : new Color(0.2f, 0.2f, 0.2f, 1f);
            Texture bumpMap = mat.HasProperty("_BumpMap") ? mat.GetTexture("_BumpMap") : null;
            float bumpScale = mat.HasProperty("_BumpScale") ? mat.GetFloat("_BumpScale") : 1f;
            Texture occlusionMap = mat.HasProperty("_OcclusionMap") ? mat.GetTexture("_OcclusionMap") : null;
            float occlusionStrength = mat.HasProperty("_OcclusionStrength") ? mat.GetFloat("_OcclusionStrength") : 1f;
            bool emissionEnabled = mat.IsKeywordEnabled("_EMISSION");
            Color emissionColor = mat.HasProperty("_EmissionColor") ? mat.GetColor("_EmissionColor") : Color.black;
            Texture emissionMap = mat.HasProperty("_EmissionMap") ? mat.GetTexture("_EmissionMap") : null;
            float renderingMode = mat.HasProperty("_Mode") ? mat.GetFloat("_Mode") : 0f;
            float cutoff = mat.HasProperty("_Cutoff") ? mat.GetFloat("_Cutoff") : 0.5f;
            float glossyReflections = mat.HasProperty("_GlossyReflections") ? mat.GetFloat("_GlossyReflections") : 1f;
            Texture detailAlbedo = mat.HasProperty("_DetailAlbedoMap") ? mat.GetTexture("_DetailAlbedoMap") : null;
            Texture detailNormal = mat.HasProperty("_DetailNormalMap") ? mat.GetTexture("_DetailNormalMap") : null;
            float detailNormalScale = mat.HasProperty("_DetailNormalMapScale") ? mat.GetFloat("_DetailNormalMapScale") : 1f;
            Vector2 detailScale = mat.HasProperty("_DetailAlbedoMap") ? mat.GetTextureScale("_DetailAlbedoMap") : Vector2.one;
            Vector2 detailOffset = mat.HasProperty("_DetailAlbedoMap") ? mat.GetTextureOffset("_DetailAlbedoMap") : Vector2.zero;
            Texture detailMask = mat.HasProperty("_DetailMask") ? mat.GetTexture("_DetailMask") : null;

            // Change shader
            string targetShaderName = isOnURP ? "Universal Render Pipeline/Lit" : "HDRP/Lit";
            Shader targetShader = Shader.Find(targetShaderName);
            if (targetShader == null) return;

            mat.shader = targetShader;

            // --- Remap properties (mirrors Unity's StandardUpgrader RenameTexture/RenameColor/RenameFloat) ---
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mainTex != null && mat.HasProperty("_BaseMap"))
            {
                mat.SetTexture("_BaseMap", mainTex);
                mat.SetTextureScale("_BaseMap", tiling);
                mat.SetTextureOffset("_BaseMap", offset);
            }

            // Smoothness: Unity picks from _GlossMapScale when metallic/spec map exists, else _Glossiness
            float smoothness;
            if (specularSetup)
            {
                smoothness = specGlossMap != null ? glossMapScale : glossiness;
            }
            else
            {
                smoothness = metallicGlossMap != null ? glossMapScale : glossiness;
            }
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);

            // Workflow mode: 1.0 = Metallic, 0.0 = Specular
            if (mat.HasProperty("_WorkflowMode")) mat.SetFloat("_WorkflowMode", specularSetup ? 0f : 1f);

            // Metallic / Specular maps
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metallic);
            if (metallicGlossMap != null && mat.HasProperty("_MetallicGlossMap")) mat.SetTexture("_MetallicGlossMap", metallicGlossMap);
            if (specularSetup)
            {
                if (mat.HasProperty("_SpecColor")) mat.SetColor("_SpecColor", specColor);
                if (specGlossMap != null && mat.HasProperty("_SpecGlossMap")) mat.SetTexture("_SpecGlossMap", specGlossMap);
            }

            // Normal map
            if (bumpMap != null && mat.HasProperty("_BumpMap"))
            {
                mat.SetTexture("_BumpMap", bumpMap);
                if (mat.HasProperty("_BumpScale")) mat.SetFloat("_BumpScale", bumpScale);
            }

            // Occlusion
            if (occlusionMap != null && mat.HasProperty("_OcclusionMap"))
            {
                mat.SetTexture("_OcclusionMap", occlusionMap);
                if (mat.HasProperty("_OcclusionStrength")) mat.SetFloat("_OcclusionStrength", occlusionStrength);
            }

            // Emission
            if (emissionEnabled)
            {
                mat.EnableKeyword("_EMISSION");
                if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", emissionColor);
                if (emissionMap != null && mat.HasProperty("_EmissionMap")) mat.SetTexture("_EmissionMap", emissionMap);
            }

            // Environment reflections
            if (mat.HasProperty("_EnvironmentReflections")) mat.SetFloat("_EnvironmentReflections", glossyReflections);

            // Detail maps
            if (detailAlbedo != null && mat.HasProperty("_DetailAlbedoMap"))
            {
                mat.SetTexture("_DetailAlbedoMap", detailAlbedo);
                // In URP detail tile/offset is multiplied with base, so adjust accordingly
                if (isOnURP)
                {
                    Vector2 adjScale = new Vector2(
                        tiling.x != 0 ? detailScale.x / tiling.x : 0,
                        tiling.y != 0 ? detailScale.y / tiling.y : 0);
                    mat.SetTextureScale("_DetailAlbedoMap", adjScale);
                    mat.SetTextureOffset("_DetailAlbedoMap", new Vector2(
                        detailOffset.x - offset.x * adjScale.x,
                        detailOffset.y - offset.y * adjScale.y));
                }
                else
                {
                    mat.SetTextureScale("_DetailAlbedoMap", detailScale);
                    mat.SetTextureOffset("_DetailAlbedoMap", detailOffset);
                }
            }
            if (detailNormal != null && mat.HasProperty("_DetailNormalMap"))
            {
                mat.SetTexture("_DetailNormalMap", detailNormal);
                if (mat.HasProperty("_DetailNormalMapScale")) mat.SetFloat("_DetailNormalMapScale", detailNormalScale);
            }
            if (detailMask != null && mat.HasProperty("_DetailMask")) mat.SetTexture("_DetailMask", detailMask);

            // Alpha clip
            bool isAlphaTest = mat.IsKeywordEnabled("_ALPHATEST_ON") || Mathf.Approximately(renderingMode, 1f);
            if (isAlphaTest && mat.HasProperty("_AlphaClip"))
            {
                mat.SetFloat("_AlphaClip", 1f);
                if (mat.HasProperty("_Cutoff")) mat.SetFloat("_Cutoff", cutoff);
            }

            // Surface type and blend mode (mirrors Unity's UpdateSurfaceTypeAndBlendMode)
            // _Mode in BIRP: 0=Opaque, 1=Cutout, 2=Fade, 3=Transparent
            int mode = Mathf.RoundToInt(renderingMode);
            if (isOnURP)
            {
                switch (mode)
                {
                    case 3: // Transparent → Premultiply
                        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                        if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f); // Transparent
                        if (mat.HasProperty("_Blend")) mat.SetFloat("_Blend", 1f); // Premultiply
                        break;
                    case 2: // Fade → Alpha
                        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                        if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f); // Transparent
                        if (mat.HasProperty("_Blend")) mat.SetFloat("_Blend", 0f); // Alpha
                        break;
                    default: // Opaque or Cutout
                        mat.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
                        if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 0f); // Opaque
                        break;
                }
            }
            else // HDRP
            {
                switch (mode)
                {
                    case 2: // Fade
                    case 3: // Transparent
                        if (mat.HasProperty("_SurfaceType")) mat.SetFloat("_SurfaceType", 1f);
                        if (mat.HasProperty("_BlendMode")) mat.SetFloat("_BlendMode", mode == 3 ? 4f : 0f); // Premultiply : Alpha
                        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                        mat.EnableKeyword("_ENABLE_FOG_ON_TRANSPARENT");
                        break;
                    default:
                        if (mat.HasProperty("_SurfaceType")) mat.SetFloat("_SurfaceType", 0f);
                        mat.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
                        break;
                }
            }

            // Keywords (mirrors Unity's UpdateStandardMaterialKeywords / UpdateStandardSpecularMaterialKeywords)
            SetKeyword(mat, "_NORMALMAP", bumpMap != null);
            SetKeyword(mat, "_OCCLUSIONMAP", occlusionMap != null);
            if (specularSetup)
            {
                SetKeyword(mat, "_METALLICSPECGLOSSMAP", specGlossMap != null);
                SetKeyword(mat, "_SPECULAR_SETUP", true);
            }
            else
            {
                SetKeyword(mat, "_METALLICSPECGLOSSMAP", metallicGlossMap != null);
            }
            mat.DisableKeyword("LOD_FADE_CROSSFADE");

            EditorUtility.SetDirty(mat);
        }

        /// <summary>
        /// Converts legacy BIRP shaders (Legacy Shaders/*, Mobile/*, Sprites/Diffuse)
        /// to URP/Simple Lit or HDRP/Lit. Mirrors Unity's StandardSimpleLightingUpgrader.
        /// </summary>
        private static void ConvertLegacyMaterial(Material mat, string shaderName, bool isOnURP, bool isOnHDRP)
        {
            // Cache common properties
            Color color = mat.HasProperty("_Color") ? mat.GetColor("_Color") : Color.white;
            Texture mainTex = mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") : null;
            Vector2 tiling = mat.HasProperty("_MainTex") ? mat.GetTextureScale("_MainTex") : Vector2.one;
            Vector2 offset = mat.HasProperty("_MainTex") ? mat.GetTextureOffset("_MainTex") : Vector2.zero;
            Texture bumpMap = mat.HasProperty("_BumpMap") ? mat.GetTexture("_BumpMap") : null;
            float bumpScale = mat.HasProperty("_BumpScale") ? mat.GetFloat("_BumpScale") : 1f;
            float shininess = mat.HasProperty("_Shininess") ? mat.GetFloat("_Shininess") : 0.5f;
            Color specColor = mat.HasProperty("_SpecColor") ? mat.GetColor("_SpecColor") : new Color(0.2f, 0.2f, 0.2f, 1f);
            bool emissionEnabled = mat.IsKeywordEnabled("_EMISSION");
            Color emissionColor = mat.HasProperty("_EmissionColor") ? mat.GetColor("_EmissionColor") : Color.black;
            Texture emissionMap = mat.HasProperty("_EmissionMap") ? mat.GetTexture("_EmissionMap") : null;
            Texture illumTex = mat.HasProperty("_Illum") ? mat.GetTexture("_Illum") : null;
            float cutoff = mat.HasProperty("_Cutoff") ? mat.GetFloat("_Cutoff") : 0.5f;

            // Determine upgrade parameters based on shader name (mirrors MaterialUpgraderProviders)
            bool isTransparent = shaderName.Contains("Transparent/") && !shaderName.Contains("Cutout/");
            bool isCutout = shaderName.Contains("Cutout/") || shaderName.Contains("Cutout");
            bool isSpecular = shaderName.Contains("Specular") || shaderName.Contains("VertexLit");
            bool isSelfIllum = shaderName.Contains("Self-Illumin");

            // Target shader
            string targetShaderName;
            if (isOnURP)
            {
                targetShaderName = "Universal Render Pipeline/Simple Lit";
            }
            else
            {
                // HDRP has no SimpleLit, use Lit
                targetShaderName = "HDRP/Lit";
            }
            Shader targetShader = Shader.Find(targetShaderName);
            if (targetShader == null)
            {
                // Fallback to Lit if SimpleLit not found
                targetShader = Shader.Find(isOnURP ? "Universal Render Pipeline/Lit" : "HDRP/Lit");
                if (targetShader == null) return;
            }

            mat.shader = targetShader;

            if (mainTex != null && mat.HasProperty("_BaseMap"))
            {
                mat.SetTexture("_BaseMap", mainTex);
                mat.SetTextureScale("_BaseMap", tiling);
                mat.SetTextureOffset("_BaseMap", offset);
            }

            // Smoothness from shininess
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", shininess);

            // Normal map
            if (bumpMap != null && mat.HasProperty("_BumpMap"))
            {
                mat.SetTexture("_BumpMap", bumpMap);
                if (mat.HasProperty("_BumpScale")) mat.SetFloat("_BumpScale", bumpScale);
            }

            // Specular color
            if (isSpecular && mat.HasProperty("_SpecColor")) mat.SetColor("_SpecColor", specColor);

            // Self-Illumin: _Illum → _EmissionMap
            if (isSelfIllum && illumTex != null)
            {
                if (mat.HasProperty("_EmissionMap")) mat.SetTexture("_EmissionMap", illumTex);
                if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", Color.white);
                mat.EnableKeyword("_EMISSION");
            }
            else if (emissionEnabled)
            {
                mat.EnableKeyword("_EMISSION");
                if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", emissionColor);
                if (emissionMap != null && mat.HasProperty("_EmissionMap")) mat.SetTexture("_EmissionMap", emissionMap);
            }

            // Surface type
            if (isOnURP)
            {
                if (isTransparent)
                {
                    mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f);
                    if (mat.HasProperty("_Blend")) mat.SetFloat("_Blend", 0f); // Alpha
                }
                else
                {
                    mat.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 0f);
                }

                if (isCutout)
                {
                    if (mat.HasProperty("_AlphaClip")) mat.SetFloat("_AlphaClip", 1f);
                    if (mat.HasProperty("_Cutoff")) mat.SetFloat("_Cutoff", cutoff);
                }
            }
            else // HDRP
            {
                if (isTransparent)
                {
                    if (mat.HasProperty("_SurfaceType")) mat.SetFloat("_SurfaceType", 1f);
                    mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    mat.EnableKeyword("_ENABLE_FOG_ON_TRANSPARENT");
                }
                else
                {
                    if (mat.HasProperty("_SurfaceType")) mat.SetFloat("_SurfaceType", 0f);
                    mat.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
                }

                if (isCutout)
                {
                    if (mat.HasProperty("_AlphaCutoffEnable")) mat.SetFloat("_AlphaCutoffEnable", 1f);
                    if (mat.HasProperty("_AlphaCutoff")) mat.SetFloat("_AlphaCutoff", cutoff);
                    mat.EnableKeyword("_ALPHATEST_ON");
                }
            }

            // Keywords
            SetKeyword(mat, "_NORMALMAP", bumpMap != null);

            // Specular source for SimpleLit
            if (isOnURP)
            {
                if (isSpecular)
                {
                    if (mat.HasProperty("_SpecularHighlights")) mat.SetFloat("_SpecularHighlights", 1f);
                }
                else
                {
                    if (mat.HasProperty("_SpecularHighlights")) mat.SetFloat("_SpecularHighlights", 0f);
                }
            }

            mat.DisableKeyword("LOD_FADE_CROSSFADE");

            EditorUtility.SetDirty(mat);
        }

        private static void ConvertCustomSurfaceMaterial(Material mat, string originalShaderName, MaterialConversionInfo conversionInfo, bool isOnURP, bool isOnHDRP)
        {
            Color color = mat.HasProperty("_Color") ? mat.GetColor("_Color") : Color.white;
            Texture mainTex = mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") : null;
            Vector2 tiling = mat.HasProperty("_MainTex") ? mat.GetTextureScale("_MainTex") : Vector2.one;
            Vector2 offset = mat.HasProperty("_MainTex") ? mat.GetTextureOffset("_MainTex") : Vector2.zero;
            float cutoff = mat.HasProperty("_Cutoff") ? mat.GetFloat("_Cutoff") : 0.5f;

            string targetShaderName = isOnURP ? "Universal Render Pipeline/Simple Lit" : "HDRP/Lit";
            Shader targetShader = Shader.Find(targetShaderName);
            if (targetShader == null)
            {
                targetShaderName = isOnURP ? "Universal Render Pipeline/Lit" : "HDRP/Lit";
                targetShader = Shader.Find(targetShaderName);
                if (targetShader == null) return;
            }

            mat.shader = targetShader;

            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mainTex != null && mat.HasProperty("_BaseMap"))
            {
                mat.SetTexture("_BaseMap", mainTex);
                mat.SetTextureScale("_BaseMap", tiling);
                mat.SetTextureOffset("_BaseMap", offset);
            }

            ApplyConvertedSurfaceSettings(mat, conversionInfo.SurfaceMode, cutoff, isOnURP, isOnHDRP);
            ApplyDoubleSidedSettings(mat, conversionInfo.DoubleSided, isOnURP, isOnHDRP);

            mat.DisableKeyword("LOD_FADE_CROSSFADE");
            EditorUtility.SetDirty(mat);

            if (AI.Config.LogPreviewCreation)
            {
                Debug.Log($"[Asset Inventory] Converted legacy custom material '{AssetDatabase.GetAssetPath(mat)}' from shader '{originalShaderName}' to '{targetShader.name}' (surface={conversionInfo.SurfaceMode}, doubleSided={conversionInfo.DoubleSided}).");
            }
        }

        /// <summary>
        /// Converts BIRP particle materials to URP or HDRP particle shaders.
        /// Handles both "modern" BIRP particle shaders (Particles/Standard Surface, Particles/Standard Unlit)
        /// and legacy simple particle shaders (Particles/Additive, Particles/Alpha Blended, Particles/Multiply, etc.).
        /// </summary>
        private static void ConvertParticleMaterial(Material mat, string shaderName, bool isOnURP)
        {
            string textureProperty = mat.HasProperty("_MainTex") && mat.GetTexture("_MainTex") != null
                ? "_MainTex"
                : mat.HasProperty("_BaseMap") && mat.GetTexture("_BaseMap") != null
                    ? "_BaseMap"
                    : null;

            Texture mainTex = textureProperty != null ? mat.GetTexture(textureProperty) : null;
            Vector2 tiling = textureProperty != null ? mat.GetTextureScale(textureProperty) : Vector2.one;
            Vector2 offset = textureProperty != null ? mat.GetTextureOffset(textureProperty) : Vector2.zero;
            Color color = ResolveParticleMaterialColor(mat, shaderName);
            float glossiness = mat.HasProperty("_Glossiness") ? mat.GetFloat("_Glossiness") : 0.5f;
            float flipbook = mat.HasProperty("_FlipbookMode") ? mat.GetFloat("_FlipbookMode") : 0f;
            float cutoff = mat.HasProperty("_Cutoff") ? mat.GetFloat("_Cutoff") : 0.5f;

            bool isModernLitParticle = string.Equals(shaderName, "Particles/Standard Surface", StringComparison.Ordinal);
            string targetShaderName;
            if (isOnURP)
            {
                targetShaderName = isModernLitParticle
                    ? "Universal Render Pipeline/Particles/Lit"
                    : "Universal Render Pipeline/Particles/Unlit";
            }
            else
            {
                targetShaderName = "HDRP/Particles/Unlit";
            }

            Shader targetShader = Shader.Find(targetShaderName);
            if (targetShader == null) return;

            mat.shader = targetShader;

            // Remap properties
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mainTex != null && mat.HasProperty("_BaseMap"))
            {
                mat.SetTexture("_BaseMap", mainTex);
                mat.SetTextureScale("_BaseMap", tiling);
                mat.SetTextureOffset("_BaseMap", offset);
            }
            if (mat.HasProperty("_MainTex") && mainTex != null)
            {
                mat.SetTexture("_MainTex", mainTex);
                mat.SetTextureScale("_MainTex", tiling);
                mat.SetTextureOffset("_MainTex", offset);
            }
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            if (isModernLitParticle && mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", glossiness);
            if (mat.HasProperty("_FlipbookBlending")) mat.SetFloat("_FlipbookBlending", flipbook);

            if (TryApplyModernParticleMode(mat, shaderName, cutoff))
            {
                mat.DisableKeyword("LOD_FADE_CROSSFADE");
                EditorUtility.SetDirty(mat);
                return;
            }

            ApplyParticleBlendSettings(mat, PrefabPreviewUtilities.ResolveParticleBlendModeForShaderName(shaderName));

            mat.DisableKeyword("LOD_FADE_CROSSFADE");
            EditorUtility.SetDirty(mat);
        }

        private static Color ResolveParticleMaterialColor(Material mat, string shaderName)
        {
            if (mat.HasProperty("_TintColor"))
            {
                Color tint = mat.GetColor("_TintColor");
                float tintMultiplier = UsesLegacyParticleDoubleTintConvention(shaderName) ? 2f : 1f;
                return new Color(tint.r * tintMultiplier, tint.g * tintMultiplier, tint.b * tintMultiplier, tint.a);
            }
            if (mat.HasProperty("_Color")) return mat.GetColor("_Color");
            if (mat.HasProperty("_BaseColor")) return mat.GetColor("_BaseColor");
            return Color.white;
        }

        private static bool TryApplyModernParticleMode(Material mat, string shaderName, float cutoff)
        {
            string normalizedShaderName = NormalizeLegacyParticleShaderName(shaderName);
            bool supportsModernMode = normalizedShaderName == "Particles/Standard Unlit" ||
                normalizedShaderName == "Particles/Standard Surface" ||
                normalizedShaderName == "Standard" ||
                normalizedShaderName == "Standard (Specular setup)";
            if (!supportsModernMode || !mat.HasProperty("_Mode")) return false;

            int mode = Mathf.RoundToInt(mat.GetFloat("_Mode"));
            switch (mode)
            {
                case 0:
                    SetFloatIfPresent(mat, "_Surface", 0f);
                    SetKeyword(mat, "_SURFACE_TYPE_TRANSPARENT", false);
                    SetKeyword(mat, "_ALPHATEST_ON", false);
                    return true;
                case 1:
                    SetFloatIfPresent(mat, "_Surface", 0f);
                    SetFloatIfPresent(mat, "_AlphaClip", 1f);
                    SetFloatIfPresent(mat, "_Cutoff", cutoff);
                    SetKeyword(mat, "_SURFACE_TYPE_TRANSPARENT", false);
                    SetKeyword(mat, "_ALPHATEST_ON", true);
                    return true;
                case 4:
                    ApplyParticleBlendSettings(mat, 2);
                    return true;
                case 5:
                    ApplyParticleBlendSettings(mat, 4);
                    return true;
                case 6:
                    ApplyParticleBlendSettings(mat, 3);
                    return true;
                default:
                    ApplyParticleBlendSettings(mat, 0);
                    return true;
            }
        }

        internal static void ApplyParticleBlendSettings(Material mat, int blendMode)
        {
            SetFloatIfPresent(mat, "_Surface", 1f);
            SetFloatIfPresent(mat, "_Blend", blendMode);
            SetFloatIfPresent(mat, "_AlphaClip", 0f);
            SetKeyword(mat, "_SURFACE_TYPE_TRANSPARENT", true);
            SetKeyword(mat, "_ALPHATEST_ON", false);
            SetKeyword(mat, "_ALPHAPREMULTIPLY_ON", false);
            SetKeyword(mat, "_ALPHAMODULATE_ON", false);

            switch (blendMode)
            {
                case 1:
                    SetIntIfPresent(mat, "_SrcBlend", (int)BlendMode.One);
                    SetIntIfPresent(mat, "_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                    SetFloatIfPresent(mat, "_SrcBlendAlpha", (float)BlendMode.One);
                    SetFloatIfPresent(mat, "_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
                    SetIntIfPresent(mat, "_ZWrite", 0);
                    SetKeyword(mat, "_ALPHAPREMULTIPLY_ON", true);
                    break;
                case 2:
                    SetIntIfPresent(mat, "_SrcBlend", (int)BlendMode.One);
                    SetIntIfPresent(mat, "_DstBlend", (int)BlendMode.One);
                    SetFloatIfPresent(mat, "_SrcBlendAlpha", (float)BlendMode.One);
                    SetFloatIfPresent(mat, "_DstBlendAlpha", (float)BlendMode.One);
                    SetIntIfPresent(mat, "_ZWrite", 0);
                    SetKeyword(mat, "_ALPHAPREMULTIPLY_ON", true);
                    break;
                case 3:
                    SetIntIfPresent(mat, "_SrcBlend", (int)BlendMode.DstColor);
                    SetIntIfPresent(mat, "_DstBlend", (int)BlendMode.Zero);
                    SetFloatIfPresent(mat, "_SrcBlendAlpha", (float)BlendMode.Zero);
                    SetFloatIfPresent(mat, "_DstBlendAlpha", (float)BlendMode.One);
                    SetIntIfPresent(mat, "_ZWrite", 0);
                    SetKeyword(mat, "_ALPHAMODULATE_ON", true);
                    break;
                default:
                    SetIntIfPresent(mat, "_SrcBlend", (int)BlendMode.SrcAlpha);
                    SetIntIfPresent(mat, "_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                    SetFloatIfPresent(mat, "_SrcBlendAlpha", (float)BlendMode.One);
                    SetFloatIfPresent(mat, "_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
                    SetIntIfPresent(mat, "_ZWrite", 0);
                    break;
            }
        }

        private static bool IsLegacyParticleShader(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName)) return false;
            if (shaderName.StartsWith("Legacy Shaders/Particles/", StringComparison.OrdinalIgnoreCase)) return true;
            if (shaderName.StartsWith("Mobile/Particles/", StringComparison.OrdinalIgnoreCase)) return true;
            if (shaderName.StartsWith("Particles/", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool UsesLegacyParticleDoubleTintConvention(string shaderName)
        {
            string normalizedShaderName = NormalizeLegacyParticleShaderName(shaderName);
            return normalizedShaderName.StartsWith("Particles/", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeLegacyParticleShaderName(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName)) return string.Empty;
            if (shaderName.StartsWith("Legacy Shaders/", StringComparison.OrdinalIgnoreCase)) return shaderName.Substring("Legacy Shaders/".Length);
            if (shaderName.StartsWith("Mobile/", StringComparison.OrdinalIgnoreCase)) return shaderName.Substring("Mobile/".Length);
            return shaderName;
        }

        #endregion

        #region Helpers

        internal static void ApplyConvertedSurfaceSettings(Material mat, MaterialSurfaceMode surfaceMode, float cutoff, bool isOnURP, bool isOnHDRP)
        {
            if (mat == null) return;

            if (isOnURP)
            {
                switch (surfaceMode)
                {
                    case MaterialSurfaceMode.Cutout:
                        mat.SetOverrideTag("RenderType", "TransparentCutout");
                        mat.renderQueue = (int)RenderQueue.AlphaTest;
                        SetFloatIfPresent(mat, "_Surface", 0f);
                        SetFloatIfPresent(mat, "_AlphaClip", 1f);
                        SetFloatIfPresent(mat, "_AlphaToMask", 1f);
                        SetFloatIfPresent(mat, "_Cutoff", cutoff);
                        SetIntIfPresent(mat, "_SrcBlend", (int)BlendMode.One);
                        SetIntIfPresent(mat, "_DstBlend", (int)BlendMode.Zero);
                        SetFloatIfPresent(mat, "_SrcBlendAlpha", (float)BlendMode.One);
                        SetFloatIfPresent(mat, "_DstBlendAlpha", (float)BlendMode.Zero);
                        SetIntIfPresent(mat, "_ZWrite", 1);
                        SetKeyword(mat, "_ALPHATEST_ON", true);
                        SetKeyword(mat, "_SURFACE_TYPE_TRANSPARENT", false);
                        SetKeyword(mat, "_ALPHAPREMULTIPLY_ON", false);
                        SetKeyword(mat, "_ALPHAMODULATE_ON", false);
                        break;
                    case MaterialSurfaceMode.Transparent:
                        mat.SetOverrideTag("RenderType", "Transparent");
                        mat.renderQueue = (int)RenderQueue.Transparent;
                        SetFloatIfPresent(mat, "_Surface", 1f);
                        SetFloatIfPresent(mat, "_Blend", 0f);
                        SetFloatIfPresent(mat, "_AlphaClip", 0f);
                        SetIntIfPresent(mat, "_SrcBlend", (int)BlendMode.SrcAlpha);
                        SetIntIfPresent(mat, "_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                        SetFloatIfPresent(mat, "_SrcBlendAlpha", (float)BlendMode.One);
                        SetFloatIfPresent(mat, "_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
                        SetIntIfPresent(mat, "_ZWrite", 0);
                        SetKeyword(mat, "_SURFACE_TYPE_TRANSPARENT", true);
                        SetKeyword(mat, "_ALPHATEST_ON", false);
                        SetKeyword(mat, "_ALPHAPREMULTIPLY_ON", false);
                        SetKeyword(mat, "_ALPHAMODULATE_ON", false);
                        break;
                    default:
                        mat.SetOverrideTag("RenderType", "");
                        mat.renderQueue = -1;
                        SetFloatIfPresent(mat, "_Surface", 0f);
                        SetFloatIfPresent(mat, "_AlphaClip", 0f);
                        SetIntIfPresent(mat, "_SrcBlend", (int)BlendMode.One);
                        SetIntIfPresent(mat, "_DstBlend", (int)BlendMode.Zero);
                        SetFloatIfPresent(mat, "_SrcBlendAlpha", (float)BlendMode.One);
                        SetFloatIfPresent(mat, "_DstBlendAlpha", (float)BlendMode.Zero);
                        SetIntIfPresent(mat, "_ZWrite", 1);
                        SetKeyword(mat, "_SURFACE_TYPE_TRANSPARENT", false);
                        SetKeyword(mat, "_ALPHATEST_ON", false);
                        SetKeyword(mat, "_ALPHAPREMULTIPLY_ON", false);
                        SetKeyword(mat, "_ALPHAMODULATE_ON", false);
                        break;
                }
            }
            else if (isOnHDRP)
            {
                switch (surfaceMode)
                {
                    case MaterialSurfaceMode.Cutout:
                        SetFloatIfPresent(mat, "_SurfaceType", 0f);
                        SetFloatIfPresent(mat, "_AlphaCutoffEnable", 1f);
                        SetFloatIfPresent(mat, "_AlphaCutoff", cutoff);
                        SetFloatIfPresent(mat, "_Cutoff", cutoff);
                        mat.SetOverrideTag("RenderType", "TransparentCutout");
                        mat.renderQueue = (int)RenderQueue.AlphaTest;
                        SetKeyword(mat, "_ALPHATEST_ON", true);
                        SetKeyword(mat, "_SURFACE_TYPE_TRANSPARENT", false);
                        break;
                    case MaterialSurfaceMode.Transparent:
                        SetFloatIfPresent(mat, "_SurfaceType", 1f);
                        SetFloatIfPresent(mat, "_BlendMode", 0f);
                        mat.SetOverrideTag("RenderType", "Transparent");
                        mat.renderQueue = (int)RenderQueue.Transparent;
                        SetKeyword(mat, "_SURFACE_TYPE_TRANSPARENT", true);
                        SetKeyword(mat, "_ENABLE_FOG_ON_TRANSPARENT", true);
                        SetKeyword(mat, "_ALPHATEST_ON", false);
                        break;
                    default:
                        SetFloatIfPresent(mat, "_SurfaceType", 0f);
                        SetFloatIfPresent(mat, "_AlphaCutoffEnable", 0f);
                        mat.SetOverrideTag("RenderType", "");
                        mat.renderQueue = -1;
                        SetKeyword(mat, "_SURFACE_TYPE_TRANSPARENT", false);
                        SetKeyword(mat, "_ALPHATEST_ON", false);
                        break;
                }
            }
        }

        internal static void ApplyDoubleSidedSettings(Material mat, bool doubleSided, bool isOnURP, bool isOnHDRP)
        {
            if (mat == null || !doubleSided) return;

            if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);
            if (isOnHDRP)
            {
                SetFloatIfPresent(mat, "_DoubleSidedEnable", 1f);
                SetFloatIfPresent(mat, "_CullMode", 0f);
                SetFloatIfPresent(mat, "_CullModeForward", 0f);
                SetKeyword(mat, "_DOUBLESIDED_ON", true);
            }
        }

        private static void SetFloatIfPresent(Material mat, string property, float value)
        {
            if (mat.HasProperty(property)) mat.SetFloat(property, value);
        }

        private static void SetIntIfPresent(Material mat, string property, int value)
        {
            if (mat.HasProperty(property)) mat.SetInt(property, value);
        }

        private static void SetTextureIfPresent(Material mat, string property, Texture texture, Vector2 scale, Vector2 offset)
        {
            if (!mat.HasProperty(property)) return;
            mat.SetTexture(property, texture);
            mat.SetTextureScale(property, scale);
            mat.SetTextureOffset(property, offset);
        }

        private static void SetColorIfPresent(Material mat, string property, Color color)
        {
            if (mat.HasProperty(property)) mat.SetColor(property, color);
        }

        /// <summary>
        /// Helper to set or clear a shader keyword on a material.
        /// </summary>
        internal static void SetKeyword(Material mat, string keyword, bool enabled)
        {
            if (enabled) mat.EnableKeyword(keyword);
            else mat.DisableKeyword(keyword);
        }

        #endregion
    }
}

