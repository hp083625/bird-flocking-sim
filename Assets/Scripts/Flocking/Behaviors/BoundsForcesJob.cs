// BoundsForcesJob.cs — Slice 4 (M3-2) Burst-compiled IJobParallelFor that produces the
// bounds-driven steering acceleration for every bird (world hard bounds + per-flock
// soft preferred zone). No spatial index required; each bird is independent.

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Bird_behiviour.Flocking.Behaviors
{
    /// <summary>
    /// <see cref="IJobParallelFor"/> that, for each bird, sums the world hard-bounds
    /// inward push (from <c>FlockWorld</c>'s AABB) and the per-flock soft preferred-zone
    /// pull (from the bird's <see cref="FlockKernelSettings"/>) and writes the result to
    /// <see cref="AccelBounds"/>.
    /// </summary>
    /// <remarks>
    /// Independent of the spatial grid — runs in parallel with
    /// <c>NeighborForcesJob</c> + <c>CursorForceJob</c> per the FLOCKING_PLAN.md §2 graph.
    /// </remarks>
    [BurstCompile]
    internal struct BoundsForcesJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> Positions;
        [ReadOnly] public NativeArray<byte>   FlockIds;
        [ReadOnly] public NativeArray<FlockKernelSettings> KernelSettings;

        public float3 WorldBoundsCenter;
        public float3 WorldBoundsExtents;
        public float  WorldBoundsWeight;
        /// <summary>Per-axis inward inset for the hard wall (e.g. 5% of min extent).</summary>
        public float  WorldBoundsMargin;

        [WriteOnly] public NativeArray<float3> AccelBounds;

        public void Execute(int i)
        {
            float3 selfPos = Positions[i];
            FlockKernelSettings s = KernelSettings[FlockIds[i]];

            float3 hard = ForceKernels.ComputeBoundsForceWorldHard(
                selfPos, WorldBoundsCenter, WorldBoundsExtents,
                WorldBoundsWeight, WorldBoundsMargin);

            float3 soft = ForceKernels.ComputeBoundsForcePreferred(
                selfPos, s.PreferredCenter, s.PreferredExtents, s.PreferredAttractionWeight);

            AccelBounds[i] = hard + soft;
        }
    }
}
