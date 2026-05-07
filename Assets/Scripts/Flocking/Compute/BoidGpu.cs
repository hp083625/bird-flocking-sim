// BoidGpu.cs — CPU-side mirror of the GPU `Boid` struct laid out in
// FlockSteering.compute. Used to seed the buffer at init and (optionally) for
// PlayMode tests that read state back via AsyncGPUReadback.
//
// LAYOUT — 48 bytes, 16-byte aligned. Apple GPU prefers 16-byte StructuredBuffer
// reads; un-aligned reads silently halve throughput. Padding fields are
// reserved for forward/up cache and a future per-bird flag word.
//
// HLSL counterpart (FlockSteering.compute):
//
//   struct Boid {
//       float3 pos;        // offset 0
//       uint   flockId;    // offset 12   → completes 16B line
//       float3 vel;        // offset 16
//       float  pad0;       // offset 28   → completes 16B line
//       float4 reserved;   // offset 32   → fwd/up cache or per-bird flags
//   };  // total 48 B
//
// CHANGE-CONTROL: any field add/remove here MUST land in FlockSteering.compute
// in the same commit. Tests will fail loudly if struct sizes diverge.

using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace Bird_behiviour.Flocking.Compute
{
    /// <summary>
    /// CPU-side mirror of the GPU <c>Boid</c> struct. 48 bytes, 16-byte aligned.
    /// </summary>
    /// <remarks>
    /// <para>The GPU is the authority on bird state once <c>GpuFlockSimulation</c>
    /// initialises the buffer. The CPU reads this layout at init only.</para>
    /// <para>Sequential layout is required so <c>GraphicsBuffer.SetData(NativeArray&lt;BoidGpu&gt;)</c>
    /// uploads with no per-element conversion.</para>
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public struct BoidGpu
    {
        /// <summary>World-space position. Updated every Tick by SteeringPass.</summary>
        public float3 Pos;

        /// <summary>0..255. Indexes into the <c>FlockSettingsGpu</c> StructuredBuffer.</summary>
        public uint FlockId;

        /// <summary>World-space velocity. Updated every Tick by SteeringPass.</summary>
        public float3 Vel;

        /// <summary>Padding so <see cref="Vel"/>+<see cref="Pad0"/> fills a 16-byte line. Do not use.</summary>
        public float Pad0;

        /// <summary>Reserved 16 bytes. Phase-5+ stashes cached forward/up here so the matrix-build doesn't re-derive them every frame.</summary>
        public float4 Reserved;

        /// <summary>Stride in bytes — pin into GraphicsBuffer ctor.</summary>
        public const int Stride = 48;
    }
}
