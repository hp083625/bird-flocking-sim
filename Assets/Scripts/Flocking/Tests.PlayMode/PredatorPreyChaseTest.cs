// PredatorPreyChaseTest.cs — Slice 6 PlayMode acceptance test.
//
// Spawns 50 predators (high outCohesion → chase) + 500 prey (high outSeparation
// → flee), runs 120 fixed-dt ticks, and asserts the centroid-to-centroid
// distance trends DOWN over the run window. We compare the mean centroid
// distance over the FIRST 30 ticks vs the LAST 30 ticks — predators must close
// in on prey on average even though prey actively flee.
//
// Why centroid distance vs nearest-pair distance:
//   - Centroid is robust to outliers (a single straggler in either flock won't
//     skew the metric like a min-distance test would).
//   - Mean over a window absorbs the natural oscillation as a chase begins
//     (predators accelerate, prey scatter, predators reorient).

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
    public sealed class PredatorPreyChaseTest
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
        public IEnumerator Predators_ChasePrey_CentroidDistanceTrendsDown()
        {
            // ── Build scene ─────────────────────────────────────────────────────────
            var worldGO = new GameObject("FlockWorld");
            worldGO.SetActive(false);
            FlockWorld world = worldGO.AddComponent<FlockWorld>();

            var preyGO = new GameObject("FlockManager_Prey");
            preyGO.SetActive(false);
            FlockManager preyMgr = preyGO.AddComponent<FlockManager>();

            var predGO = new GameObject("FlockManager_Predator");
            predGO.SetActive(false);
            FlockManager predMgr = predGO.AddComponent<FlockManager>();

            // Prey: id 0 (registered first). 500 birds, flee predators.
            var preySettings = ScriptableObject.CreateInstance<FlockSettings>();
            preySettings.name = "PreySettings";
            ApplyPreyDefaults(preySettings);
            preyMgr.SetSettings(preySettings);

            // Predator: id 1. 50 birds, chase prey.
            var predSettings = ScriptableObject.CreateInstance<FlockSettings>();
            predSettings.name = "PredatorSettings";
            ApplyPredatorDefaults(predSettings);
            predMgr.SetSettings(predSettings);

            worldGO.SetActive(true);
            preyGO.SetActive(true);
            predGO.SetActive(true);
            yield return null;

            Assert.AreEqual(2, world.RegisteredFlockCount);
            Assert.AreEqual(550, world.TotalBirdCount);

            FlockSlice preySlice = world.Slices[0];
            FlockSlice predSlice = world.Slices[1];
            Assert.AreEqual(500, preySlice.Count);
            Assert.AreEqual(50,  predSlice.Count);

            // ── Drive 120 ticks, sample centroid distance every tick ─────────────────
            const int totalTicks  = 120;
            const int sampleEnd   = 30;   // first window: ticks [0,30)
            const int lateStart   = 90;   // last  window: ticks [90,120)
            const float dt        = 1f / 60f;

            float sumEarly = 0f; int countEarly = 0;
            float sumLate  = 0f; int countLate  = 0;

            for (int frame = 0; frame < totalTicks; frame++)
            {
                world.Tick(dt);

                float d = CentroidDistance(world, preySlice, predSlice);
                if (frame < sampleEnd)              { sumEarly += d; countEarly++; }
                else if (frame >= lateStart)        { sumLate  += d; countLate++; }
            }

            float meanEarly = sumEarly / countEarly;
            float meanLate  = sumLate  / countLate;

            // Sanity — distances finite + positive.
            Assert.IsTrue(meanEarly > 0f && !float.IsNaN(meanEarly) && !float.IsInfinity(meanEarly),
                $"Early-window mean centroid distance invalid: {meanEarly}");
            Assert.IsTrue(meanLate  > 0f && !float.IsNaN(meanLate)  && !float.IsInfinity(meanLate),
                $"Late-window mean centroid distance invalid: {meanLate}");

            // Hero assertion — predators net closer over the run window. Prey scatter
            // (high outSeparation) so we don't demand a huge collapse; just a clear
            // negative trend (≥ 5% reduction). With outCohesionWeight=5 vs prey's
            // outSeparationWeight=5 + the predator's MaxSpeed advantage (12 vs 10),
            // closing trend should be obvious.
            float reduction = (meanEarly - meanLate) / meanEarly;
            Assert.IsTrue(reduction > 0.05f,
                $"Predators failed to close on prey. " +
                $"Early-mean centroid distance = {meanEarly:F2}, " +
                $"Late-mean = {meanLate:F2}, reduction = {reduction*100f:F1}% " +
                $"(expected > 5%).");

            LogAssert.NoUnexpectedReceived();

            Object.DestroyImmediate(predGO);
            Object.DestroyImmediate(preyGO);
            Object.DestroyImmediate(worldGO);
            Object.DestroyImmediate(preySettings);
            Object.DestroyImmediate(predSettings);
        }

        // Centroid is the simple arithmetic mean of every position in the slice.
        // O(n+m) per sample which is fine for 550 birds × 120 ticks.
        private static float CentroidDistance(FlockWorld world, FlockSlice a, FlockSlice b)
        {
            float3 sumA = float3.zero;
            for (int i = 0; i < a.Count; i++) sumA += world.Positions[a.StartIndex + i];
            float3 cA = sumA / a.Count;

            float3 sumB = float3.zero;
            for (int i = 0; i < b.Count; i++) sumB += world.Positions[b.StartIndex + i];
            float3 cB = sumB / b.Count;

            return math.distance(cA, cB);
        }

        // Prey: 500 birds; high out-separation = flee predators; cursor-scatter on for
        // the sandbox demo (off here so the test isolates predator/prey interaction).
        private static void ApplyPreyDefaults(FlockSettings settings)
        {
            const string overlayJson =
                "{" +
                "\"inSeparationWeight\":1.0,\"inAlignmentWeight\":1.0,\"inCohesionWeight\":1.0," +
                "\"outSeparationWeight\":5.0,\"outAlignmentWeight\":0.0,\"outCohesionWeight\":0.0," +
                "\"preferredCenter\":{\"x\":0.0,\"y\":0.0,\"z\":0.0}," +
                "\"preferredExtents\":{\"x\":20.0,\"y\":10.0,\"z\":20.0}," +
                "\"preferredAttractionWeight\":1.0," +
                "\"perceptionRadius\":5.0,\"separationRadius\":1.5," +
                "\"perceptionConeHalfAngleRadians\":2.356194," +
                "\"minSpeed\":1.0,\"maxSpeed\":10.0,\"maxAcceleration\":30.0," +
                "\"cursorReactionStrength\":0.0,\"cursorReactionRadius\":10.0," +
                "\"birdCount\":500,\"randomSeed\":12345" +
                "}";
            JsonUtility.FromJsonOverwrite(overlayJson, settings);
        }

        // Predator: 50 birds; high out-cohesion = chase prey centroid; faster than
        // prey so the closing trend is unambiguous.
        private static void ApplyPredatorDefaults(FlockSettings settings)
        {
            const string overlayJson =
                "{" +
                "\"inSeparationWeight\":1.5,\"inAlignmentWeight\":0.5,\"inCohesionWeight\":0.5," +
                "\"outSeparationWeight\":0.0,\"outAlignmentWeight\":0.0,\"outCohesionWeight\":5.0," +
                "\"preferredCenter\":{\"x\":0.0,\"y\":0.0,\"z\":0.0}," +
                "\"preferredExtents\":{\"x\":20.0,\"y\":10.0,\"z\":20.0}," +
                "\"preferredAttractionWeight\":1.0," +
                "\"perceptionRadius\":8.0,\"separationRadius\":2.0," +
                "\"perceptionConeHalfAngleRadians\":2.356194," +
                "\"minSpeed\":1.5,\"maxSpeed\":12.0,\"maxAcceleration\":35.0," +
                "\"cursorReactionStrength\":0.0,\"cursorReactionRadius\":10.0," +
                "\"birdCount\":50,\"randomSeed\":54321" +
                "}";
            JsonUtility.FromJsonOverwrite(overlayJson, settings);
        }
    }
}
