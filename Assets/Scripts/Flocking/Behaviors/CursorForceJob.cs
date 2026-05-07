// CursorForceJob.cs — Slice 4 (M3-3) intentional NO-OP STUB.
//
// Slice 4 wires the cursor branch into the per-frame job graph but leaves the math
// blank (writes zero into AccelCursor). Slice 7/8 will replace Execute with a real
// dispatch to ForceKernels.ComputeCursorForce — by which time the kernel itself
// is already written + unit-tested here.
//
// Why ship the stub now? The FLOCKING_PLAN.md §2 graph shows three middle jobs
// (Neighbor / Bounds / Cursor) running in parallel; getting the dependency wiring
// + intermediate buffer for the third branch in place lets Slice 7 land a small
// PR (kernel call only, no graph reshuffling).

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Bird_behiviour.Flocking.Behaviors
{
    /// <summary>
    /// <see cref="IJobParallelFor"/> that writes <see cref="float3.zero"/> into
    /// <see cref="AccelCursor"/> for every bird. No-op stub for Slice 4 — the real
    /// implementation (calling <c>ForceKernels.ComputeCursorForce</c> with the world
    /// cursor point + per-flock strength/radius) lands in Slice 7/8.
    /// </summary>
    [BurstCompile]
    internal struct CursorForceJob : IJobParallelFor
    {
        [WriteOnly] public NativeArray<float3> AccelCursor;

        public void Execute(int i)
        {
            AccelCursor[i] = float3.zero;
        }
    }
}
