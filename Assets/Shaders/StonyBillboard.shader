// Distant trees: crossed quads showing an impostor baked from the full tree.
// Thins the forest with distance and enlarges the survivors so canopy cover stays even.
Shader "Stony/TreeBillboard"
{
    Properties
    {
        _MainTex ("Impostor (RGBA)", 2D) = "white" {}
        _Cutoff ("Alpha cutoff", Range(0, 1)) = 0.4
        _TintA ("Instance tint A", Color) = (0.92, 1.0, 0.86, 1)
        _TintB ("Instance tint B", Color) = (1.08, 1.02, 0.86, 1)
        _LodFade ("Fade in from, to (m)", Vector) = (70, 85, 0, 0)
        _Thin ("Thin from, to (m), keep", Vector) = (380, 1100, 0.3, 0)
        _CrownCenter ("Crown centre height (object)", Float) = 9
    }
    SubShader
    {
        Tags { "Queue" = "AlphaTest" "RenderType" = "TransparentCutout" "IgnoreProjector" = "True" }
        Cull Off
        CGPROGRAM
        #pragma surface surf Wrap vertex:vert nolightmap nodynlightmap nodirlightmap nometa noshadow
        #pragma multi_compile_instancing
        #pragma instancing_options assumeuniformscaling
        #pragma target 3.5
        #include "StonyCommon.cginc"

        sampler2D _MainTex;
        fixed _Cutoff;
        fixed4 _TintA, _TintB;
        float4 _LodFade, _Thin;
        float _CrownCenter;

        struct Input { float2 uv_MainTex; float4 screenPos; float fade; float tint; };

        void vert(inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            float3 origin = StonyObjectOrigin();
            float dist = distance(_WorldSpaceCameraPos.xz, origin.xz);
            float h = StonyHash(origin.xz);
            float keep = lerp(1, _Thin.z, smoothstep(_Thin.x, _Thin.y, dist));
            float survive = step(h, keep);
            float grow = 1 / sqrt(keep);
            float3 local = v.vertex.xyz;
            v.normal = normalize(local - float3(0, _CrownCenter, 0) + float3(0, 2, 0));
            v.vertex.xyz = local * grow * survive;
            o.fade = saturate((dist - _LodFade.x) / max(_LodFade.y - _LodFade.x, 0.01));
            o.tint = h;
        }

        half4 LightingWrap(SurfaceOutput s, half3 lightDir, half atten)
        {
            half wrap = saturate(dot(s.Normal, lightDir) * 0.5 + 0.5);
            return half4(s.Albedo * _LightColor0.rgb * wrap * atten, 1);
        }

        void surf(Input IN, inout SurfaceOutput o)
        {
            fixed4 tex = tex2D(_MainTex, IN.uv_MainTex);
            clip(tex.a - _Cutoff);
            float2 pix = IN.screenPos.xy / max(IN.screenPos.w, 1e-4) * _ScreenParams.xy;
            clip(IN.fade - StonyDither(pix));
            o.Albedo = tex.rgb * lerp(_TintA.rgb, _TintB.rgb, IN.tint);
            o.Alpha = 1;
        }
        ENDCG
    }
}
