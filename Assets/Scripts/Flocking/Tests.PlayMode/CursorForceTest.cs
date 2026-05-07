// CursorForceTest.cs — Slice 7 / M6 PlayMode test for the real CursorForceJob.
// Three single-bird scenarios validate the kernel end-to-end through the job graph:
//
//   1. Attract  (strength = +5) → distance to cursor decreases over 60 ticks.
//   2. Repel    (strength = -5) → distance to cursor increases over 60 ticks.
//   3. Off      (cursorOnScreen = false) → bird stays put (no other forces in a
//      1-bird flock with origin-centred soft bounds).
//
// To make these deterministic we override Positions[0] / Velocities[0] *after*
// FlockManager.SpawnIntoSlice runs (which seeds them randomly), and tune
// FlockSettings so neighbour / bounds forces are zero in the steady state:
//   - 1 bird → no neighbours.
//   - preferredCenter = origin, large preferredExtents → soft-bounds force = 0
//     for any bird position inside the radius we'll be testing.
//   - large worldBoundsExtents + bird stays well inside → hard-bounds force = 0.
//   - minSpeed = 0 → IntegrateJob doesn't kick a stationary bird back into motion
//     in test 3 (the "off" case).
// All weights left at zero except CursorReactionStrength so only the cursor
// branch contributes acceleration.

using System.Collections;
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
    /// Slice 7 acceptance: the real <c>CursorForceJob</c> applies signed
    /// per-flock cursor reaction with a smoothstep falloff, respects the
    /// <c>cursorOnScreen</c> gate, and is wired into <c>FlockWorld.Tick</c> via
    /// <c>SteeringJobGraph.DispatchSpec</c>.
    /// </summary>
    public sealed class CursorForceTest
    {
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
        public IEnumerator Cursor_AttractStrength_DecreasesDistance()
        {
            yield return RunCursorScenario(
                strength: +5f,
                cursorOnScreen: true,
                cursorWorldPoint: new float3(5f, 0f, 0f),
                assertion: (initialDist, finalDist) =>
                    Assert.Less(finalDist, initialDist - 0.1f,
                        $"Attract: bird should have moved closer to the cursor " +
                        $"(initial={initialDist:F4}, final={finalDist:F4})."));
        }

        [UnityTest]
        public IEnumerator Cursor_RepelStrength_IncreasesDistance()
        {
            yield return RunCursorScenario(
                strength: -5f,
                cursorOnScreen: true,
                cursorWorldPoint: new float3(5f, 0f, 0f),
                assertion: (initialDist, finalDist) =>
                    Assert.Greater(finalDist, initialDist + 0.1f,
                        $"Repel: bird should have moved away from the cursor " +
                        $"(initial={initialDist:F4}, final={finalDist:F4})."));
        }

        [UnityTest]
        public IEnumerator Cursor_OffScreen_DoesNotMoveBird()
        {
            yield return RunCursorScenario(
                strength: +5f,
                cursorOnScreen: false,
                cursorWorldPoint: new float3(5f, 0f, 0f),
                assertion: (initialDist, finalDist) =>
                    Assert.That(math.abs(finalDist - initialDist), Is.LessThan(1e-3f),
                        $"Off-screen cursor: bird should not have moved " +
                        $"(initial={initialDist:F6}, final={finalDist:F6})."));
        }

        // ── Shared scenario harness ──────────────────────────────────────────────────

        private delegate void DistanceAssertion(float initialDist, float finalDist);

        private static IEnumerator RunCursorScenario(
            float strength,
            bool cursorOnScreen,
            float3 cursorWorldPoint,
            DistanceAssertion assertion)
        {
            // ── Build a hermetic 1-bird FlockWorld + FlockManager ────────────────────
            var worldGO = new GameObject("FlockWorld");
            worldGO.SetActive(false);
            FlockWorld world = worldGO.AddComponent<FlockWorld>();

            var managerGO = new GameObject("FlockManager");
            managerGO.SetActive(false);
            FlockManager manager = managerGO.AddComponent<FlockManager>();

            var settings = ScriptableObject.CreateInstance<FlockSettings>();
            settings.name = "CursorTestFlockSettings";
            ApplyTestDefaults(settings, strength);
            manager.SetSettings(settings);

            worldGO.SetActive(true);
            managerGO.SetActive(true);

            // Wait one frame for OnEnable / RegisterFlock / SpawnIntoSlice.
            yield return null;

            Assert.AreEqual(1, world.RegisteredFlockCount);
            Assert.AreEqual(1, world.TotalBirdCount);

            // ── Override the spawned position + velocity so the test is deterministic
            //    regardless of the RandomSeed-driven SpawnIntoSlice output ────────────
            world.Positions[0]  = float3.zero;
            world.Velocities[0] = float3.zero;

            // ── Publish the cursor state CursorForceJob will read this Tick ──────────
            world.SetCursor(cursorWorldPoint, cursorOnScreen);

            float initialDist = math.distance(world.Positions[0], cursorWorldPoint);

            const float dt = 1f / 60f;
            for (int frame = 0; frame < 60; frame++)
            {
                // Re-publish each frame in case any future code clears the cursor in Tick.
                world.SetCursor(cursorWorldPoint, cursorOnScreen);
                world.Tick(dt);
            }

            float finalDist = math.distance(world.Positions[0], cursorWorldPoint);

            // No exceptions / errors should have leaked from the simulation.
            LogAssert.NoUnexpectedReceived();

            // ── Teardown before assert (so a failed assert doesn't leak NativeArrays) ─
            Object.DestroyImmediate(managerGO);
            Object.DestroyImmediate(worldGO);
            Object.DestroyImmediate(settings);

            assertion(initialDist, finalDist);
        }

        // Tuned so only the cursor branch contributes:
        //  - all neighbour weights = 0 (and N=1 → no neighbours anyway)
        //  - preferredAttractionWeight = 0 + huge soft zone (no soft pull)
        //  - perceptionRadius small but nonzero (FlockWorld auto-derives cellSize from it)
        //  - minSpeed = 0 so the off-screen test can hold position
        //  - cursorReactionRadius = 10 so a bird at origin reacts to a cursor at (5,0,0)
        private static void ApplyTestDefaults(FlockSettings settings, float cursorStrength)
        {
            string overlayJson =
                "{" +
                "\"inSeparationWeight\":0.0,\"inAlignmentWeight\":0.0,\"inCohesionWeight\":0.0," +
                "\"outSeparationWeight\":0.0,\"outAlignmentWeight\":0.0,\"outCohesionWeight\":0.0," +
                "\"preferredCenter\":{\"x\":0.0,\"y\":0.0,\"z\":0.0}," +
                "\"preferredExtents\":{\"x\":1000.0,\"y\":1000.0,\"z\":1000.0}," +
                "\"preferredAttractionWeight\":0.0," +
                "\"perceptionRadius\":1.0,\"separationRadius\":0.5," +
                "\"perceptionConeHalfAngleRadians\":2.356194," +
                "\"minSpeed\":0.0,\"maxSpeed\":50.0,\"maxAcceleration\":100.0," +
                "\"cursorReactionStrength\":" + cursorStrength.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + "," +
                "\"cursorReactionRadius\":10.0," +
                "\"birdCount\":1,\"randomSeed\":7" +
                "}";
            JsonUtility.FromJsonOverwrite(overlayJson, settings);
        }
    }
}
