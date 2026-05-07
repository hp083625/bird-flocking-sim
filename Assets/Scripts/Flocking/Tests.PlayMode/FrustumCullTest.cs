// FrustumCullTest.cs — Slice 8 / M4 PlayMode test for FrustumCullJob + BuildMatricesJob.
//
// Headless-friendly: rather than wire up a real MainCamera in the test scene we build
// 6 frustum planes by hand (a tight axis-aligned box sliver around X∈[-30, 30],
// Y∈[-50, 50], Z∈[-50, 50]) and pin them via FlockWorld.SetCameraFrustumPlanesForTest.
// 100 birds are then placed deterministically across the full X∈[-50, 50] range with
// fixed Y/Z inside the test frustum, so the post-cull visible set is *exactly* the
// birds whose X is inside the padded slab. This makes the assertions cheap to verify
// and the test resilient to whatever Camera.main aspect ratio / FOV the host editor
// happens to ship with.
//
// Three assertions per the slice spec:
//   1. visibleCount < total bird count (some birds were culled)
//   2. visibleCount > 0 (some birds were kept — sanity)
//   3. every reported visible bird index has a position inside the *padded* slab
//      (centre slab ± BirdCullRadius), proving the job didn't hand back false positives

using System.Collections;
using Bird_behiviour.Flocking.Core;
using Bird_behiviour.Flocking.Simulation;
using Bird_behiviour.Flocking.Tooling;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace Bird_behiviour.Flocking.Tests.PlayMode
{
    /// <summary>
    /// Slice 8 acceptance test: spawns 100 birds spread across the full world X axis,
    /// pins a tight test frustum around X∈[-30, 30], runs 5 ticks, and asserts the cull
    /// kept a strict subset whose positions all live inside the padded slab.
    /// </summary>
    public sealed class FrustumCullTest
    {
        private const int    BirdCount        = 100;
        private const float  SlabHalfX        = 30f;   // test frustum slab in X
        private const float  WorldHalfX       = 50f;   // birds spread across [-50, 50]
        private const float  WorldHalfYZ      = 5f;    // birds packed in a thin Y/Z slab so test plane is the only cull axis
        private const float  CullRadiusFudge  = 1.0f;  // generous tolerance for the padded-slab assertion (≥ default 0.5 BirdCullRadius)

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

        [UnityTest]
        public IEnumerator TightFrustum_CullsBirdsOutsideSlab_KeptIndicesAreInsidePaddedSlab()
        {
            // ── Build a deterministic 1-flock world ─────────────────────────────────
            var worldGO = new GameObject("FlockWorld");
            worldGO.SetActive(false);
            FlockWorld world = worldGO.AddComponent<FlockWorld>();

            var managerGO = new GameObject("FlockManager");
            managerGO.SetActive(false);
            FlockManager manager = managerGO.AddComponent<FlockManager>();

            var settings = ScriptableObject.CreateInstance<FlockSettings>();
            settings.name = "FrustumCullTestFlockSettings";
            ApplyTestDefaults(settings);
            manager.SetSettings(settings);

            worldGO.SetActive(true);
            managerGO.SetActive(true);

            // Let OnEnable fire so the manager registers and the per-bird arrays exist.
            yield return null;

            Assert.AreEqual(1, world.RegisteredFlockCount);
            Assert.AreEqual(BirdCount, world.TotalBirdCount);

            // ── Override the random spawn with a deterministic X-axis spread so the
            //    cull's expected output is exactly knowable from each bird's X coord.
            for (int i = 0; i < BirdCount; i++)
            {
                // X linearly spans [-WorldHalfX, +WorldHalfX]; Y/Z parked at 0 so they
                // sit comfortably inside the test frustum's Y/Z slabs.
                float t = BirdCount == 1 ? 0.5f : i / (float)(BirdCount - 1);
                float x = math.lerp(-WorldHalfX, WorldHalfX, t);
                world.Positions[i]  = new float3(x, 0f, 0f);
                world.Velocities[i] = new float3(0f, 0f, 1f); // stationary heading; integrator will keep speed at MinSpeed
            }

            // ── Build 6 hand-rolled frustum planes for an axis-aligned box slab.
            //    GeometryUtility convention: planes face INWARD; a point p is inside
            //    iff dot(n, p) + d >= 0 for every plane.
            //
            //    Slab: X ∈ [-SlabHalfX, +SlabHalfX], Y ∈ [-WorldHalfYZ, +WorldHalfYZ], Z ∈ [-WorldHalfYZ, +WorldHalfYZ]
            using (var planes = new NativeArray<float4>(6, Allocator.Temp, NativeArrayOptions.UninitializedMemory))
            {
                // +X face: normal = (-1, 0, 0), distance = +SlabHalfX  →  -px + SlabHalfX >= 0
                planes[0] = new float4(-1f, 0f, 0f, SlabHalfX);
                // -X face: normal = (+1, 0, 0), distance = +SlabHalfX  →   px + SlabHalfX >= 0
                planes[1] = new float4(+1f, 0f, 0f, SlabHalfX);
                // +Y face
                planes[2] = new float4(0f, -1f, 0f, WorldHalfYZ);
                // -Y face
                planes[3] = new float4(0f, +1f, 0f, WorldHalfYZ);
                // +Z face
                planes[4] = new float4(0f, 0f, -1f, WorldHalfYZ);
                // -Z face
                planes[5] = new float4(0f, 0f, +1f, WorldHalfYZ);

                world.SetCameraFrustumPlanesForTest(planes);
            }

            // ── Drive the sim for 5 ticks — long enough for the integrator to settle
            //    and the cull job to populate visibleIndices, short enough to keep the
            //    bird-position deterministic-ish (we re-pin positions before the last
            //    tick so the post-Tick visibleIndices reflect a known layout).
            const float dt = 1f / 60f;
            for (int frame = 0; frame < 4; frame++)
            {
                world.Tick(dt);
            }

            // Re-pin positions immediately before the final Tick so the cull's output
            // for THIS tick exactly reflects the deterministic X spread above —
            // independent of what 4 ticks of integration did to the positions.
            for (int i = 0; i < BirdCount; i++)
            {
                float t = BirdCount == 1 ? 0.5f : i / (float)(BirdCount - 1);
                float x = math.lerp(-WorldHalfX, WorldHalfX, t);
                world.Positions[i]  = new float3(x, 0f, 0f);
                world.Velocities[i] = new float3(0f, 0f, 1f);
            }
            world.Tick(dt);

            // ── Assertions ───────────────────────────────────────────────────────────
            int visibleCount = world.GetVisibleCountForTest(0);

            Assert.Greater(visibleCount, 0,
                "Expected at least one bird to survive the cull (slab covers X∈[-30, 30] and birds span [-50, 50]).");
            Assert.Less(visibleCount, BirdCount,
                $"Expected SOME birds to be culled (visibleCount={visibleCount}, total={BirdCount}). " +
                "Either the cull job is broken or the frustum-plane override didn't take effect.");

            // Every visible bird index must point at a position inside the padded slab.
            float padX = SlabHalfX + world.BirdCullRadius + CullRadiusFudge;
            int[] visibleIndices = world.GetVisibleIndicesSnapshotForTest(0);
            Assert.AreEqual(visibleCount, visibleIndices.Length,
                "GetVisibleIndicesSnapshotForTest length must match GetVisibleCountForTest.");

            for (int k = 0; k < visibleIndices.Length; k++)
            {
                int birdIdx = visibleIndices[k];
                Assert.IsTrue(birdIdx >= 0 && birdIdx < BirdCount,
                    $"Visible index {k} = {birdIdx} is outside [0, {BirdCount}).");

                float3 p = world.Positions[birdIdx];
                Assert.IsTrue(math.abs(p.x) <= padX,
                    $"Visible bird {birdIdx} at X={p.x} sits outside the padded slab |X|<={padX}. " +
                    "FrustumCullJob is producing false positives — check the inward-normal convention or the radius pad.");
            }

            LogAssert.NoUnexpectedReceived();

            // ── Teardown ─────────────────────────────────────────────────────────────
            Object.DestroyImmediate(managerGO);
            Object.DestroyImmediate(worldGO);
            Object.DestroyImmediate(settings);
        }

        // Same JSON-overlay strategy as HelloFlockIntegrationTest: 100 birds with neutral
        // weights, a wide preferred zone so the soft bounds force is ~zero in the centre
        // of the world, and zero cursor strength so cursor doesn't perturb positions.
        private static void ApplyTestDefaults(FlockSettings settings)
        {
            const string overlayJson =
                "{" +
                "\"inSeparationWeight\":0.0,\"inAlignmentWeight\":0.0,\"inCohesionWeight\":0.0," +
                "\"outSeparationWeight\":0.0,\"outAlignmentWeight\":0.0,\"outCohesionWeight\":0.0," +
                "\"preferredCenter\":{\"x\":0.0,\"y\":0.0,\"z\":0.0}," +
                "\"preferredExtents\":{\"x\":80.0,\"y\":40.0,\"z\":80.0}," +
                "\"preferredAttractionWeight\":0.0," +
                "\"perceptionRadius\":1.0,\"separationRadius\":0.5," +
                "\"perceptionConeHalfAngleRadians\":2.356194," +
                "\"minSpeed\":0.1,\"maxSpeed\":1.0,\"maxAcceleration\":1.0," +
                "\"cursorReactionStrength\":0.0,\"cursorReactionRadius\":1.0," +
                "\"birdCount\":100,\"randomSeed\":49169" +
                "}";
            JsonUtility.FromJsonOverwrite(overlayJson, settings);
        }
    }
}
