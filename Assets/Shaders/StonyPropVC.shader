// Small props (tables, benches, cars...): vertex colours baked from the source models, lit like the trees.
// Instanced, so hundreds of the same prop cost a few draw calls.
Shader "Stony/PropVC"
{
    Properties { _Tint ("Tint", Color) = (1, 1, 1, 1) }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        CGPROGRAM
        #pragma surface surf Lambert addshadow nolightmap nodynlightmap nodirlightmap nometa
        #pragma multi_compile_instancing
        #pragma target 3.5
        fixed4 _Tint;
        struct Input { float4 color : COLOR; };
        void surf(Input IN, inout SurfaceOutput o)
        {
            // colours were baked as sRGB bytes
            o.Albedo = GammaToLinearSpace(IN.color.rgb) * _Tint.rgb;
            o.Alpha = 1;
        }
        ENDCG
    }
    Fallback "Diffuse"
}
