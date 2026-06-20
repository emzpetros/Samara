Shader "Impossible Robert/Common/Sample Lit"
{
    Properties
    {
        [MainTexture] _BaseMap ("Base Map", 2D) = "white" {}
        [MainColor] _BaseColor ("Base Color", Color) = (1,1,1,1)
        _Metallic ("Metallic", Range(0,1)) = 0
        _Smoothness ("Smoothness", Range(0,1)) = 0.5
        [HDR] _EmissionColor ("Emission Color", Color) = (0,0,0,1)
        [HideInInspector] _Color ("Legacy Base Color", Color) = (1,1,1,1)
        [HideInInspector] _BaseColorMap ("Base Color Map", 2D) = "white" {}
        [HideInInspector] _MainTex ("Main Texture", 2D) = "white" {}
        [HideInInspector] _EmissiveColor ("Emissive Color", Color) = (0,0,0,1)
        [HideInInspector] _EmissionIntensity ("Emission Intensity", Range(0,10)) = 1
        [HideInInspector] _EmissiveIntensity ("Emissive Intensity", Range(0,10)) = 1
        [HideInInspector] _Glossiness ("Glossiness", Range(0,1)) = 0.5
        [HideInInspector] _WorkflowMode ("Workflow Mode", Float) = 1
        [HideInInspector] _SpecColor ("Specular Color", Color) = (0.2,0.2,0.2,1)
        [HideInInspector] _SpecGlossMap ("Specular Gloss Map", 2D) = "white" {}
        [HideInInspector] _MetallicGlossMap ("Metallic Gloss Map", 2D) = "white" {}
        [HideInInspector] _BumpMap ("Normal Map", 2D) = "bump" {}
        [HideInInspector] _ParallaxMap ("Parallax Map", 2D) = "black" {}
        [HideInInspector] _OcclusionMap ("Occlusion Map", 2D) = "white" {}
        [HideInInspector] _DetailMask ("Detail Mask", 2D) = "white" {}
        [HideInInspector] _DetailAlbedoMap ("Detail Albedo Map", 2D) = "linearGrey" {}
        [HideInInspector] _DetailNormalMap ("Detail Normal Map", 2D) = "bump" {}
        [HideInInspector] _EmissionMap ("Emission Map", 2D) = "white" {}
        [HideInInspector] _BumpScale ("Normal Scale", Float) = 1
        [HideInInspector] _Parallax ("Parallax", Range(0.005,0.08)) = 0.005
        [HideInInspector] _OcclusionStrength ("Occlusion Strength", Range(0,1)) = 1
        [HideInInspector] _DetailAlbedoMapScale ("Detail Albedo Scale", Range(0,2)) = 1
        [HideInInspector] _DetailNormalMapScale ("Detail Normal Scale", Range(0,2)) = 1
        [HideInInspector] _SmoothnessTextureChannel ("Smoothness Texture Channel", Float) = 0
        [HideInInspector] _SpecularHighlights ("Specular Highlights", Float) = 1
        [HideInInspector] _EnvironmentReflections ("Environment Reflections", Float) = 1
        [HideInInspector] _ReceiveShadows ("Receive Shadows", Float) = 1
        [HideInInspector] _Surface ("Surface Type", Float) = 0
        [HideInInspector] _Blend ("Blend Mode", Float) = 0
        [HideInInspector] _Cutoff ("Alpha Clipping", Range(0,1)) = 0.5
        [HideInInspector] _AlphaCutoff ("Alpha Cutoff", Range(0,1)) = 0.5
        [HideInInspector] _AlphaClip ("Alpha Clip", Float) = 0
        [HideInInspector] _AlphaToMask ("Alpha To Mask", Float) = 0
        [HideInInspector] _QueueOffset ("Queue Offset", Float) = 0
        [HideInInspector] _BlendModePreserveSpecular ("Blend Preserve Specular", Float) = 1
        [HideInInspector] _AlphaCutoffEnable ("Alpha Cutoff Enable", Float) = 0
        [HideInInspector] _UseEmissiveIntensity ("Use Emissive Intensity", Int) = 0
        [HideInInspector] _SurfaceType ("Surface Type", Float) = 0
        [HideInInspector] _BlendMode ("Blend Mode", Float) = 0
        [HideInInspector] _SrcBlend ("Source Blend", Float) = 1
        [HideInInspector] _DstBlend ("Destination Blend", Float) = 0
        [HideInInspector] _SrcBlendAlpha ("Source Blend Alpha", Float) = 1
        [HideInInspector] _DstBlendAlpha ("Destination Blend Alpha", Float) = 0
        [HideInInspector] _AlphaSrcBlend ("Alpha Source Blend", Float) = 1
        [HideInInspector] _AlphaDstBlend ("Alpha Destination Blend", Float) = 0
        [HideInInspector] _ZWrite ("Z Write", Float) = 1
        [HideInInspector] _TransparentZWrite ("Transparent Z Write", Float) = 0
        [HideInInspector] _Cull ("Cull", Float) = 2
        [HideInInspector] _CullMode ("Cull Mode", Float) = 2
        [HideInInspector] _CullModeForward ("Cull Mode Forward", Float) = 2
        [HideInInspector] _OpaqueCullMode ("Opaque Cull Mode", Float) = 2
        [HideInInspector] _TransparentCullMode ("Transparent Cull Mode", Float) = 2
        [HideInInspector] _ZTestDepthEqualForOpaque ("ZTest Depth Equal For Opaque", Int) = 4
        [HideInInspector] _ZTestGBuffer ("ZTest GBuffer", Int) = 4
        [HideInInspector] _ZTestTransparent ("ZTest Transparent", Int) = 4
        [HideInInspector] _StencilRef ("Stencil Ref", Int) = 0
        [HideInInspector] _StencilWriteMask ("Stencil Write Mask", Int) = 3
        [HideInInspector] _StencilRefGBuffer ("Stencil Ref GBuffer", Int) = 2
        [HideInInspector] _StencilWriteMaskGBuffer ("Stencil Write Mask GBuffer", Int) = 3
        [HideInInspector] _StencilRefDepth ("Stencil Ref Depth", Int) = 0
        [HideInInspector] _StencilWriteMaskDepth ("Stencil Write Mask Depth", Int) = 8
        [HideInInspector] _DoubleSidedEnable ("Double Sided Enable", Float) = 0
        [HideInInspector] _DoubleSidedNormalMode ("Double Sided Normal Mode", Float) = 1
        [HideInInspector] _DoubleSidedConstants ("Double Sided Constants", Vector) = (1,1,-1,0)
        [HideInInspector] _MaterialID ("Material ID", Int) = 1
        [HideInInspector] _SupportDecals ("Support Decals", Float) = 1
    }

    SubShader
    {
        PackageRequirements { "com.unity.render-pipelines.universal" }
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "IgnoreProjector" = "True"
        }
        LOD 200
        Cull [_Cull]
        ZWrite On
        ZTest LEqual

        UsePass "Universal Render Pipeline/Lit/ForwardLit"
        UsePass "Universal Render Pipeline/Lit/ShadowCaster"
        UsePass "Universal Render Pipeline/Lit/DepthOnly"
        UsePass "Universal Render Pipeline/Lit/DepthNormals"
        UsePass "Universal Render Pipeline/Lit/Meta"
    }

    SubShader
    {
        PackageRequirements { "com.unity.render-pipelines.high-definition" }
        Tags
        {
            "RenderPipeline" = "HDRenderPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "IgnoreProjector" = "True"
        }
        LOD 150
        Cull [_Cull]
        ZWrite On
        ZTest LEqual

        UsePass "HDRP/Lit/GBuffer"
        UsePass "HDRP/Lit/META"
        UsePass "HDRP/Lit/ShadowCaster"
        UsePass "HDRP/Lit/DepthOnly"
        UsePass "HDRP/Lit/Forward"
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "IgnoreProjector" = "True"
        }
        LOD 200
        Cull [_Cull]
        ZWrite On
        ZTest LEqual

        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0

        sampler2D _BaseMap;
        fixed4 _BaseColor;
        fixed4 _EmissionColor;
        half _Metallic;
        half _Smoothness;

        struct Input
        {
            float2 uv_BaseMap;
        };

        void surf(Input input, inout SurfaceOutputStandard output)
        {
            fixed4 albedo = tex2D(_BaseMap, input.uv_BaseMap) * _BaseColor;
            output.Albedo = albedo.rgb;
            output.Metallic = _Metallic;
            output.Smoothness = _Smoothness;
            output.Emission = _EmissionColor.rgb;
            output.Alpha = albedo.a;
        }
        ENDCG
    }

    FallBack "Standard"
}
