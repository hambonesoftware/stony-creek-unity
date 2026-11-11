// Four-layer terrain: photo textures up close with anti-tiling, macro colour (from NLCD land cover) far away.
Shader "Stony/Terrain"
{
    Properties
    {
        [HideInInspector] _Control ("Control (RGBA)", 2D) = "red" {}
        [HideInInspector] _Splat0 ("Layer 0", 2D) = "white" {}
        [HideInInspector] _Splat1 ("Layer 1", 2D) = "white" {}
        [HideInInspector] _Splat2 ("Layer 2", 2D) = "white" {}
        [HideInInspector] _Splat3 ("Layer 3", 2D) = "white" {}
        [HideInInspector] _Normal0 ("Normal 0", 2D) = "bump" {}
        [HideInInspector] _Normal1 ("Normal 1", 2D) = "bump" {}
        [HideInInspector] _Normal2 ("Normal 2", 2D) = "bump" {}
        [HideInInspector] _Normal3 ("Normal 3", 2D) = "bump" {}
        _Macro ("Macro colour", 2D) = "gray" {}
        _Variation ("Variation", 2D) = "gray" {}
        _WorldSize ("World size (x, z)", Vector) = (6540, 9224, 0, 0)
        _MacroRange ("Macro blend start, end", Vector) = (90, 650, 0, 0)
        _Tint0 ("Lawn tint", Color) = (0.8, 1.02, 0.6, 1)
        _Tint1 ("Meadow tint", Color) = (0.96, 1.0, 0.8, 1)
        _Tint2 ("Forest tint", Color) = (1, 1, 1, 1)
        _Tint3 ("Sand tint", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
        Tags { "Queue" = "Geometry-100" "RenderType" = "Opaque" "TerrainCompatible" = "True" }
        CGPROGRAM
        #pragma surface surf Standard vertex:vert fullforwardshadows nolightmap nodynlightmap nodirlightmap
        #pragma target 3.5
        sampler2D _Control;
        sampler2D _Splat0, _Splat1, _Splat2, _Splat3;
        sampler2D _Normal0, _Normal1, _Normal2, _Normal3;
        float4 _Splat0_ST, _Splat1_ST, _Splat2_ST, _Splat3_ST;
        sampler2D _Macro, _Variation;
        float4 _WorldSize, _MacroRange;
        fixed4 _Tint0, _Tint1, _Tint2, _Tint3;

        struct Input { float2 tc; float3 worldPos; };

        void vert(inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            o.tc = v.texcoord.xy;
            v.tangent.xyz = cross(v.normal, float3(0, 0, 1));
            v.tangent.w = -1;
        }

        // Two rotated, differently scaled samples hide the repeat.
        fixed3 Layer(sampler2D tex, float2 uv, float mixAmount)
        {
            fixed3 a = tex2D(tex, uv).rgb;
            float2 uv2 = float2(uv.x * 0.83 - uv.y * 0.56, uv.x * 0.56 + uv.y * 0.83) * 0.31 + 0.37;
            fixed3 b = tex2D(tex, uv2).rgb;
            return lerp(a, b, mixAmount);
        }

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 w = tex2D(_Control, IN.tc);
            float2 world = IN.worldPos.xz;
            float var = tex2D(_Variation, world / 97.0).r;
            float var2 = tex2D(_Variation, world / 23.0).r;
            float m = saturate(var * 1.4 - 0.2);
            // TerrainLayer tile size arrives as _SplatN_ST over the 0..1 terrain uv.
            float2 uv0 = IN.tc * _Splat0_ST.xy + _Splat0_ST.zw;
            float2 uv1 = IN.tc * _Splat1_ST.xy + _Splat1_ST.zw;
            float2 uv2 = IN.tc * _Splat2_ST.xy + _Splat2_ST.zw;
            float2 uv3 = IN.tc * _Splat3_ST.xy + _Splat3_ST.zw;

            fixed3 col = Layer(_Splat0, uv0, m) * _Tint0.rgb * w.r
                       + Layer(_Splat1, uv1, m) * _Tint1.rgb * w.g
                       + Layer(_Splat2, uv2, m) * _Tint2.rgb * w.b
                       + Layer(_Splat3, uv3, m) * _Tint3.rgb * w.a;
            col *= 0.86 + 0.28 * var2;

            float dist = distance(_WorldSpaceCameraPos, IN.worldPos);
            float far = smoothstep(_MacroRange.x, _MacroRange.y, dist);
            fixed3 macro = tex2D(_Macro, world / _WorldSize.xy).rgb;
            col = lerp(col, macro * (0.9 + 0.2 * var), far);

            float nearN = 1 - smoothstep(25, 80, dist);
            fixed3 n = UnpackNormal(tex2D(_Normal0, uv0)) * w.r + UnpackNormal(tex2D(_Normal1, uv1)) * w.g
                     + UnpackNormal(tex2D(_Normal2, uv2)) * w.b + UnpackNormal(tex2D(_Normal3, uv3)) * w.a;
            o.Normal = normalize(lerp(fixed3(0, 0, 1), n, nearN));
            o.Albedo = col;
            o.Smoothness = 0.05;
            o.Metallic = 0;
        }
        ENDCG
    }
    Fallback "Diffuse"
}
