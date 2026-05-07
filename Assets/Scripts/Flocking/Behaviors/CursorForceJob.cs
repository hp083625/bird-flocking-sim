// CursorForceJob.cs — Slice 7 (M3-3) real implementation. Replaces the Slice 4 no-op
// stub: per bird, looks up that flock's cursor reaction parameters from the kernel
// settings array and dispatches ForceKernels.ComputeCursorForce. The kernel itself is
// covered by EditMode unit tests in M6-2 (force-kernel zero / radius / smoothstep
// asserts); this job only adds the per-bird flock-id indirection + the array writes.
//
// Falloff choice: smoothstep, inherited from ForceKernels.ComputeCursorForce. The
// Slice 7 spec accepts either linear (1 - d/r) or smoothstep; smoothstep gives a
// gentler near-radius taper which keeps the cursor feel smooth instead of clipping
// when birds graze the edge of the influence sphere. See the kernel's docstring for
// the exact formula.

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Bird_behiviour.Flocking.Behaviors
{
    /// <summary>
    /// <see cref="IJobParallelFor"/> that, for each bird, computes the signed cursor
    /// reaction force (attract / repel / ignore — per the bird's flock settings) by
    /// dispatching <see cref="ForceKernels.ComputeCursorForce"/>, and writes it to
    /// <see cref="AccelCursor"/>.
    /// </summary>
    /// <remarks>
    /// Independent of the spatial grid — runs in parallel with
    /// <c>NeighborForcesJob</c> + <c>BoundsForcesJob</c> per the FLOCKING_PLAN.md §2 graph.
    /// <para/>
    /// <b>cursorOnScreen</b> is carried as a managed-friendly <see cref="bool"/>. Burst
    /// 1.8+ accepts <c>bool</c> as a job field on Apple Silicon; the kernel itself takes
    /// a <c>bool</c> by value so no extra unpack is required.
    /// </remarks>
    [BurstCompile(CompileSynchronously = true)]
    internal struct CursorForceJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> Positions;
        [ReadOnly] public NativeArray<byte>   FlockIds;
        [ReadOnly] public NativeArray<FlockKernelSettings> KernelSettings;

        /// <summary>Cursor world-point published by <c>CursorInputController</c>.</summary>
        public float3 CursorWorldPoint;

        /// <summary>Whether the cursor was successfully projected onto the horizontal plane this tick.</summary>
        public bool CursorOnScreen;

        [WriteOnly] public NativeArray<float3> AccelCursor;

        public void Execute(int i)
        {
            FlockKernelSettings s = KernelSettings[FlockIds[i]];
            AccelCursor[i] = ForceKernels.ComputeCursorForce(
                Positions[i],
                CursorWorldPoint,
                CursorOnScreen,
                s.CursorReactionStrength,
                s.CursorReactionRadius);
        }
    }
}
