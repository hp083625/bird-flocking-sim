// FlockSettingsHotReloadTests.cs — Slice 10 (M5-2 + M6) attribution tests.
// Verify that the [FlockTunable] vs [FlockStructural] partitioning routes correctly:
//   1. Editing a tunable field does NOT trigger the Apply pipeline (no rebuild).
//   2. Staging a structural field + calling the Apply code path triggers the
//      pipeline exactly once.
//
// These are pure EditMode tests — no scene, no FlockManager instances. The Apply
// pipeline broadcasts via FlockSettingsInspector.StructuralChangesApplied, which
// the tests use as the rebuild-attribution counter (per the team-lead's hint:
// "easiest: stub + a counter incremented in Rebuild"). This keeps the test free
// of the Graphics-dependent InstancedFlockRenderer path FlockManager.Rebuild()
// would otherwise drag in.

using System;
using Bird_behiviour.Flocking.Editor;
using Bird_behiviour.Flocking.Tooling;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Bird_behiviour.Flocking.Tests.EditMode
{
    [TestFixture]
    public sealed class FlockSettingsHotReloadTests
    {
        private FlockSettings settings;
        private int applyEventCount;
        private Action<FlockSettings, int> applyHandler;

        [SetUp]
        public void SetUp()
        {
            settings = ScriptableObject.CreateInstance<FlockSettings>();
            settings.name = "Test_FlockSettings";

            applyEventCount = 0;
            applyHandler = (s, _) => { applyEventCount++; };
            FlockSettingsInspector.StructuralChangesApplied += applyHandler;
        }

        [TearDown]
        public void TearDown()
        {
            if (applyHandler != null)
            {
                FlockSettingsInspector.StructuralChangesApplied -= applyHandler;
                applyHandler = null;
            }
            if (settings != null)
            {
                UnityEngine.Object.DestroyImmediate(settings);
                settings = null;
            }
        }

        [Test]
        public void EditingTunableField_DoesNotTriggerApplyPipeline()
        {
            // [FlockTunable] field edit goes straight to the asset — no Apply step.
            using (var so = new SerializedObject(settings))
            {
                SerializedProperty p = so.FindProperty("inSeparationWeight");
                Assert.IsNotNull(p, "inSeparationWeight serialized property must exist");

                float original = p.floatValue;
                p.floatValue = original + 2.5f;
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            // The asset value reflects the edit immediately…
            Assert.AreEqual(3.5f, settings.InSeparationWeight, 1e-4f,
                "Tunable edit should write through to the asset on ApplyModifiedProperties");

            // …but no rebuild was attributed.
            Assert.AreEqual(0, applyEventCount,
                "Editing a [FlockTunable] field must not trigger the Apply pipeline (rebuild counter must stay at 0)");
        }

        [Test]
        public void StagingStructuralFieldThenApply_TriggersApplyPipelineExactlyOnce()
        {
            // Stage a [FlockStructural] field change. SerializedObject.ApplyModifiedProperties
            // commits the value to the asset, but per the slice-10 contract the *Rebuild* still
            // hasn't fired — that only happens when the inspector's "Apply Structural Changes"
            // button (or the static ApplyStructuralChanges helper) runs.
            using (var so = new SerializedObject(settings))
            {
                SerializedProperty p = so.FindProperty("birdCount");
                Assert.IsNotNull(p, "birdCount serialized property must exist");

                int original = p.intValue;
                p.intValue = original + 50;
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            // Pre-apply: still zero rebuild attributions even though the asset value changed.
            Assert.AreEqual(0, applyEventCount,
                "Staging a structural field (without Apply) must not trigger the rebuild pipeline");
            Assert.AreEqual(150, settings.BirdCount,
                "Asset value must reflect the staged change (default birdCount = 100, +50 = 150)");

            // Trigger the apply pipeline (this is what the inspector's button does).
            FlockSettingsInspector.ApplyStructuralChanges(settings);

            // Exactly one apply event for one Apply call. No scene managers reference the
            // settings asset, so the broadcast carries rebuiltCount=0 — but the *pipeline*
            // ran, which is what the slice-10 contract requires.
            Assert.AreEqual(1, applyEventCount,
                "Calling ApplyStructuralChanges must trigger the rebuild pipeline exactly once");
        }
    }
}
