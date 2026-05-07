// BuildMatricesJob.cs — Slice 8 / M4-1 Burst-compiled per-flock matrix builder.
//
// Iterates the visible-bird indices produced by FrustumCullJob (read via the same
// NativeList<int>'s deferred job array — no Length-read sync point is needed because
// IJobParallelForDefer.Schedule resolves the iteration count when the producer job
// has finished writing). For each visible bird we read its world-space position and
// velocity off FlockWorld's flat per-bird arrays and emit a packed float4x4 into
// VisibleMatrices[k]. The output is index-aligned with VisibleIndices, NOT with bird
// id — InstancedFlockRenderer.Render consumes [0, visibleCount) directly.
//
// Per FLOCKING_PLAN §6 M4-1: rotation is built with quaternion.LookRotationSafe so
// truly-zero velocities (which IntegrateJob nudges to (0,0,MinSpeed)) still produce a
// well-defined heading.

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Bird_behiviour.Flocking.Rendering
{
    /// <summary>
    /// Per-flock matrix builder. Consumes the packed visible-index array produced by
    /// <see cref="FrustumCullJob"/> and writes one <see cref="float4x4"/> per visible
    /// bird into <see cref="VisibleMatrices"/>.
    /// </summary>
    /// <remarks>
    /// <b>Indexing.</b> <c>VisibleMatrices[k]</c> is the matrix for the bird whose
    /// global index is <c>VisibleIndices[k]</c>. The renderer consumes the matrices
    /// array directly in <c>[0, visibleCount)</c> order — there is no per-bird-id
    /// indirection needed downstream.
    /// <para/>
    /// <b>Rotation.</b> <see cref="quaternion.LookRotationSafe"/> handles zero-velocity
    /// gracefully (returns identity, which composed with a non-zero position still
    /// produces a valid TRS matrix). <see cref="Behaviors.IntegrateJob"/> already
    /// floors |vel| to <c>MinSpeed</c>, so the LookRotationSafe fallback is purely
    /// defensive against a future integrator change.
    /// </remarks>
    [BurstCompile]
    public struct BuildMatricesJob : IJobParallelForDefer
    {
        /// <summary>
        /// Visible-bird global indices, sized by the producing <c>FrustumCullJob</c>'s
        /// <c>NativeList&lt;int&gt;.Length</c> at job-start time (deferred).
        /// </summary>
        [ReadOnly] public NativeArray<int> VisibleIndices;

        /// <summary>World-space bird positions (full FlockWorld array, indexed by global bird id).</summary>
        [ReadOnly] public NativeArray<float3> Positions;

        /// <summary>World-space bird velocities (full FlockWorld array, indexed by global bird id).</summary>
        [ReadOnly] public NativeArray<float3> Velocities;

        /// <summary>
        /// Packed output. <c>VisibleMatrices[k]</c> = TRS for <c>VisibleIndices[k]</c>.
        /// Sized to the flock's worst-case visible count (= flock <c>BirdCount</c>) by the caller.
        /// </summary>
        [WriteOnly] public NativeArray<float4x4> VisibleMatrices;

        /// <summary>
        /// <see cref="IJobParallelForDefer.Execute(int)"/> — <paramref name="k"/> is the
        /// packed visible-bird index in <c>[0, visibleCount)</c>.
        /// </summary>
        public void Execute(int k)
        {
            int birdIdx = VisibleIndices[k];
            float3 pos  = Positions[birdIdx];
            float3 vel  = Velocities[birdIdx];

            // LookRotationSafe degenerates to identity when |vel| ≈ 0. IntegrateJob
            // already floors speeds to MinSpeed, but the safe variant costs nothing.
            quaternion rot = quaternion.LookRotationSafe(vel, math.up());
            VisibleMatrices[k] = float4x4.TRS(pos, rot, new float3(1f, 1f, 1f));
        }
    }
}
