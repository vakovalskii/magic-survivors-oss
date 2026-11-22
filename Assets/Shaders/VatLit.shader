Shader "Magic/VatLit"
{
    Properties
    {
        _MainTex ("Atlas", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _VatTex ("VAT positions", 2D) = "black" {}
        _VatFrames ("Frames", Float) = 107
        _VatFps ("Fps", Float) = 30
        _VatVerts ("Verts", Float) = 743
        _VatStateOffset ("State offset", Float) = 0
        _VatStateFrames ("State frames", Float) = 32
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            Cull Back
            ZWrite On
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            TEXTURE2D(_VatTex); SAMPLER(sampler_VatTex);
            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _Color;
                float _VatFrames;
                float _VatFps;
                float _VatVerts;
            CBUFFER_END
            float _VatStateOffset;
            float _VatStateFrames;
            float _VatStateStart;
            float _VatSpeed;
            float _VatLoop;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float3 normalOS : NORMAL;
                uint vid : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
            };

            float3 SampleVat(uint vid, float frame)
            {
                float f = clamp(frame, _VatStateOffset, _VatStateOffset + _VatStateFrames - 1);
                float row = (f + 0.5) / _VatFrames;
                float col = ((float)vid + 0.5) / _VatVerts;
                return SAMPLE_TEXTURE2D_LOD(_VatTex, sampler_VatTex, float2(col, row), 0).xyz;
            }

            Varyings vert (Attributes v)
            {
                Varyings o;
                float elapsed = max(0, _Time.y - _VatStateStart) * _VatFps * max(0.05, _VatSpeed);
                float frame = _VatLoop > 0.5
                    ? _VatStateOffset + fmod(elapsed, _VatStateFrames)
                    : _VatStateOffset + min(_VatStateFrames - 1, elapsed);
                float f0 = floor(frame);
                float3 p0 = SampleVat(v.vid, f0);
                float3 p1 = SampleVat(v.vid, f0 + 1);
                float3 posOS = lerp(p0, p1, frac(frame));
                o.positionWS = TransformObjectToWorld(posOS);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            half4 frag (Varyings i) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
                Light mainLight = GetMainLight();
                float ndl = saturate(dot(normalize(i.normalWS), mainLight.direction)) * 0.7 + 0.3;
                half3 col = tex.rgb * _Color.rgb * mainLight.color * ndl;
                return half4(col, 1);
            }
            ENDHLSL
        }
    }
    FallBack "Universal Render Pipeline/Lit"
}
