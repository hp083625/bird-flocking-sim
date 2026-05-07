// FrustumCullJob.cs — Slice 8 / M4-2 Burst-compiled per-flock CPU frustum cull.
//
// Iterates one flock's index window [StartIndex, StartIndex + Count) of the world
// position array and writes the global indices of birds whose padded sphere intersects
// the camera frustum into a NativeList<int> via its ParallelWriter. The list's Length
// after the job completes IS the visible-bird count for that flock (no separate counter
// to thread through downstream).
//
// Per FLOCKING_PLAN §6 M4-2 / §2 job graph: this job is fully independent of the
// steering chain and runs in parallel with it; BuildMatricesJob fans in on
// CombineDependencies(cullH, integrateH).

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Bird_behiviour.Flocking.Rendering
{
    /// <summary>
    /// Per-flock CPU frustum cull. For every bird in a flock's slice, performs the
    /// classic 6-plane sphere test (with a small radius pad to prevent edge-popping)
    /// and atomically appends visible bird indices to <see cref="VisibleIndicesWriter"/>.
    /// </summary>
    /// <remarks>
    /// <b>Plane convention.</b> <see cref="CameraFrustumPlanes"/> is exactly 6 entries,
    /// each <c>(xyz = inward-facing normal, w = signed distance)</c> — matching
    /// <see cref="UnityEngine.GeometryUtility.CalculateFrustumPlanes(UnityEngine.Camera, UnityEngine.Plane[])"/>
    /// — so a sphere of radius <see cref="BirdRadius"/> centred at <c>p</c> intersects
    /// the frustum iff <c>dot(plane.xyz, p) + plane.w + BirdRadius &gt;= 0</c> for every
    /// plane. A single failing plane proves the sphere is fully outside.
    /// <para/>
    /// <b>Atomic write.</b> We use <c>NativeList&lt;int&gt;.ParallelWriter.AddNoResize</c>
    /// rather than rolling our own <c>Interlocked.Increment</c> on a shared counter — the
    /// list's capacity is pre-sized to the flock's bird count (worst case all visible) by
    /// the caller, so <c>AddNoResize</c> never needs to grow and is wait-free.
    /// <para/>
    /// <b>Output ordering.</b> Indices appear in arbitrary order (whichever worker thread
    /// happens to win the increment race). <see cref="BuildMatricesJob"/> doesn't care.
    /// </remarks>
    [BurstCompile]
    public struct FrustumCullJob : IJobParallelFor
    {
        /// <summary>World-space bird positions (full FlockWorld array — sliced by <see cref="StartIndex"/>).</summary>
        [ReadOnly] public NativeArray<float3> Positions;

        /// <summary>6 frustum planes (xyz = inward normal, w = signed distance).</summary>
        [ReadOnly] public NativeArray<float4> CameraFrustumPlanes;

        /// <summary>Global index of the first bird in this flock's slice.</summary>
        public int StartIndex;

        /// <summary>Padding radius added to the plane test so birds don't pop in/out at the edge.</summary>
        public float BirdRadius;

        /// <summary>Append-only writer for global bird indices. Pre-sized to the flock's <c>Count</c>.</summary>
        public NativeList<int>.ParallelWriter VisibleIndicesWriter;

        /// <summary>
        /// <see cref="IJobParallelFor.Execute(int)"/> — <paramref name="i"/> is the
        /// flock-local index in <c>[0, slice.Count)</c>; the global bird index is
        /// <see cref="StartIndex"/> + <paramref name="i"/>.
        /// </summary>
        public void Execute(int i)
        {
            int birdIdx = StartIndex + i;
            float3 p = Positions[birdIdx];

            // 6-plane sphere test: a single failing plane (signed distance < -radius)
            // means the sphere is fully outside the frustum on that plane's far side.
            // We branch out as soon as one plane fails to keep the inner loop tight on
            // the off-screen majority once the player flies near the edge.
            for (int k = 0; k < 6; k++)
            {
                float4 plane = CameraFrustumPlanes[k];
                float signedDist = math.dot(plane.xyz, p) + plane.w;
                if (signedDist + BirdRadius < 0f)
                {
                    return;
                }
            }

            // Visible — atomic append. AddNoResize is safe because the caller sized
            // the list's capacity to slice.Count (worst case all visible).
            VisibleIndicesWriter.AddNoResize(birdIdx);
        }
    }
}
