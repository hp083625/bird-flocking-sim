// NeighborForcesJob.cs — Slice 4 (M3-1) Burst-compiled IJobParallelFor that produces
// the neighbour-driven steering acceleration (separation + alignment + cohesion, with
// a binary in-flock vs out-of-flock weight branch) for every bird, using the cell-list
// SpatialIndexReadOnly to enumerate candidates in O(avg_cell_occupancy) per bird.
//
// Replaces the per-bird main-thread loop NaiveSteering.ComputeAccelerations was running
// in Slice 3. Math is identical so visuals stay unchanged frame-for-frame.

using Bird_behiviour.Flocking.Core;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Bird_behiviour.Flocking.Behaviors
{
    /// <summary>
    /// <see cref="IJobParallelFor"/> that, for each bird, walks the 27-cell
    /// <see cref="SpatialIndexReadOnly"/> neighbourhood, applies the perception cone test
    /// (with zero-velocity fallback to 360°), branches on
    /// <c>flockIds[self] == flockIds[neighbor]</c> for in-flock vs out-of-flock weights,
    /// and writes the summed <c>(separation + alignment + cohesion)</c> acceleration to
    /// <see cref="AccelNeighbor"/>.
    /// </summary>
    /// <remarks>
    /// All weights come from <b>self's</b> <see cref="FlockKernelSettings"/> entry — the
    /// cross-flock weights are described in FLOCKING_PLAN.md §4 as "applied uniformly to
    /// all OTHER flocks", so only the in/out branch needs the neighbour's flock id.
    /// <para/>
    /// Per-frame allocations: this job allocates nothing; the calling site
    /// (<c>FlockWorld.Tick</c>) provides the output array.
    /// </remarks>
    [BurstCompile]
    internal struct NeighborForcesJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> Positions;
        [ReadOnly] public NativeArray<float3> Velocities;
        [ReadOnly] public NativeArray<byte>   FlockIds;
        [ReadOnly] public NativeArray<FlockKernelSettings> KernelSettings;
        [ReadOnly] public SpatialIndexReadOnly Spatial;

        [WriteOnly] public NativeArray<float3> AccelNeighbor;

        public void Execute(int i)
        {
            byte selfFlockId = FlockIds[i];
            FlockKernelSettings s = KernelSettings[selfFlockId];

            float3 selfPos = Positions[i];
            float3 selfVel = Velocities[i];

            float perceptionSq = s.PerceptionRadius * s.PerceptionRadius;
            float coneCos = s.PerceptionConeCos;

            // In-flock running accumulators.
            float3 sepAccumIn   = float3.zero;
            float3 alignSumIn   = float3.zero;
            float3 cohSumIn     = float3.zero;
            int    countIn      = 0;

            // Out-of-flock running accumulators.
            float3 sepAccumOut  = float3.zero;
            float3 alignSumOut  = float3.zero;
            float3 cohSumOut    = float3.zero;
            int    countOut     = 0;

            // Walk the 27-cell spatial neighbourhood. The enumerator is a Burst-friendly
            // value-type struct — no managed allocation, no IEnumerable boxing.
            NeighborEnumerator e = Spatial.GetNeighbors(selfPos);
            while (e.MoveNext())
            {
                int j = e.Current;
                if (j == i)
                {
                    continue;
                }

                float3 nPos = Positions[j];
                float3 toN  = nPos - selfPos;
                float distSq = math.lengthsq(toN);
                if (distSq > perceptionSq || distSq <= 1e-12f)
                {
                    continue;
                }

                if (!ForceKernels.PerceptionConeAccepts(selfPos, selfVel, nPos, coneCos))
                {
                    continue;
                }

                bool sameFlock = FlockIds[j] == selfFlockId;

                // Per-pair separation contribution (kernel returns zero past separationRadius).
                float3 push = ForceKernels.ComputeSeparation(
                    selfPos, nPos, s.SeparationRadius, weight: 1f);

                if (sameFlock)
                {
                    sepAccumIn  += push;
                    alignSumIn  += Velocities[j];
                    cohSumIn    += nPos;
                    countIn++;
                }
                else
                {
                    sepAccumOut += push;
                    alignSumOut += Velocities[j];
                    cohSumOut   += nPos;
                    countOut++;
                }
            }

            // Fold in-flock contributions through the alignment / cohesion kernels (they
            // handle the "0 neighbours → zero" guard internally).
            float3 totalIn =
                sepAccumIn * s.InSeparationWeight +
                ForceKernels.ComputeAlignment(selfVel, alignSumIn, countIn, s.MaxSpeed, s.InAlignmentWeight) +
                ForceKernels.ComputeCohesion (selfPos, cohSumIn,   countIn, s.InCohesionWeight);

            float3 totalOut =
                sepAccumOut * s.OutSeparationWeight +
                ForceKernels.ComputeAlignment(selfVel, alignSumOut, countOut, s.MaxSpeed, s.OutAlignmentWeight) +
                ForceKernels.ComputeCohesion (selfPos, cohSumOut,   countOut, s.OutCohesionWeight);

            AccelNeighbor[i] = totalIn + totalOut;
        }
    }
}
