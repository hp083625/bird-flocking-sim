// FlockSettingsGpu.cs — CPU mirror of the GPU `FlockKernelSettings` struct laid
// out in FlockSteering.compute. One entry per flock (max 256 supported by the
// uint flockId encoding). The simulation builds a NativeArray<FlockSettingsGpu>
// at init and uploads it once; per-tick changes only happen on the (rare)
// "apply settings" inspector action.
//
// LAYOUT — 96 bytes, 16-byte aligned. Apple GPU prefers 16B reads.
//
// HLSL counterpart (FlockSteering.compute, P3):
//
//   struct FlockKernelSettings {
//       float3 color;                     float perceptionRadius;     // 16
//       float separationRadius;           float perceptionConeCos;
//       float minSpeed;                   float maxSpeed;             // 16
//       float maxAcceleration;
//       float inSepW;        float inAliW;        float inCohW;       // 16
//       float outSepW;       float outAliW;       float outCohW;
//       float cursorReactionStrength;                                 // 16
//       float cursorReactionRadius;
//       float3 preferredCenter;           float preferredAttractW;    // 16
//       float3 preferredExtents;          float _pad;                 // 16
//   };
//
// CHANGE-CONTROL: any field add/remove here MUST land in FlockSteering.compute
// in the same commit and the Stride const below must match.

using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace Bird_behiviour.Flocking.Compute
{
    /// <summary>
    /// CPU mirror of one flock's GPU steering parameters. 96 bytes, 16-byte
    /// aligned. The GPU StructuredBuffer is indexed by <see cref="BoidGpu.FlockId"/>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FlockSettingsGpu
    {
        // ── Visual ──────────────────────────────────────────────────────────────
        public float3 Color;             // base RGB tint applied per-instance in the vertex shader
        public float  PerceptionRadius;

        // ── Perception ──────────────────────────────────────────────────────────
        public float SeparationRadius;
        public float PerceptionConeCos; // pre-computed cos(coneHalfAngleRadians)
        public float MinSpeed;
        public float MaxSpeed;

        // ── Motion ──────────────────────────────────────────────────────────────
        public float MaxAcceleration;
        public float InSeparationWeight;
        public float InAlignmentWeight;
        public float InCohesionWeight;

        public float OutSeparationWeight;
        public float OutAlignmentWeight;
        public float OutCohesionWeight;
        public float CursorReactionStrength;

        public float  CursorReactionRadius;
        public float3 PreferredCenter;

        public float  PreferredAttractionWeight;
        public float3 PreferredExtents;

        // Stride = 96 bytes. 24 floats × 4 bytes. Each 16-byte line of an Apple GPU
        // read pulls 4 floats so this lays out as 6 contiguous lines per entry. Do
        // NOT add fields without bumping the matching FlockKernelSettings struct in
        // FlockSteering.compute and updating the stride below — GraphicsBuffer.SetData
        // requires sizeof(T) == buffer stride exactly.
        public const int Stride = 96;
    }
}
