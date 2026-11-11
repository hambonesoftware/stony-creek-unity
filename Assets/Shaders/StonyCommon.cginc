#ifndef STONY_COMMON_INCLUDED
#define STONY_COMMON_INCLUDED

float _StonyWind;          // global wind strength (set by StonyExplorer), 0 when baking impostors
float4 _StonyWindDir;      // xz direction

float StonyHash(float2 p)
{
    return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
}

// Object origin in world space (works per instance with GPU instancing).
float3 StonyObjectOrigin()
{
    return float3(unity_ObjectToWorld._m03, unity_ObjectToWorld._m13, unity_ObjectToWorld._m23);
}

// Sway of a whole plant plus a quick flutter. flex: 0 at the base, 1 at the tips.
float3 StonyWindOffset(float3 worldPos, float3 origin, float flex, float phase, float flutterAmount)
{
    float t = _Time.y;
    float gust = 0.6 + 0.4 * sin(t * 0.37 + origin.x * 0.011 + origin.z * 0.007);
    float sway = sin(t * 1.15 + origin.x * 0.05 + origin.z * 0.043) * 0.5 + sin(t * 2.1 + origin.z * 0.09) * 0.2;
    float3 dir = float3(_StonyWindDir.x, 0, _StonyWindDir.z);
    float3 offset = dir * (sway * gust * flex * flex);
    float flutter = sin(t * 7.3 + phase * 6.283 + worldPos.y * 1.7) * flutterAmount * flex;
    offset += float3(flutter, flutter * 0.5, -flutter * 0.7);
    return offset * _StonyWind;
}

// Ordered dither for screen-door fades (LOD transitions without transparency sorting).
float StonyDither(float2 screenPixel)
{
    const float4x4 m = float4x4(0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5);
    uint2 p = uint2(fmod(screenPixel, 4));
    return (m[p.x][p.y] + 0.5) / 16.0;
}

#endif
