// AllocationRegressionTest.cs — Slice 4 / M6-4 PlayMode regression test that pins the
// per-frame managed-allocation budget at zero (≤ 1 KB tolerance) once the simulation
// has warmed up. If this test fails, somebody added a `new SomeClass()` / boxing /
// closure / params-array to a hot path; treat the test message as a bug filing
// instead of relaxing the threshold.
//
// Test recipe:
//   1. Build a 1000-bird flock with a fixed RandomSeed (deterministic spawn).
//   2. Tick 5 frames to absorb the one-time grid build / job-graph warm-up cost
//      (Burst JIT, NativeArray initial growth, etc.).
//   3. Snapshot Profiler.GetMonoUsedSizeLong().
//   4. Tick 60 more frames.
//   5. Snapshot again.
//   6. Assert (after - before) ≤ 1024 bytes.
//
// Diagnostic guidance on failure (in priority order):
//   - SteeringJobGraph.BuildKernelSettings: did someone change settingsByFlockId from
//     a stable IFlockSettings[] to something that re-allocates each Tick?
//   - FlockWorld.Tick TempJob arrays: NativeArray uses unmanaged storage and should NOT
//     show up. If GetMonoUsedSizeLong jumped, the leak is in managed code, not native.
//   - DispatchRendering: did the renderer start boxing settings into a managed object?
//   - BuildMatrices: any new LINQ / params usage?
//   - ProfilerMarker.Auto(): allocation-free in retail, but ensure no lambdas captured.

using System.Collections;
using Bird_behiviour.Flocking.Core;
using Bird_behiviour.Flocking.Simulation;
using Bird_behiviour.Flocking.Tooling;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.TestTools;

namespace Bird_behiviour.Flocking.Tests.PlayMode
{
    /// <summary>
    /// Slice 4 acceptance test: confirms the steering job graph + per-frame intermediates
    /// produce zero managed-heap growth over a steady-state run. Allocates 1000 birds at
    /// a fixed RandomSeed, warms 5 frames, then asserts the Mono heap delta over 60
    /// further ticks is ≤ 1024 bytes.
    /// </summary>
    public sealed class AllocationRegressionTest
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
        public IEnumerator Tick_60Frames_DoesNotGrowManagedHeap()
        {
            // ── Build scene ──────────────────────────────────────────────────────────
            var worldGO = new GameObject("FlockWorld");
            worldGO.SetActive(false);
            FlockWorld world = worldGO.AddComponent<FlockWorld>();

            var managerGO = new GameObject("FlockManager");
            managerGO.SetActive(false);
            FlockManager manager = managerGO.AddComponent<FlockManager>();

            var settings = ScriptableObject.CreateInstance<FlockSettings>();
            settings.name = "AllocRegressionFlockSettings";
            ApplyTestDefaults(settings);
            manager.SetSettings(settings);

            worldGO.SetActive(true);
            managerGO.SetActive(true);

            // Allow OnEnable callbacks (registers the flock + spawns birds).
            yield return null;

            Assert.AreEqual(1, world.RegisteredFlockCount);
            Assert.AreEqual(1000, world.TotalBirdCount);

            const float dt = 1f / 60f;

            // ── Warm-up: absorb Burst JIT + first-frame NativeArray cost ────────────
            for (int i = 0; i < 5; i++)
            {
                world.Tick(dt);
            }

            // Force a deterministic GC so the snapshot reflects steady-state heap.
            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
            System.GC.Collect();

            long before = Profiler.GetMonoUsedSizeLong();

            // ── Steady-state: 60 ticks. No managed allocation should occur. ─────────
            for (int i = 0; i < 60; i++)
            {
                world.Tick(dt);
            }

            long after = Profiler.GetMonoUsedSizeLong();
            long delta = after - before;

            // Confirm sanity (no exceptions in the loop).
            LogAssert.NoUnexpectedReceived();

            // ── Teardown ─────────────────────────────────────────────────────────────
            Object.DestroyImmediate(managerGO);
            Object.DestroyImmediate(worldGO);
            Object.DestroyImmediate(settings);

            // ── Assertion ────────────────────────────────────────────────────────────
            const long Threshold = 1024L;
            Assert.That(delta, Is.LessThanOrEqualTo(Threshold),
                $"Managed heap grew by {delta} bytes over 60 ticks at 1000 birds (threshold {Threshold} B).\n" +
                "Likely culprits (in order): SteeringJobGraph.BuildKernelSettings, FlockWorld.Tick TempJob " +
                "lifecycle, DispatchRendering closures, BuildMatrices LINQ. Profile Mono.GC.Alloc to diagnose.");
        }

        // Same overlay strategy as HelloFlockIntegrationTest but with 1000 birds for the
        // allocation-budget check. Keep settings tight so the 60-frame run stays well
        // under a reasonable test timeout.
        private static void ApplyTestDefaults(FlockSettings settings)
        {
            const string overlayJson =
                "{" +
                "\"inSeparationWeight\":1.0,\"inAlignmentWeight\":1.0,\"inCohesionWeight\":1.0," +
                "\"outSeparationWeight\":1.0,\"outAlignmentWeight\":0.0,\"outCohesionWeight\":0.0," +
                "\"preferredCenter\":{\"x\":0.0,\"y\":0.0,\"z\":0.0}," +
                "\"preferredExtents\":{\"x\":40.0,\"y\":20.0,\"z\":40.0}," +
                "\"preferredAttractionWeight\":1.0," +
                "\"perceptionRadius\":5.0,\"separationRadius\":1.5," +
                "\"perceptionConeHalfAngleRadians\":2.356194," +
                "\"minSpeed\":1.0,\"maxSpeed\":10.0,\"maxAcceleration\":30.0," +
                "\"cursorReactionStrength\":0.0,\"cursorReactionRadius\":10.0," +
                "\"birdCount\":1000,\"randomSeed\":424242" +
                "}";
            JsonUtility.FromJsonOverwrite(overlayJson, settings);
        }
    }
}
