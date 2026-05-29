Shader "AIBuilder/UI/CardDepth"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)
        _BevelWidth ("Bevel Width", Range(0.005, 0.18)) = 0.055
        _Depth ("Depth", Range(0, 1)) = 0.6
        _Grain ("Grain", Range(0, 1)) = 0.25
        _FoilStrength ("Foil Strength", Range(0, 1)) = 0.18
        _InnerGlow ("Inner Glow", Range(0, 1)) = 0.16
        _Vignette ("Vignette", Range(0, 1)) = 0.18
        _FoilColor ("Foil Color", Color) = (1.0, 0.76, 0.28, 1)
        _ShadowColor ("Shadow Color", Color) = (0.12, 0.035, 0.02, 1)
        _HighlightColor ("Highlight Color", Color) = (1.0, 0.92, 0.68, 1)

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
            Name "CardDepth"

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
            float _BevelWidth;
            float _Depth;
            float _Grain;
            float _FoilStrength;
            float _InnerGlow;
            float _Vignette;
            fixed4 _FoilColor;
            fixed4 _ShadowColor;
            fixed4 _HighlightColor;

            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

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

                float edge = min(min(uv.x, 1.0 - uv.x), min(uv.y, 1.0 - uv.y));
                float bevel = 1.0 - smoothstep(0.0, _BevelWidth, edge);
                float innerRidge = smoothstep(_BevelWidth, _BevelWidth * 1.9, edge)
                    * (1.0 - smoothstep(_BevelWidth * 1.9, _BevelWidth * 4.4, edge));

                float topLeft = saturate((1.0 - uv.x) * 0.72 + uv.y * 0.82);
                float bottomRight = saturate(uv.x * 0.66 + (1.0 - uv.y) * 0.82);
                float highlight = bevel * topLeft * _Depth;
                float shade = bevel * bottomRight * _Depth;

                float2 centered = uv - float2(0.5, 0.54);
                float vignette = smoothstep(0.28, 0.78, length(centered * float2(1.0, 1.24))) * _Vignette;
                float grain = (Hash21(uv * 780.0) - 0.5) * _Grain;
                float brushed = sin((uv.x * 120.0 + uv.y * 34.0) + _Time.y * 0.45) * 0.5 + 0.5;
                float foilBand = 1.0 - abs((uv.x + uv.y * 0.72 + sin(_Time.y * 0.24) * 0.08) - 1.16) * 4.2;
                float foil = pow(saturate(foilBand), 3.0) * (0.35 + bevel * 0.65) * _FoilStrength;

                color.rgb = lerp(color.rgb, _HighlightColor.rgb, highlight * 0.36);
                color.rgb = lerp(color.rgb, _ShadowColor.rgb, shade * 0.32 + vignette * 0.26);
                color.rgb += innerRidge * _InnerGlow * _HighlightColor.rgb * 0.16;
                color.rgb *= 1.0 + grain * 0.11 + brushed * _Grain * 0.025;
                color.rgb = lerp(color.rgb, _FoilColor.rgb, foil);
                color.rgb = max(color.rgb, 0.0);

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
