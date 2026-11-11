// Ground plants near the walker: grass clumps and leafy understory, waving and shrinking away with distance.
Shader "Stony/Grass"
{
    Properties
    {
        _MainTex ("Blades (RGBA)", 2D) = "white" {}
        _Cutoff ("Alpha cutoff", Range(0, 1)) = 0.45
        _Color ("Colour", Color) = (1, 1, 1, 1)
        _DryColor ("Dry colour", Color) = (1.15, 1.0, 0.7, 1)
        _FadeRange ("Shrink from, to (m)", Vector) = (32, 44, 0, 0)
        _Wave ("Wave", Range(0, 1)) = 0.25
    }
    SubShader
    {
        Tags { "Queue" = "AlphaTest" "RenderType" = "TransparentCutout" "IgnoreProjector" = "True" }
        Cull Off
        CGPROGRAM
        #pragma surface surf Wrap vertex:vert nolightmap nodynlightmap nodirlightmap nometa noshadow
        #pragma multi_compile_instancing
        #pragma target 3.5
        #include "StonyCommon.cginc"

        sampler2D _MainTex;
        fixed _Cutoff, _Wave;
        fixed4 _Color, _DryColor;
        float4 _FadeRange;

        struct Input { float2 uv_MainTex; float4 color : COLOR; float dry; };

        void vert(inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            float3 origin = StonyObjectOrigin();
            float dist = distance(_WorldSpaceCameraPos, origin);
            float shrink = 1 - smoothstep(_FadeRange.x, _FadeRange.y, dist);
            float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
            float3 off = StonyWindOffset(wp, origin, v.color.r, StonyHash(origin.xz), 0.02) * _Wave;
            v.vertex.xyz = v.vertex.xyz * shrink + mul((float3x3)unity_WorldToObject, off);
            v.normal = float3(0, 1, 0);
            o.dry = StonyHash(origin.zx);
        }

        half4 LightingWrap(SurfaceOutput s, half3 lightDir, half atten)
        {
            half wrap = saturate(dot(s.Normal, lightDir) * 0.5 + 0.5);
            return half4(s.Albedo * _LightColor0.rgb * wrap * (0.4 + 0.6 * atten), 1);
        }

        void surf(Input IN, inout SurfaceOutput o)
        {
            fixed4 tex = tex2D(_MainTex, IN.uv_MainTex);
            clip(tex.a - _Cutoff);
            o.Albedo = tex.rgb * _Color.rgb * lerp(fixed3(1, 1, 1), _DryColor.rgb, IN.dry * IN.dry) * (0.6 + 0.4 * IN.color.b);
            o.Alpha = 1;
        }
        ENDCG
    }
}
