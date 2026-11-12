// Buildings from map footprints: vertex-coloured walls and roofs with procedural siding, brick, shingle
// courses, concrete and rows of windows. Vertex colour alpha says what a surface is:
//   0.00 siding with windows   0.15 brick with windows   0.25 plain brick   0.35 timber lodge siding with windows
//   0.50 plain painted/timber  0.60 concrete             0.70 smooth white wall with ribbon windows   1.00 roof
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
        struct Input { float2 uv_Variation; float4 color : COLOR; float3 worldPos; };

        void surf(Input IN, inout SurfaceOutput o)
        {
            float a = IN.color.a;
            float roof = step(0.75, a);
            float brick = step(0.1, a) * step(a, 0.3);
            float lodge = step(0.3, a) * step(a, 0.4);
            float concrete = step(0.55, a) * step(a, 0.65);
            float smoothW = step(0.65, a) * step(a, 0.75);
            float windowed = saturate(step(a, 0.19) + lodge + smoothW);
            float2 uv = IN.uv_Variation;

            // fine patterns (siding boards, brick joints, shingles) fade out as they get too small to resolve, so far walls do not shimmer
            float fineDetail = 1 - saturate((fwidth(IN.uv_Variation.y) - 0.012) / 0.04);
            float rowSize = lerp(0.2, 0.32, roof);                      // siding boards / shingle courses
            float row = frac(uv.y / rowSize);
            float course = lerp(lerp(1.0, smoothstep(0.0, 0.12, row) * 0.14 + 0.86, fineDetail), 1.0, smoothW);    // shadow line under each course
            float n = tex2D(_Variation, IN.worldPos.xz / 9.0 + IN.worldPos.y / 7.0).r;
            float n2 = tex2D(_Variation, uv / 1.7).r;
            fixed3 col = IN.color.rgb * (0.86 + 0.2 * n) * (0.94 + 0.12 * n2);

            // brick: courses 7.5 cm high, joints every 23 cm, offset on alternate courses
            float br = floor(uv.y / 0.075);
            float bx = frac((uv.x + 0.115 * fmod(br, 2)) / 0.23);
            float mortar = saturate(step(frac(uv.y / 0.075), 0.1) + step(bx, 0.035)) * fineDetail;
            float brickTone = lerp(1.0, 0.85 + 0.3 * tex2D(_Variation, float2(floor((uv.x + 0.115 * fmod(br, 2)) / 0.23), br) * 0.37).r, fineDetail);
            fixed3 brickCol = lerp(IN.color.rgb * brickTone, fixed3(0.66, 0.63, 0.58), mortar);
            col = lerp(col * course, brickCol * (0.9 + 0.12 * n), brick);
            col = lerp(col, IN.color.rgb * (0.9 + 0.14 * n2), concrete);

            // windows every 3.2 m (2.6 m on the lodge), 1.0 to 2.3 m above each floor, with a pale frame
            // the modern office gets wide ribbon glazing in a single row; other buildings repeat every 3 m of height
            float pitch = lerp(lerp(3.2, 2.6, lodge), 3.6, smoothW);
            float wx = frac(uv.x / pitch) * pitch;
            float wy = fmod(max(uv.y - 0.6, 0), lerp(3.0, 100.0, saturate(smoothW + lodge)));   // single storey for the lodge and the office
            float c0 = pitch * 0.5;
            float hw = lerp(0.55, 1.45, smoothW), y0 = lerp(1.0, 0.85, smoothW), y1 = lerp(2.3, 3.4, smoothW);
            float inX = step(c0 - hw, wx) * step(wx, c0 + hw), inY = step(y0, wy) * step(wy, y1);
            float frameX = step(c0 - hw - 0.08, wx) * step(wx, c0 + hw + 0.08), frameY = step(y0 - 0.08, wy) * step(wy, y1 + 0.08);
            float glass = inX * inY * windowed, frame = frameX * frameY * windowed;
            float mullion = step(abs(wx - c0), 0.03) + step(abs(wy - 1.65), 0.03);
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
