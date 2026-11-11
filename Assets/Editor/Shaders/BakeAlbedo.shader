// Editor-only: renders a tree's albedo (with its baked occlusion) for impostor textures.
Shader "Hidden/Stony/BakeAlbedo"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Cutoff ("Cutoff", Float) = 0.42
        _Color ("Colour", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex; float _Cutoff; float4 _Color;
            struct a2v { float4 vertex : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; float3 normal : NORMAL; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; float3 n : TEXCOORD1; };
            v2f vert(a2v v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.uv; o.color = v.color; o.n = UnityObjectToWorldNormal(v.normal); return o; }
            float4 frag(v2f i) : SV_Target
            {
                float4 t = tex2D(_MainTex, i.uv);
                clip(t.a - _Cutoff);
                // gentle top light so the impostor keeps some crown shape
                float shade = 0.82 + 0.25 * saturate(i.n.y * 0.5 + 0.5);
                return float4(t.rgb * _Color.rgb * (0.45 + 0.55 * i.color.b) * shade, 1);
            }
            ENDCG
        }
    }
}
