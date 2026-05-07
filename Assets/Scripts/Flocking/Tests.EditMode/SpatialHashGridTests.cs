// SpatialHashGridTests.cs — Slice 3 (M2) EditMode unit tests for the cell-list spatial
// grid. Drives SpatialHashGrid + BuildGridJob directly (no FlockWorld, no MonoBehaviours)
// so the tests are hermetic and fast.
//
// Test cases:
//   1. SameCell_ReturnsBothBirds                 — two birds in one cell come back together
//   2. TwentySixNeighborCells_AllReturned        — each of the 26 face/edge/corner cells
//   3. OutOfBoundsBird_IsExcluded                — bird outside grid AABB is not built in
//   4. EdgePosition_LandsInDeterministicCell     — pos == boundsMin + n*cellSize ⇒ cell n
//   5. BuildIsDeterministic                      — same input twice ⇒ same cellBirds order

using System.Collections.Generic;
using Bird_behiviour.Flocking.Core;
using Bird_behiviour.Flocking.Spatial;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace Bird_behiviour.Flocking.Tests.EditMode
{
    /// <summary>
    /// EditMode tests for the cell-list spatial grid (<see cref="SpatialHashGrid"/> +
    /// <see cref="BuildGridJob"/>). No MonoBehaviours, no scene — each test allocates a
    /// small grid + a positions array, runs the build to completion synchronously, and
    /// asserts via <see cref="SpatialIndexReadOnly.GetNeighbors(float3)"/>.
    /// </summary>
    [TestFixture]
    public sealed class SpatialHashGridTests
    {
        // Use a 4×4×4 grid with 1-unit cells centred on the origin. Bounds AABB =
        // [-2, 2] on each axis. Plenty of headroom for placing test birds anywhere.
        private const float CellSize       = 1f;
        private static readonly int3 CellsPerAxis = new int3(4, 4, 4);
        private static readonly float3 BoundsMin  = new float3(-2f, -2f, -2f);

        private NativeLeakDetectionMode previousLeakMode;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            previousLeakMode = NativeLeakDetection.Mode;
            NativeLeakDetection.Mode = NativeLeakDetectionMode.EnabledWithStackTrace;
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            NativeLeakDetection.Mode = previousLeakMode;
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Allocates a grid sized to <see cref="CellsPerAxis"/> × <see cref="CellSize"/>,
        /// runs the full three-pass build for the supplied positions, and returns the
        /// grid. Caller is responsible for disposing it (use a using/try-finally).
        /// </summary>
        private static SpatialHashGrid BuildGrid(NativeArray<float3> positions, int count)
        {
            var grid = new SpatialHashGrid();
            grid.Allocate(BoundsMin, CellsPerAxis, CellSize, count);

            var handle = BuildGridJob.Schedule(grid, positions.AsReadOnly(), count, default);
            handle.Complete();
            return grid;
        }

        /// <summary>Drains an enumerator into a sorted list of bird indices (test convenience).</summary>
        private static List<int> Collect(NeighborEnumerator e)
        {
            var result = new List<int>(8);
            while (e.MoveNext())
            {
                result.Add(e.Current);
            }
            result.Sort();
            return result;
        }

        /// <summary>World-space centre of the cell at integer coords (cx, cy, cz).</summary>
        private static float3 CellCenter(int cx, int cy, int cz)
        {
            return BoundsMin + new float3(cx + 0.5f, cy + 0.5f, cz + 0.5f) * CellSize;
        }

        // ── Test 1: same-cell birds returned together ───────────────────────────────

        [Test]
        public void SameCell_ReturnsBothBirds()
        {
            // Two birds, both inside the same cell (1, 1, 1).
            var positions = new NativeArray<float3>(2, Allocator.Temp);
            positions[0] = CellCenter(1, 1, 1) + new float3(-0.1f, 0f, 0f);
            positions[1] = CellCenter(1, 1, 1) + new float3( 0.1f, 0f, 0f);

            var grid = BuildGrid(positions, positions.Length);
            try
            {
                SpatialIndexReadOnly view = grid.AsReadOnly();
                List<int> hits = Collect(view.GetNeighbors(CellCenter(1, 1, 1)));

                CollectionAssert.AreEquivalent(new[] { 0, 1 }, hits,
                    "Both birds in cell (1,1,1) should be returned by a query at the cell centre.");
            }
            finally
            {
                grid.Dispose();
                positions.Dispose();
            }
        }

        // ── Test 2: every one of the 26 surrounding cells is reachable ──────────────

        [Test]
        public void TwentySixNeighborCells_AllReturned()
        {
            // Centre the test on cell (2, 2, 2). Place 27 birds — one per cell in the
            // 3×3×3 block centred on (2, 2, 2). The query at the centre cell must
            // return all 27 indices (the centre bird + the 26 neighbours).
            var positions = new NativeArray<float3>(27, Allocator.Temp);
            int writeIdx = 0;
            for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                positions[writeIdx++] = CellCenter(2 + dx, 2 + dy, 2 + dz);
            }

            var grid = BuildGrid(positions, positions.Length);
            try
            {
                SpatialIndexReadOnly view = grid.AsReadOnly();
                List<int> hits = Collect(view.GetNeighbors(CellCenter(2, 2, 2)));

                Assert.AreEqual(27, hits.Count,
                    "Expected the centre + all 26 surrounding cell-occupants to be returned.");
                for (int i = 0; i < 27; i++)
                {
                    Assert.IsTrue(hits.Contains(i), $"Bird {i} (cell offset) was missing from the 27-cell scan.");
                }
            }
            finally
            {
                grid.Dispose();
                positions.Dispose();
            }
        }

        // ── Test 3: birds outside the AABB are excluded from the build ──────────────

        [Test]
        public void OutOfBoundsBird_IsExcluded()
        {
            // Bird 0 is well inside the grid; bird 1 is far outside on +X.
            var positions = new NativeArray<float3>(2, Allocator.Temp);
            positions[0] = CellCenter(1, 1, 1);
            positions[1] = new float3(1000f, 0f, 0f); // far outside [-2, 2]

            var grid = BuildGrid(positions, positions.Length);
            try
            {
                SpatialIndexReadOnly view = grid.AsReadOnly();

                // Total prefix-sum entry == count of birds actually inserted into cells.
                int builtCount = grid.CellOffset[grid.TotalCells];
                Assert.AreEqual(1, builtCount,
                    "Out-of-bounds bird should not contribute to any cell.");

                // A query inside any in-bounds cell must never return the OOB bird (1).
                List<int> hits = Collect(view.GetNeighbors(CellCenter(1, 1, 1)));
                CollectionAssert.AreEqual(new[] { 0 }, hits,
                    "Only the in-bounds bird should be enumerated.");
                Assert.IsFalse(hits.Contains(1), "Out-of-bounds bird must never appear in a neighbour scan.");
            }
            finally
            {
                grid.Dispose();
                positions.Dispose();
            }
        }

        // ── Test 4: positions exactly on cell edges are placed deterministically ────

        [Test]
        public void EdgePosition_LandsInDeterministicCell()
        {
            // Canonical hash uses floor((pos - boundsMin) / cellSize):
            //   pos = boundsMin + 1 * cellSize on X  → cell.x = 1 (NOT 0).
            //   pos = boundsMin                      → cell = (0, 0, 0).
            //   pos = boundsMin + 2 * cellSize       → cell = (2, ...).
            // We place birds at exact edge positions and confirm each lands in the cell
            // its query origin (the cell centre 0.5 units inside) reaches.
            var positions = new NativeArray<float3>(3, Allocator.Temp);
            positions[0] = BoundsMin;                                    // cell (0,0,0)
            positions[1] = BoundsMin + new float3(CellSize, 0f, 0f);     // cell (1,0,0)
            positions[2] = BoundsMin + new float3(0f, 2f * CellSize, 0f);// cell (0,2,0)

            var grid = BuildGrid(positions, positions.Length);
            try
            {
                SpatialIndexReadOnly view = grid.AsReadOnly();

                // Bird 0 must be reachable from a query at the centre of (0,0,0).
                List<int> at000 = Collect(view.GetNeighbors(CellCenter(0, 0, 0)));
                Assert.IsTrue(at000.Contains(0),
                    "Bird at boundsMin should land in cell (0,0,0) via floor.");

                // Bird 1 must be reachable from the centre of (1,0,0), NOT (0,0,0).
                List<int> at100 = Collect(view.GetNeighbors(CellCenter(1, 0, 0)));
                Assert.IsTrue(at100.Contains(1),
                    "Bird at +1 cellSize on X must land in cell (1,0,0) via floor.");

                // Bird 2 must be reachable from (0,2,0). Use a query origin that sits
                // far enough away from (0,0,0) that the 27-cell block around (0,0,0)
                // does NOT touch (0,2,0) — a query at cell (0,0,0) covers y∈[-1..1],
                // so cell (0,2,0) is not in that block. Confirm bird 2 is NOT found there.
                Assert.IsFalse(at000.Contains(2),
                    "Bird at +2 cellSize on Y must not appear in the 3x3x3 block around (0,0,0).");

                List<int> at020 = Collect(view.GetNeighbors(CellCenter(0, 2, 0)));
                Assert.IsTrue(at020.Contains(2),
                    "Bird at +2 cellSize on Y must land in cell (0,2,0) via floor.");
            }
            finally
            {
                grid.Dispose();
                positions.Dispose();
            }
        }

        // ── Test 5: build is deterministic — same input ⇒ same cellBirds layout ───

        [Test]
        public void BuildIsDeterministic_SameInputProducesSameCellBirdsOrder()
        {
            // Pack 16 birds into 4 distinct cells (4 birds per cell) so the test exercises
            // within-cell ordering as well as cross-cell ordering. If Pass 3's scatter is
            // not stable, run-to-run within-cell shuffles will surface here.
            var positions = new NativeArray<float3>(16, Allocator.Temp);
            int3[] cells =
            {
                new int3(0, 0, 0),
                new int3(1, 1, 1),
                new int3(2, 2, 2),
                new int3(3, 0, 1),
            };
            for (int i = 0; i < 16; i++)
            {
                int3 c = cells[i % cells.Length];
                // Slight per-bird offset within each cell so positions aren't degenerate;
                // still well inside the cell so they all hash the same.
                float3 jitter = new float3((i * 0.07f) - 0.3f, (i * 0.05f) - 0.2f, (i * 0.03f) - 0.1f);
                positions[i] = CellCenter(c.x, c.y, c.z) + jitter * 0.1f;
            }

            var first  = BuildGrid(positions, positions.Length);
            var second = BuildGrid(positions, positions.Length);
            try
            {
                Assert.AreEqual(first.TotalCells, second.TotalCells, "Grid sizes must match.");
                Assert.AreEqual(first.BirdCapacity, second.BirdCapacity, "Bird capacities must match.");

                // Compare cellOffset entry-for-entry.
                for (int i = 0; i <= first.TotalCells; i++)
                {
                    Assert.AreEqual(first.CellOffset[i], second.CellOffset[i],
                        $"cellOffset mismatch at index {i}.");
                }

                // Compare cellBirds entry-for-entry — within-cell ordering must be stable.
                int total = first.CellOffset[first.TotalCells];
                for (int i = 0; i < total; i++)
                {
                    Assert.AreEqual(first.CellBirds[i], second.CellBirds[i],
                        $"cellBirds mismatch at slot {i} (within-cell order is not stable).");
                }
            }
            finally
            {
                first.Dispose();
                second.Dispose();
                positions.Dispose();
            }
        }
    }
}
