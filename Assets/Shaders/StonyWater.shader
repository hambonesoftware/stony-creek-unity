// Lakes: deep colour, sky reflection by Fresnel, rippling sun glints.
Shader "Stony/Water"
{
    Properties
    {
        _BumpMap ("Ripples", 2D) = "bump" {}
        _Deep ("Deep colour", Color) = (0.05, 0.13, 0.15, 1)
        _Shallow ("Body colour", Color) = (0.12, 0.24, 0.24, 1)
        _Sky ("Sky reflection", Color) = (0.55, 0.7, 0.82, 1)
        _Horizon ("Horizon reflection", Color) = (0.78, 0.84, 0.86, 1)
        _Glint ("Sun glint", Float) = 3
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry+10" }
        CGPROGRAM
        #pragma surface surf WaterLight nolightmap nodynlightmap nodirlightmap nometa noshadow
        #pragma target 3.5
        sampler2D _BumpMap;
        fixed4 _Deep, _Shallow, _Sky, _Horizon;
        float _Glint;

        struct Input { float3 worldPos; };

        half4 LightingWaterLight(SurfaceOutput s, half3 lightDir, half3 viewDir, half atten)
        {
            half3 h = normalize(lightDir + viewDir);
            half spec = pow(saturate(dot(s.Normal, h)), 420) * _Glint + pow(saturate(dot(s.Normal, h)), 60) * 0.15;
            half3 c = s.Albedo * _LightColor0.rgb * saturate(dot(s.Normal, lightDir)) * 0.4 + _LightColor0.rgb * spec * atten;
            return half4(c, 1);
        }

        void surf(Input IN, inout SurfaceOutput o)
        {
            float2 p = IN.worldPos.xz;
            float t = _Time.y;
            fixed3 n1 = UnpackNormal(tex2D(_BumpMap, p / 9.0 + float2(t * 0.012, t * 0.007)));
            fixed3 n2 = UnpackNormal(tex2D(_BumpMap, p / 3.1 - float2(t * 0.019, -t * 0.011)));
            fixed3 n = normalize(float3(n1.xy + n2.xy * 0.6, 1.8));
            float3 v = normalize(_WorldSpaceCameraPos - IN.worldPos);
            float fresnel = pow(1 - saturate(v.y + n.x * 0.08), 4);
            fixed3 sky = lerp(_Sky.rgb, _Horizon.rgb, fresnel);
            o.Albedo = _Shallow.rgb;
            o.Emission = lerp(_Deep.rgb, sky, 0.12 + 0.8 * fresnel);
            o.Normal = n;
            o.Alpha = 1;
        }
        ENDCG
    }
}
