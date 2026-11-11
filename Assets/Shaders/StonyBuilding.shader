// Simple buildings from map footprints: vertex-coloured walls and roofs with procedural siding,
// shingle courses and rows of windows.
Shader "Stony/Building"
{
    Properties
    {
        _Variation ("Variation", 2D) = "gray" {}
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        CGPROGRAM
        #pragma surface surf Lambert addshadow nolightmap nodynlightmap nodirlightmap nometa
        #pragma target 3.5
        sampler2D _Variation;

        // uv: x along the surface in metres, y up the wall (from 0.6 m below ground) or up the roof slope.
        // colour alpha: 0 = wall with windows, 0.5 = plain wall, 1 = roof.
        struct Input { float2 uv_Variation; float4 color : COLOR; float3 worldPos; };

        void surf(Input IN, inout SurfaceOutput o)
        {
            float roof = step(0.75, IN.color.a);
            float windowed = step(IN.color.a, 0.25);
            float2 uv = IN.uv_Variation;
            float rowSize = lerp(0.2, 0.32, roof);                      // siding boards / shingle courses
            float row = frac(uv.y / rowSize);
            float course = smoothstep(0.0, 0.12, row) * 0.14 + 0.86;    // shadow line under each course
            float n = tex2D(_Variation, IN.worldPos.xz / 9.0 + IN.worldPos.y / 7.0).r;
            float n2 = tex2D(_Variation, uv / 1.7).r;
            fixed3 col = IN.color.rgb * course * (0.86 + 0.2 * n) * (0.94 + 0.12 * n2);

            // windows: one every 3.2 m, 1.0 to 2.3 m above each 3 m floor, with a pale frame
            float wx = frac(uv.x / 3.2) * 3.2;
            float wy = fmod(max(uv.y - 0.6, 0), 3.0);
            float inX = step(1.05, wx) * step(wx, 2.15), inY = step(1.0, wy) * step(wy, 2.3);
            float frameX = step(0.97, wx) * step(wx, 2.23), frameY = step(0.92, wy) * step(wy, 2.38);
            float glass = inX * inY * windowed, frame = frameX * frameY * windowed;
            float mullion = step(abs(wx - 1.6), 0.03) + step(abs(wy - 1.65), 0.03);
            fixed3 glassCol = lerp(fixed3(0.1, 0.14, 0.18), fixed3(0.32, 0.4, 0.46), saturate(n * 1.4 - 0.3));
            col = lerp(col, fixed3(0.86, 0.85, 0.8), frame);
            col = lerp(col, glassCol, glass * (1 - saturate(mullion)));

            o.Albedo = col;
            o.Alpha = 1;
        }
        ENDCG
    }
    Fallback "Diffuse"
}
