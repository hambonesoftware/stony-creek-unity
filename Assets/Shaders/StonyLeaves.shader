// Leaf and needle cards: alpha-tested photo sprays, soft wrap lighting with back-lit translucency, wind.
Shader "Stony/Leaves"
{
    Properties
    {
        _MainTex ("Leaf spray (RGBA)", 2D) = "white" {}
        _Cutoff ("Alpha cutoff", Range(0, 1)) = 0.42
        _Color ("Colour", Color) = (1, 1, 1, 1)
        _TintA ("Instance tint A", Color) = (0.92, 1.0, 0.86, 1)
        _TintB ("Instance tint B", Color) = (1.08, 1.02, 0.86, 1)
        _Translucency ("Translucency", Range(0, 2)) = 0.7
        _LodFade ("Fade out from, to (m)", Vector) = (9000, 9001, 0, 0)
        _Flutter ("Flutter", Range(0, 0.2)) = 0.05
        _SwayScale ("Sway scale", Range(0, 2)) = 0.35
    }
    SubShader
    {
        Tags { "Queue" = "AlphaTest" "RenderType" = "TransparentCutout" "IgnoreProjector" = "True" }
        Cull Off
        CGPROGRAM
        #pragma surface surf Foliage vertex:vert addshadow nolightmap nodynlightmap nodirlightmap nometa
        #pragma multi_compile_instancing
        #pragma instancing_options assumeuniformscaling
        #pragma target 3.5
        #include "StonyCommon.cginc"

        sampler2D _MainTex;
        fixed _Cutoff, _Translucency, _Flutter, _SwayScale;
        fixed4 _Color, _TintA, _TintB;
        float4 _LodFade;

        struct Input { float2 uv_MainTex; float4 color : COLOR; float4 screenPos; float3 worldPos; };

        void vert(inout appdata_full v)
        {
            float3 origin = StonyObjectOrigin();
            float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
            float3 off = StonyWindOffset(wp, origin, v.color.r, v.color.g, _Flutter) * _SwayScale;
            v.vertex.xyz += mul((float3x3)unity_WorldToObject, off);
        }

        half4 LightingFoliage(SurfaceOutput s, half3 lightDir, half3 viewDir, half atten)
        {
            half ndl = dot(s.Normal, lightDir);
            half wrap = saturate(ndl * 0.6 + 0.4);
            half back = pow(saturate(dot(viewDir, -lightDir)), 4) * _Translucency;
            half3 c = s.Albedo * _LightColor0.rgb * (wrap * atten + back * (0.35 + 0.65 * atten));
            return half4(c, 1);
        }

        void surf(Input IN, inout SurfaceOutput o)
        {
            fixed4 tex = tex2D(_MainTex, IN.uv_MainTex);
            clip(tex.a - _Cutoff);
        #ifndef UNITY_PASS_SHADOWCASTER
            float3 origin = StonyObjectOrigin();
            float dist = distance(_WorldSpaceCameraPos.xz, origin.xz);
            float fade = 1 - saturate((dist - _LodFade.x) / max(_LodFade.y - _LodFade.x, 0.01));
            float2 pix = IN.screenPos.xy / max(IN.screenPos.w, 1e-4) * _ScreenParams.xy;
            clip(fade - StonyDither(pix));
            float h = StonyHash(origin.xz);
            o.Albedo = tex.rgb * _Color.rgb * lerp(_TintA.rgb, _TintB.rgb, h) * (0.45 + 0.55 * IN.color.b);
        #else
            o.Albedo = tex.rgb;
        #endif
            o.Alpha = 1;
        }
        ENDCG
    }
    Fallback "Legacy Shaders/Transparent/Cutout/VertexLit"
}
