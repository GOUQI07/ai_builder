Shader "AIBuilder/UI/BackgroundAtmosphere"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)
        _Vignette ("Vignette", Range(0, 1)) = 0.42
        _MistStrength ("Mist Strength", Range(0, 1)) = 0.18
        _GlowStrength ("Glow Strength", Range(0, 1)) = 0.14
        _Drift ("Drift", Range(0, 1)) = 0.16
        _MistColor ("Mist Color", Color) = (0.46, 0.58, 0.64, 1)
        _CenterGlowColor ("Center Glow Color", Color) = (0.90, 0.62, 0.30, 1)

        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "BackgroundAtmosphere"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP

            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            struct appdata_t
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
                float4 worldPosition : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            fixed4 _Color;
            fixed4 _TextureSampleAdd;
            float4 _ClipRect;
            float4 _MainTex_ST;
            float _Vignette;
            float _MistStrength;
            float _GlowStrength;
            float _Drift;
            fixed4 _MistColor;
            fixed4 _CenterGlowColor;

            v2f vert(appdata_t v)
            {
                v2f OUT;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.worldPosition = v.vertex;
                OUT.vertex = UnityObjectToClipPos(v.vertex);
                OUT.texcoord = TRANSFORM_TEX(v.texcoord, _MainTex);
                OUT.color = v.color * _Color;
                return OUT;
            }

            fixed4 frag(v2f IN) : SV_Target
            {
                float2 uv = saturate(IN.texcoord);
                fixed4 color = (tex2D(_MainTex, IN.texcoord) + _TextureSampleAdd) * IN.color;

                float time = _Time.y * lerp(0.05, 0.42, _Drift);
                float horizonMist = smoothstep(0.08, 0.72, uv.y) * (1.0 - smoothstep(0.78, 1.0, uv.y));
                float bands = sin((uv.y * 7.0 + uv.x * 1.9 + time) * 6.28318) * 0.5 + 0.5;
                float fine = sin((uv.x * 17.0 - uv.y * 9.0 - time * 1.7) * 6.28318) * 0.5 + 0.5;
                float mist = saturate(bands * 0.62 + fine * 0.22) * horizonMist * _MistStrength;

                float2 centerUv = (uv - float2(0.5, 0.48)) * float2(1.12, 1.35);
                float vignette = smoothstep(0.34, 0.82, length(centerUv)) * _Vignette;
                float glow = 1.0 - smoothstep(0.04, 0.72, length((uv - float2(0.5, 0.5)) * float2(1.35, 1.0)));

                color.rgb = lerp(color.rgb, _MistColor.rgb, mist);
                color.rgb += _CenterGlowColor.rgb * glow * _GlowStrength;
                color.rgb *= 1.0 - vignette * 0.72;

                #ifdef UNITY_UI_CLIP_RECT
                color.a *= UnityGet2DClipping(IN.worldPosition.xy, _ClipRect);
                #endif

                #ifdef UNITY_UI_ALPHACLIP
                clip(color.a - 0.001);
                #endif

                return color;
            }
            ENDCG
        }
    }
}
