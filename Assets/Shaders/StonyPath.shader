// Trail and road ribbons draped on the terrain, with ragged soft edges.
Shader "Stony/Path"
{
    Properties
    {
        _MainTex ("Surface", 2D) = "white" {}
        _BumpMap ("Normal", 2D) = "bump" {}
        _Color ("Colour", Color) = (1, 1, 1, 1)
        _Edge ("Edge softness", Range(0.01, 0.5)) = 0.22
        _Variation ("Variation", 2D) = "gray" {}
    }
    SubShader
    {
        Tags { "Queue" = "Geometry+1" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Offset -2, -2
        ZWrite Off
        CGPROGRAM
        #pragma surface surf Standard alpha:fade nolightmap nodynlightmap nodirlightmap nometa
        #pragma target 3.5
        #include "StonyCommon.cginc"
        sampler2D _MainTex, _BumpMap, _Variation;
        fixed4 _Color;
        fixed _Edge;

        // uv_MainTex: x across the ribbon 0..1, y along it in metres / tile.
        struct Input { float2 uv_MainTex; float3 worldPos; float4 screenPos; };

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            float across = abs(IN.uv_MainTex.x - 0.5) * 2;
            float ragged = tex2D(_Variation, IN.worldPos.xz / 3.7).r;
            float edge = 1 - smoothstep(1 - _Edge * (0.6 + ragged), 1, across);
            float2 uv = IN.worldPos.xz / 3.0;
            o.Albedo = tex2D(_MainTex, uv).rgb * _Color.rgb * (0.88 + 0.24 * tex2D(_Variation, IN.worldPos.xz / 41.0).r);
            o.Normal = UnpackNormal(tex2D(_BumpMap, uv));
            o.Smoothness = 0.08;
            o.Alpha = edge * 0.96;
        }
        ENDCG
    }
}
