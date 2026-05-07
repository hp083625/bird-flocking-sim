// MultiFlockCoexistTest.cs — Slice 5 PlayMode acceptance test.
//
// Registers TWO FlockManagers with 500 birds each, runs 60 fixed-dt ticks, and
// asserts:
//   1. FlockWorld.RegisteredFlockCount == 2 and TotalBirdCount == 1000
//   2. FlockIds[] correctly partition the global array (slice A = [0,500), slice B = [500,1000))
//   3. all 1000 birds remain finite + inside WorldBounds*1.5 after 60 ticks
//   4. NO unexpected error/exception logs (LogAssert.NoUnexpectedReceived)
//
// Both flocks are configured with out-of-flock weights EQUAL to in-flock (per
// Slice 5 brief — the predator/prey cross-flock asymmetry lands in Slice 6).
// This still exercises the NeighborForcesJob's in/out branch (every bird sees
// neighbours of both flock ids), it just does so symmetrically.

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
    public sealed class MultiFlockCoexistTest
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
        public IEnumerator TwoFlocks_500Each_60Ticks_StayInBoundsNoExceptions()
        {
            // ── Build scene: one world + two managers, all deactivated until wired ───
            var worldGO = new GameObject("FlockWorld");
            worldGO.SetActive(false);
            FlockWorld world = worldGO.AddComponent<FlockWorld>();

            var managerA_GO = new GameObject("FlockManager_A");
            managerA_GO.SetActive(false);
            FlockManager managerA = managerA_GO.AddComponent<FlockManager>();

            var managerB_GO = new GameObject("FlockManager_B");
            managerB_GO.SetActive(false);
            FlockManager managerB = managerB_GO.AddComponent<FlockManager>();

            var settingsA = ScriptableObject.CreateInstance<FlockSettings>();
            settingsA.name = "TestFlockSettings_A";
            ApplyTestDefaults(settingsA, seed: 12345);
            managerA.SetSettings(settingsA);

            var settingsB = ScriptableObject.CreateInstance<FlockSettings>();
            settingsB.name = "TestFlockSettings_B";
            // Different seed so the two flocks spawn distinctly even with identical other params.
            ApplyTestDefaults(settingsB, seed: 67890);
            managerB.SetSettings(settingsB);

            worldGO.SetActive(true);
            managerA_GO.SetActive(true);
            managerB_GO.SetActive(true);

            yield return null;

            // ── Registration assertions ──────────────────────────────────────────────
            Assert.AreEqual(2, world.RegisteredFlockCount, "Both managers should have registered.");
            Assert.AreEqual(1000, world.TotalBirdCount, "TotalBirdCount should be 500 + 500.");
            Assert.AreEqual(2, world.Slices.Length);

            FlockSlice sliceA = world.Slices[0];
            FlockSlice sliceB = world.Slices[1];
            Assert.AreEqual(0,   sliceA.StartIndex);
            Assert.AreEqual(500, sliceA.Count);
            Assert.AreEqual(0,   (int)sliceA.FlockId);
            Assert.AreEqual(500, sliceB.StartIndex);
            Assert.AreEqual(500, sliceB.Count);
            Assert.AreEqual(1,   (int)sliceB.FlockId);

            // Verify the FlockIds[] partition matches Slices.
            for (int i = 0; i < 500; i++)
            {
                Assert.AreEqual((byte)0, world.FlockIds[i],
                    $"Bird {i} should belong to flock 0; was {world.FlockIds[i]}");
            }
            for (int i = 500; i < 1000; i++)
            {
                Assert.AreEqual((byte)1, world.FlockIds[i],
                    $"Bird {i} should belong to flock 1; was {world.FlockIds[i]}");
            }

            // ── Drive 60 fixed ticks ─────────────────────────────────────────────────
            const float dt = 1f / 60f;
            for (int frame = 0; frame < 60; frame++)
            {
                world.Tick(dt);
            }

            // ── Validity + bounds ────────────────────────────────────────────────────
            float3 boundsCenter  = world.WorldBoundsCenter;
            float3 boundsExtents = (float3)world.WorldBoundsExtents * 1.5f;

            int total = world.TotalBirdCount;
            for (int i = 0; i < total; i++)
            {
                float3 p = world.Positions[i];

                Assert.IsFalse(math.any(math.isnan(p)),
                    $"Bird {i} (flock {world.FlockIds[i]}) produced NaN after 60 ticks: {p}");
                Assert.IsFalse(math.any(math.isinf(p)),
                    $"Bird {i} (flock {world.FlockIds[i]}) produced infinite position after 60 ticks: {p}");

                float3 offset = math.abs(p - boundsCenter);
                bool inBounds = math.all(offset <= boundsExtents);
                Assert.IsTrue(inBounds,
                    $"Bird {i} (flock {world.FlockIds[i]}) escaped WorldBounds*1.5 after 60 ticks: pos={p}");
            }

            LogAssert.NoUnexpectedReceived();

            // ── Teardown ──────────────────────────────────────────────────────────────
            Object.DestroyImmediate(managerA_GO);
            Object.DestroyImmediate(managerB_GO);
            Object.DestroyImmediate(worldGO);
            Object.DestroyImmediate(settingsA);
            Object.DestroyImmediate(settingsB);
        }

        // Symmetric out-of-flock weights (= in-flock) per the Slice 5 brief: every
        // bird responds to neighbours of either flock equally. Slice 6 will introduce
        // the predator/prey asymmetry by tuning these per asset.
        private static void ApplyTestDefaults(FlockSettings settings, int seed)
        {
            string overlayJson =
                "{" +
                "\"inSeparationWeight\":1.0,\"inAlignmentWeight\":1.0,\"inCohesionWeight\":1.0," +
                "\"outSeparationWeight\":1.0,\"outAlignmentWeight\":1.0,\"outCohesionWeight\":1.0," +
                "\"preferredCenter\":{\"x\":0.0,\"y\":0.0,\"z\":0.0}," +
                "\"preferredExtents\":{\"x\":20.0,\"y\":10.0,\"z\":20.0}," +
                "\"preferredAttractionWeight\":1.0," +
                "\"perceptionRadius\":5.0,\"separationRadius\":1.5," +
                "\"perceptionConeHalfAngleRadians\":2.356194," +
                "\"minSpeed\":1.0,\"maxSpeed\":10.0,\"maxAcceleration\":30.0," +
                "\"cursorReactionStrength\":0.0,\"cursorReactionRadius\":10.0," +
                "\"birdCount\":500,\"randomSeed\":" + seed +
                "}";
            JsonUtility.FromJsonOverwrite(overlayJson, settings);
        }
    }
}
