// Trunks and branches: photo bark with normal map, sways with the crown.
Shader "Stony/Bark"
{
    Properties
    {
        _MainTex ("Bark", 2D) = "white" {}
        _BumpMap ("Normal", 2D) = "bump" {}
        _Color ("Colour", Color) = (1, 1, 1, 1)
        _LodFade ("Fade out from, to (m)", Vector) = (9000, 9001, 0, 0)
        _SwayScale ("Sway scale", Range(0, 2)) = 0.35
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        CGPROGRAM
        #pragma surface surf Lambert vertex:vert addshadow nolightmap nodynlightmap nodirlightmap nometa
        #pragma multi_compile_instancing
        #pragma instancing_options assumeuniformscaling
        #pragma target 3.5
        #include "StonyCommon.cginc"

        sampler2D _MainTex, _BumpMap;
        fixed4 _Color;
        float4 _LodFade;
        fixed _SwayScale;

        struct Input { float2 uv_MainTex; float4 color : COLOR; float4 screenPos; };

        void vert(inout appdata_full v)
        {
            float3 origin = StonyObjectOrigin();
            float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
            float3 off = StonyWindOffset(wp, origin, v.color.r, v.color.g, 0) * _SwayScale;
            v.vertex.xyz += mul((float3x3)unity_WorldToObject, off);
        }

        void surf(Input IN, inout SurfaceOutput o)
        {
        #ifndef UNITY_PASS_SHADOWCASTER
            float3 origin = StonyObjectOrigin();
            float dist = distance(_WorldSpaceCameraPos.xz, origin.xz);
            float fade = 1 - saturate((dist - _LodFade.x) / max(_LodFade.y - _LodFade.x, 0.01));
            float2 pix = IN.screenPos.xy / max(IN.screenPos.w, 1e-4) * _ScreenParams.xy;
            clip(fade - StonyDither(pix));
        #endif
            o.Albedo = tex2D(_MainTex, IN.uv_MainTex).rgb * _Color.rgb * (0.55 + 0.45 * IN.color.b);
            o.Normal = UnpackNormal(tex2D(_BumpMap, IN.uv_MainTex));
            o.Alpha = 1;
        }
        ENDCG
    }
    Fallback "Diffuse"
}
