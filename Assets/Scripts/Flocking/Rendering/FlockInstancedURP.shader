// FlockInstancedURP.shader — Slice 9 / M4 minimal URP forward-lit shader for
// Graphics.RenderMeshIndirect. Reads per-instance world matrices from a
// StructuredBuffer<float4x4> _Matrices indexed by the SV_InstanceID provided by
// the indirect draw. URP/Lit (the original BirdMaterial shader) does NOT read
// from a custom matrix buffer — it gets unity_ObjectToWorld from the legacy
// instanced-array path or DOTS-instancing buffers, neither of which we want to
// pay for here. This shader is the cheapest path that:
//   1. Picks up the per-instance TRS from our own buffer (no driver overhead
//      for matrix uploads — buffer is updated once per frame via SetData on a
//      slice we already have native).
//   2. Reproduces the BirdMaterial look: a single _BaseColor solid fill plus a
//      simple Lambert N·L term against the URP main light. Smoothness/specular
//      are dropped — placeholder bird mesh has no UVs and is rendered at large
//      flock counts where each bird is sub-pixel anyway.
//
// Material wiring: IndirectFlockRenderer clones the source BirdMaterial, swaps
// its shader to this one, and copies the _BaseColor over. This way authors keep
// using the URP/Lit material in the inspector for preview / fallback while the
// runtime sim uses the indirect-friendly material under the hood.

Shader "Bird_behiviour/FlockInstancedURP"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.7, 0.8, 1.0, 1.0)
    }

    SubShader
    {
        Tags
        {
            "RenderType"      = "Opaque"
            "RenderPipeline"  = "UniversalPipeline"
            "Queue"           = "Geometry"
            "IgnoreProjector" = "True"
        }
        LOD 100
        Cull Back
        ZWrite On
        ZTest LEqual

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag
            #pragma target   4.5
            // Required so SV_InstanceID is generated and unity_InstanceID is wired
            // by Unity's macros — even though we read the matrix ourselves, keeping
            // the macros lets URP's lighting helpers stay happy on platforms that
            // demand it.
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            // Per-instance world matrices, populated by IndirectFlockRenderer via
            // GraphicsBuffer.SetData(NativeArray<float4x4>, ...). Indexed by
            // SV_InstanceID (= 0..instanceCount-1 from IndirectDrawIndexedArgs).
            StructuredBuffer<float4x4> _Matrices;

            // P3: per-instance flock id buffer, populated by CSBuildMatrices in
            // FlockSteering.compute. Bound by GpuFlockSimulation. Optional —
            // when bound, vertex shader looks up _FlockColors[flockId] for tinting.
            // When NOT bound (legacy CPU path), vertex shader falls back to _BaseColor.
            StructuredBuffer<uint> _InstanceFlockIds;

            // Per-flock color palette, packed RGBA. Up to 8 flocks for v1; extend
            // by bumping this constant + the matching SetVectorArray on the C# side.
            float4 _FlockColors[8];

            // Whether the per-flock palette is active (1) or fall back to _BaseColor (0).
            // Pushed once per GpuFlockSimulation init via Material.SetFloat.
            float _UsePerFlockColor;

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                uint   instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 normalWS    : TEXCOORD0;
                float4 tint        : TEXCOORD1;
            };

            Varyings Vert (Attributes IN)
            {
                Varyings OUT;
                float4x4 m = _Matrices[IN.instanceID];

                float4 positionWS = mul(m, float4(IN.positionOS.xyz, 1.0));

                // Inverse-transpose for normals: assume uniform scale (TRS with
                // scale=(1,1,1) — IntegrateJob doesn't write per-bird scale) so
                // we can reuse the upper-3x3 of m directly.
                float3 normalWS = normalize(mul((float3x3)m, IN.normalOS));

                OUT.positionHCS = TransformWorldToHClip(positionWS.xyz);
                OUT.normalWS    = normalWS;

                if (_UsePerFlockColor > 0.5)
                {
                    uint flockId = _InstanceFlockIds[IN.instanceID];
                    OUT.tint = _FlockColors[flockId & 7u];
                }
                else
                {
                    OUT.tint = _BaseColor;
                }
                return OUT;
            }

            half4 Frag (Varyings IN) : SV_Target
            {
                Light mainLight = GetMainLight();
                float ndotl = saturate(dot(IN.normalWS, mainLight.direction));
                half3 ambient = SampleSH(IN.normalWS);
                half3 lit = IN.tint.rgb * (ambient + mainLight.color.rgb * ndotl);
                return half4(lit, IN.tint.a);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex   ShadowVert
            #pragma fragment ShadowFrag
            #pragma target   4.5
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            // URP's ShadowCasterPass.hlsl declares these as cbuffer globals at file
            // scope. We don't include the pass file (we have our own SV_InstanceID
            // vertex), so re-declare both here to satisfy the linker.
            float3 _LightDirection;
            float3 _LightPosition;

            StructuredBuffer<float4x4> _Matrices;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                uint   instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
            };

            Varyings ShadowVert (Attributes IN)
            {
                Varyings OUT;
                float4x4 m = _Matrices[IN.instanceID];
                float3 positionWS = mul(m, float4(IN.positionOS.xyz, 1.0)).xyz;
                float3 normalWS = normalize(mul((float3x3)m, IN.normalOS));
                // Apply shadow bias the same way URP's shipped ShadowCasterPass does
                // (light-direction-aware position offset). Reusing the helper keeps
                // self-shadow acne in line with the rest of the scene.
                float3 lightDirWS = _LightDirection;
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirWS));
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                OUT.positionHCS = positionCS;
                return OUT;
            }

            half4 ShadowFrag (Varyings IN) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
