// Route lines, blips and arrows drawn with GL in pixel space, optionally clipped to the minimap circle.
Shader "Stony/MapOverlay"
{
    Properties
    {
        _Clip ("Clip circle (x, y, radius, on)", Vector) = (0, 0, 0, 0)
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
            float4 _Clip;
            struct appdata { float4 vertex : POSITION; float4 color : COLOR; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float4 color : COLOR; float2 pix : TEXCOORD0; };
            v2f vert(appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.color = v.color; o.pix = v.uv; return o; }
            float4 frag(v2f i) : SV_Target
            {
                float4 c = i.color;
                if (_Clip.w > 0.5)
                {
                    float d = distance(i.pix, _Clip.xy);
                    c.a *= 1 - smoothstep(_Clip.z - 1.5, _Clip.z, d);
                }
                return c;
            }
            ENDCG
        }
    }
}
