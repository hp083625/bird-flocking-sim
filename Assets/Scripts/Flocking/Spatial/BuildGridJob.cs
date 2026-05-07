// BuildGridJob.cs — three-pass Burst-compiled cell-list build (counting sort) per
// FLOCKING_PLAN.md §6 M2-2. Lives in the Spatial asmdef.
//
// Pass 1 (parallel): per bird → compute cell hash, atomic-increment cellCount[hash].
// Pass 2 (single):   prefix-sum cellCount → cellOffset (length = totalCells + 1).
// Pass 3 (single):   per bird in index order, place into cellBirds at the next free slot
//                    in cellOffset[hash]. Sequential to give a stable within-cell order
//                    so the build is deterministic between runs.

using Bird_behiviour.Flocking.Core;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Bird_behiviour.Flocking.Spatial
{
    /// <summary>
    /// Static helpers + Burst job types for building a <see cref="SpatialHashGrid"/> from
    /// a positions array via the standard three-pass counting-sort algorithm.
    /// </summary>
    /// <remarks>
    /// Schedules three jobs in sequence: <see cref="CountPass"/> (parallel),
    /// <see cref="PrefixSumPass"/> (single), <see cref="ScatterPass"/> (single). Returns a
    /// <see cref="JobHandle"/> that completes when the grid is fully built.
    /// <para/>
    /// <b>Determinism note.</b> Pass 3 is single-threaded so the within-cell ordering
    /// of <see cref="SpatialHashGrid.CellBirds"/> is the bird-index order — guaranteeing
    /// that the same input positions produce the same <c>cellBirds</c> layout on every
    /// run. A parallel scatter with atomic-fetch-add would not satisfy this property.
    /// O(N) work either way; at expected occupancy (~10 birds/cell) the wall-clock
    /// difference is dwarfed by the neighbour-scan cost the grid exists to reduce.
    /// </remarks>
    internal static class BuildGridJob
    {
        /// <summary>
        /// Computes the cell index a position falls into.
        /// </summary>
        /// <param name="position">World-space position.</param>
        /// <param name="boundsMin">Grid AABB minimum corner.</param>
        /// <param name="invCellSize">Reciprocal of the cell edge length (1 / cellSize).</param>
        /// <returns>Per-axis integer cell coordinates (may be out of grid range).</returns>
        public static int3 ComputeCell(float3 position, float3 boundsMin, float invCellSize)
        {
            return (int3)math.floor((position - boundsMin) * invCellSize);
        }

        /// <summary>Flattens a per-axis cell coordinate to a linear cell index (row-major: x then y then z).</summary>
        public static int Flatten(int3 cell, int3 cellsPerAxis)
        {
            return cell.x + cell.y * cellsPerAxis.x + cell.z * cellsPerAxis.x * cellsPerAxis.y;
        }

        /// <summary>True iff <paramref name="cell"/> lies in <c>[0, cellsPerAxis)</c> on every axis.</summary>
        public static bool InBounds(int3 cell, int3 cellsPerAxis)
        {
            return math.all(cell >= 0) && math.all(cell < cellsPerAxis);
        }

        /// <summary>
        /// Schedules the full three-pass build of <paramref name="grid"/> from
        /// <paramref name="positions"/>. The returned handle completes when
        /// <see cref="SpatialHashGrid.CellBirds"/> is fully populated and
        /// <see cref="SpatialHashGrid.CellOffset"/> is ready to query.
        /// </summary>
        /// <param name="grid">Target grid (must be allocated to dimensions matching the world).</param>
        /// <param name="positions">Per-bird position array (length ≥ <paramref name="count"/>).</param>
        /// <param name="count">Number of valid birds in <paramref name="positions"/>.</param>
        /// <param name="deps">Predecessor job handle to chain after.</param>
        /// <returns>JobHandle that completes when the grid is fully built.</returns>
        public static JobHandle Schedule(
            SpatialHashGrid grid,
            NativeArray<float3>.ReadOnly positions,
            int count,
            JobHandle deps)
        {
            float invCellSize = 1f / grid.CellSize;

            // Pass 0: clear the cellCount array (NativeArrayOptions.ClearMemory only clears at
            // allocation time; subsequent frames need an explicit clear).
            JobHandle clearHandle = new ClearIntsJob { Array = grid.CellCount }.Schedule(deps);

            // Pass 1 (parallel): per-bird cell-count.
            JobHandle countHandle = new CountPass
            {
                Positions    = positions,
                Count        = count,
                BoundsMin    = grid.BoundsMin,
                InvCellSize  = invCellSize,
                CellsPerAxis = grid.CellsPerAxis,
                CellCount    = grid.CellCount,
            }.Schedule(count, 64, clearHandle);

            // Pass 2 (single-threaded): prefix sum cellCount → cellOffset.
            JobHandle prefixHandle = new PrefixSumPass
            {
                CellCount  = grid.CellCount,
                CellOffset = grid.CellOffset,
            }.Schedule(countHandle);

            // Pass 3 (single-threaded for stable order): scatter bird indices into cellBirds.
            JobHandle scatterHandle = new ScatterPass
            {
                Positions    = positions,
                Count        = count,
                BoundsMin    = grid.BoundsMin,
                InvCellSize  = invCellSize,
                CellsPerAxis = grid.CellsPerAxis,
                CellOffset   = grid.CellOffset,
                CellBirds    = grid.CellBirds,
            }.Schedule(prefixHandle);

            return scatterHandle;
        }

        // ── Pass 0: clear an int array to zero ──────────────────────────────────────

        [BurstCompile]
        internal struct ClearIntsJob : IJob
        {
            public NativeArray<int> Array;

            public void Execute()
            {
                for (int i = 0; i < Array.Length; i++)
                {
                    Array[i] = 0;
                }
            }
        }

        // ── Pass 1: count birds per cell (parallel, atomic increment) ───────────────

        [BurstCompile]
        internal unsafe struct CountPass : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3>.ReadOnly Positions;
            [ReadOnly] public int   Count;
            [ReadOnly] public float3 BoundsMin;
            [ReadOnly] public float  InvCellSize;
            [ReadOnly] public int3   CellsPerAxis;

            // NativeDisableParallelForRestriction lets every parallel index increment any
            // cell counter; safety is provided by the atomic itself.
            [NativeDisableParallelForRestriction]
            public NativeArray<int> CellCount;

            public void Execute(int birdIndex)
            {
                if (birdIndex >= Count) return;

                int3 cell = ComputeCell(Positions[birdIndex], BoundsMin, InvCellSize);
                if (!InBounds(cell, CellsPerAxis)) return;

                int hash = Flatten(cell, CellsPerAxis);
                int* basePtr = (int*)CellCount.GetUnsafePtr();
                System.Threading.Interlocked.Increment(ref basePtr[hash]);
            }
        }

        // ── Pass 2: prefix sum cellCount → cellOffset (single-threaded) ─────────────

        [BurstCompile]
        internal struct PrefixSumPass : IJob
        {
            [ReadOnly] public NativeArray<int> CellCount;
            public NativeArray<int> CellOffset;

            public void Execute()
            {
                int total = 0;
                int n = CellCount.Length;
                for (int i = 0; i < n; i++)
                {
                    CellOffset[i] = total;
                    total += CellCount[i];
                }
                CellOffset[n] = total;
            }
        }

        // ── Pass 3: scatter bird indices into cellBirds (single-threaded, stable) ───

        [BurstCompile]
        internal struct ScatterPass : IJob
        {
            [ReadOnly] public NativeArray<float3>.ReadOnly Positions;
            [ReadOnly] public int    Count;
            [ReadOnly] public float3 BoundsMin;
            [ReadOnly] public float  InvCellSize;
            [ReadOnly] public int3   CellsPerAxis;
            [ReadOnly] public NativeArray<int> CellOffset;

            public NativeArray<int> CellBirds;

            public void Execute()
            {
                // Local cursor: per-cell next-write offset relative to cellOffset[hash].
                // Allocator.Temp is the right choice for short-lived scratch inside a Burst
                // IJob that completes within the same frame.
                int totalCells = CellsPerAxis.x * CellsPerAxis.y * CellsPerAxis.z;
                var cursor = new NativeArray<int>(totalCells, Allocator.Temp, NativeArrayOptions.ClearMemory);

                for (int i = 0; i < Count; i++)
                {
                    int3 cell = ComputeCell(Positions[i], BoundsMin, InvCellSize);
                    if (!InBounds(cell, CellsPerAxis)) continue;

                    int hash = Flatten(cell, CellsPerAxis);
                    int slot = cursor[hash]++;
                    CellBirds[CellOffset[hash] + slot] = i;
                }

                cursor.Dispose();
            }
        }
    }
}
