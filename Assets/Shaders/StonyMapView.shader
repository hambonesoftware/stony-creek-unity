// Draws the park map into a GUI rect: rotated circular minimap with compass ring, or the flat full map.
Shader "Stony/MapView"
{
    Properties
    {
        _MainTex ("Map", 2D) = "white" {}
        _Center ("Centre (metres x, z)", Vector) = (0, 0, 0, 0)
        _World ("World size (x, z)", Vector) = (6540, 9224, 0, 0)
        _Bearing ("Up bearing (radians)", Float) = 0
        _Span ("Metres per half height", Float) = 200
        _Aspect ("Rect width / height", Float) = 1
        _Circle ("Circular", Float) = 1
        _Land ("Outside colour", Color) = (0.42, 0.5, 0.36, 1)
        _Ring ("Ring colour", Color) = (0.05, 0.07, 0.06, 0.82)
    }
    SubShader
    {
        Tags { "Queue" = "Overlay" "RenderType" = "Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZTest Always ZWrite Off Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _Center, _World, _Land, _Ring;
            float _Bearing, _Span, _Aspect, _Circle;

            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(appdata_img v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.texcoord; return o; }

            float4 frag(v2f i) : SV_Target
            {
                float2 p = (i.uv - 0.5) * 2;
                p.x *= _Aspect;
                float s, c; sincos(_Bearing, s, c);
                float2 world = (p.x * float2(c, -s) + p.y * float2(s, c)) * _Span; // east, north metres
                float2 m = (_Center.xy + world) / _World.xy;
                float inside = step(0, m.x) * step(m.x, 1) * step(0, m.y) * step(m.y, 1);
                float4 col = lerp(_Land, tex2D(_MainTex, m), inside);
                if (_Circle < 0.5) return float4(col.rgb, 1);

                float r = length(p);
                float aa = fwidth(r) * 1.5;
                const float inner = 0.84;
                float ring = smoothstep(inner - aa, inner, r);
                col.rgb *= 1 - 0.18 * smoothstep(0.45, inner, r);
                // compass ticks every 15 degrees, long ones at the cardinal points
                float ang = _Bearing + atan2(p.x, p.y);
                float step15 = 6.2831853 / 24;
                float k = abs(frac(ang / step15 + 0.5) - 0.5) * step15 * r;
                float cardinal = abs(frac(ang / (6.2831853 / 4) + 0.5) - 0.5) < (0.5 / 6) ? 1 : 0;
                float tickLen = lerp(0.94, 0.9, cardinal);
                float tick = (1 - smoothstep(0.004, 0.011, k)) * smoothstep(tickLen - aa, tickLen, r) * (1 - smoothstep(0.975 - aa, 0.975, r));
                float4 ringCol = _Ring;
                ringCol.rgb = lerp(ringCol.rgb, float3(0.9, 0.92, 0.88), tick * (0.55 + 0.45 * cardinal));
                // thin bright edge at the map boundary
                ringCol.rgb = lerp(ringCol.rgb, float3(0.95, 0.96, 0.92), (1 - smoothstep(0, aa * 1.5, abs(r - inner))) * 0.8);
                float4 o = lerp(col, ringCol, ring);
                o.a *= 1 - smoothstep(1 - aa, 1, r);
                return o;
            }
            ENDCG
        }
    }
}
