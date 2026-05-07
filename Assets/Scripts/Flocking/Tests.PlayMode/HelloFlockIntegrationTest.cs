// HelloFlockIntegrationTest.cs — Slice 2 PlayMode acceptance test (M6-3 stub).
//
// Builds a fresh scene in code: one FlockWorld + one FlockManager + one in-memory
// FlockSettings (ScriptableObject.CreateInstance — no asset on disk), spins the sim
// for 60 fixed-dt ticks via FlockWorld.Tick, and asserts:
//   1. no NaN positions across all birds
//   2. no error / exception logs
//   3. every bird inside WorldBoundsCenter +- WorldBoundsExtents * 1.5

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
    /// Slice 2 acceptance test. Lightweight — does not load the sandbox scene; instead
    /// constructs the minimum viable hierarchy in code so the test is hermetic.
    /// </summary>
    public sealed class HelloFlockIntegrationTest
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
        public IEnumerator HelloFlock_60Ticks_BirdsStayValidAndInBounds()
        {
            // ── Build scene (deactivated parent so SetSettings can win the race) ─────
            var worldGO = new GameObject("FlockWorld");
            worldGO.SetActive(false);
            FlockWorld world = worldGO.AddComponent<FlockWorld>();

            var managerGO = new GameObject("FlockManager");
            managerGO.SetActive(false);
            FlockManager manager = managerGO.AddComponent<FlockManager>();

            // Deterministic in-memory settings.
            var settings = ScriptableObject.CreateInstance<FlockSettings>();
            settings.name = "TestFlockSettings";
            ApplyTestDefaults(settings);
            manager.SetSettings(settings);

            // Activate (OnEnable on FlockWorld first via execution order, then FlockManager
            // → registers, slice allocated, birds spawned).
            worldGO.SetActive(true);
            managerGO.SetActive(true);

            // Let one frame elapse for OnEnable callbacks.
            yield return null;

            Assert.AreEqual(1, world.RegisteredFlockCount, "FlockManager should have registered.");
            Assert.AreEqual(100, world.TotalBirdCount, "Total bird count should equal BirdCount.");

            // ── Drive 60 fixed ticks ─────────────────────────────────────────────────
            const float dt = 1f / 60f;
            for (int frame = 0; frame < 60; frame++)
            {
                world.Tick(dt);
            }

            // ── Assertions ────────────────────────────────────────────────────────────
            float3 boundsCenter  = world.WorldBoundsCenter;
            float3 boundsExtents = (float3)world.WorldBoundsExtents * 1.5f;

            int total = world.TotalBirdCount;
            for (int i = 0; i < total; i++)
            {
                float3 p = world.Positions[i];

                Assert.IsFalse(math.any(math.isnan(p)),
                    $"Bird {i} produced NaN position after 60 ticks: {p}");
                Assert.IsFalse(math.any(math.isinf(p)),
                    $"Bird {i} produced infinite position after 60 ticks: {p}");

                float3 offset = math.abs(p - boundsCenter);
                bool inBounds = math.all(offset <= boundsExtents);
                Assert.IsTrue(inBounds,
                    $"Bird {i} escaped WorldBounds*1.5 after 60 ticks: pos={p}, " +
                    $"center={boundsCenter}, allowedHalfExtents={boundsExtents}");
            }

            // Confirms no exception / error logs were emitted by the simulation.
            LogAssert.NoUnexpectedReceived();

            // ── Teardown ──────────────────────────────────────────────────────────────
            Object.DestroyImmediate(managerGO);
            Object.DestroyImmediate(worldGO);
            Object.DestroyImmediate(settings);
        }

        // Reflection-free way to seed the in-memory FlockSettings: writes via SerializedObject
        // would need an Editor reference, and we don't have one in PlayMode. Instead we set
        // the public-API-derived defaults that exercise a balanced flock: 100 birds in a
        // moderate volume with sane perception / speed.
        private static void ApplyTestDefaults(FlockSettings settings)
        {
            // FlockSettings exposes only get-properties (per IFlockSettings), so we use
            // SerializedObject through Unity's JsonUtility round-trip to set fields.
            // The asset's defaults already match what we want for Slice 2 except that we
            // want a deterministic seed and a smaller world to keep the test runtime tight.
            //
            // We achieve determinism + tighter world by JSON-overlaying a partial spec.
            const string overlayJson =
                "{" +
                "\"inSeparationWeight\":1.0,\"inAlignmentWeight\":1.0,\"inCohesionWeight\":1.0," +
                "\"outSeparationWeight\":1.0,\"outAlignmentWeight\":0.0,\"outCohesionWeight\":0.0," +
                "\"preferredCenter\":{\"x\":0.0,\"y\":0.0,\"z\":0.0}," +
                "\"preferredExtents\":{\"x\":15.0,\"y\":8.0,\"z\":15.0}," +
                "\"preferredAttractionWeight\":1.0," +
                "\"perceptionRadius\":5.0,\"separationRadius\":1.5," +
                "\"perceptionConeHalfAngleRadians\":2.356194," +
                "\"minSpeed\":1.0,\"maxSpeed\":10.0,\"maxAcceleration\":30.0," +
                "\"cursorReactionStrength\":0.0,\"cursorReactionRadius\":10.0," +
                "\"birdCount\":100,\"randomSeed\":12345" +
                "}";
            JsonUtility.FromJsonOverwrite(overlayJson, settings);
        }
    }
}
