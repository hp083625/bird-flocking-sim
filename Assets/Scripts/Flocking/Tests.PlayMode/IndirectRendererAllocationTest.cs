// IndirectRendererAllocationTest.cs — Slice 9 PlayMode test that exercises the
// IndirectFlockRenderer directly (without going through FlockWorld.Tick) and
// asserts it has zero per-frame managed-heap growth after warm-up.
//
// The existing AllocationRegressionTest covers FlockWorld.Tick at 1000 birds
// but does NOT exercise the renderer because the test FlockSettings has no
// BirdMaterial assigned (DispatchRendering skips flocks without a material).
// This test fills that gap: spawn a procedural mesh + a Material directly in
// memory, call renderer.Render() in a tight loop, and verify the Mono heap
// stays flat.
//
// Test recipe:
//   1. Build a procedural cone mesh and an off-the-shelf Lit material.
//   2. Allocate a NativeArray<float4x4>(visibleCount) once.
//   3. Call Render() 5 times to warm up GraphicsBuffer creation, the cloned-
//      material setup, and the first SetData uploads.
//   4. Snapshot Profiler.GetMonoUsedSizeLong().
//   5. Call Render() 60 more times.
//   6. Assert the heap delta is ≤ 1024 bytes.
//
// Diagnostic guidance on failure (in priority order):
//   - IndirectFlockRenderer.EnsureClonedMaterial: did someone add a string
//     concat / boxing path that re-runs every frame?
//   - The cached argsScratch[1] array — confirm it's allocated in the field
//     initialiser, not Render().
//   - Material.SetColor / SetBuffer: should only fire when the source material
//     reference changes; ensure the cache short-circuit still holds.
//   - GraphicsBuffer.SetData: NativeArray overload is unmanaged; if someone
//     swapped to a managed-array overload we'd see allocs here.

using System.Collections;
using Bird_behiviour.Flocking.Core;
using Bird_behiviour.Flocking.Rendering;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.TestTools;

namespace Bird_behiviour.Flocking.Tests.PlayMode
{
    /// <summary>
    /// Slice 9 acceptance test: confirms <see cref="IndirectFlockRenderer"/> has
    /// zero managed-heap growth on its render hot path. Calls
    /// <see cref="IFlockRenderer.Render"/> 60 times after a 5-frame warm-up and
    /// asserts the Mono heap delta is ≤ 1 KB.
    /// </summary>
    public sealed class IndirectRendererAllocationTest
    {
        private const int VisibleCount = 1000;

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
        public IEnumerator Render_60Frames_DoesNotGrowManagedHeap()
        {
            // ── Set up a mesh + source material the renderer can clone ──────────────
            Mesh mesh = ProceduralBirdMesh.Build();
            mesh.hideFlags = HideFlags.HideAndDontSave;

            Shader litShader = Shader.Find("Universal Render Pipeline/Lit");
            if (litShader == null)
            {
                // Fall back to the default shader so the test still validates the
                // alloc path even on a pipeline without URP wired in. The cloned
                // material code path swaps to FlockInstancedURP regardless of source.
                litShader = Shader.Find("Standard");
            }
            Assert.IsNotNull(litShader, "Need *some* shader on the source material to drive the cloned-material path.");

            var sourceMaterial = new Material(litShader)
            {
                name = "IndirectRendererAllocTestSource",
                hideFlags = HideFlags.HideAndDontSave,
            };
            if (sourceMaterial.HasProperty("_BaseColor"))
            {
                sourceMaterial.SetColor("_BaseColor", new Color(0.5f, 0.7f, 0.2f, 1f));
            }

            // ── Pre-populate a deterministic visible-matrix array. The renderer does
            //    not write to it; it's reused across all 65 calls below. ─────────────
            var matrices = new NativeArray<float4x4>(VisibleCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            try
            {
                for (int i = 0; i < VisibleCount; i++)
                {
                    float t = i * 0.1f;
                    matrices[i] = float4x4.TRS(new float3(t, 0f, 0f), quaternion.identity, new float3(1f, 1f, 1f));
                }

                // ── Build the renderer + a fake slice (renderer ignores its content). ─
                var renderer = new IndirectFlockRenderer();
                var slice = new FlockSlice(0, VisibleCount, 0);

                // Camera optional — null is acceptable to RenderMeshIndirect (it falls
                // back to default rendering camera). Tests don't have a real
                // MainCamera and we don't want to set one up just for this check.
                Camera camera = Camera.main;

                try
                {
                    // ── Warm-up: the first Render() allocates the GraphicsBuffer pool
                    //    and the cloned material; subsequent calls should be allocation-
                    //    free. 5 calls is enough to also warm up shader-keyword paths. ─
                    for (int i = 0; i < 5; i++)
                    {
                        renderer.Render(slice, mesh, sourceMaterial, matrices, VisibleCount, camera);
                        // Yield each warm-up frame so the SRP has a chance to actually
                        // process the indirect dispatch (not strictly required for
                        // the alloc check but mirrors the real per-frame cadence).
                        yield return null;
                    }

                    System.GC.Collect();
                    System.GC.WaitForPendingFinalizers();
                    System.GC.Collect();

                    long before = Profiler.GetMonoUsedSizeLong();

                    // ── Steady-state: 60 ticks, no managed allocation expected. ─────
                    for (int i = 0; i < 60; i++)
                    {
                        renderer.Render(slice, mesh, sourceMaterial, matrices, VisibleCount, camera);
                    }

                    long after = Profiler.GetMonoUsedSizeLong();
                    long delta = after - before;

                    LogAssert.NoUnexpectedReceived();

                    const long Threshold = 1024L;
                    Assert.That(delta, Is.LessThanOrEqualTo(Threshold),
                        $"IndirectFlockRenderer leaked {delta} bytes of managed heap over 60 Render() calls " +
                        $"at {VisibleCount} instances (threshold {Threshold} B).\n" +
                        "Likely culprits: per-frame Material.SetColor / Shader.Find / managed-array SetData / new {} struct outside cache.");
                }
                finally
                {
                    renderer.Dispose();
                }
            }
            finally
            {
                if (matrices.IsCreated) matrices.Dispose();
                if (sourceMaterial != null) Object.DestroyImmediate(sourceMaterial);
                if (mesh != null) Object.DestroyImmediate(mesh);
            }
        }
    }
}
