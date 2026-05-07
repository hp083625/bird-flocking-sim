// GpuFlockSimulation.cs — P1 of the GPU rewrite. Self-contained MonoBehaviour
// that owns every per-bird GraphicsBuffer, dispatches the FlockSteering compute
// pipeline, and hands the matrices buffer to GpuIndirectFlockRenderer.
//
// SCOPE (P1):
//   * Single flock (all birds share one set of steering parameters).
//   * Brute-force O(N²) SteeringPass — no spatial hash yet (lands in P2).
//   * No cursor / cone / cross-flock weights (land in P3).
//   * No GPU frustum cull — CPU stamps the indirect args once (P4 makes it GPU).
//
// The CPU path (FlockWorld + the four steering jobs) is untouched in P1 so the
// existing Flocking_Sandbox scene remains bench-able for A/B comparison.
// Drop this component into a fresh GameObject; it owns one mesh, one material,
// one indirect renderer, and one bird population.

using Bird_behiviour.Flocking.Core;
using Bird_behiviour.Flocking.Rendering;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using Unity.Profiling;

namespace Bird_behiviour.Flocking.Compute
{
    /// <summary>
    /// One-per-scene MonoBehaviour that runs the entire flocking simulation on the
    /// GPU. Owns all <see cref="GraphicsBuffer"/>s, loads
    /// <see cref="FlockSteering"/>.compute, and dispatches the per-tick pipeline.
    /// </summary>
    /// <remarks>
    /// <b>Pipeline (per Tick):</b>
    /// <list type="number">
    ///   <item>Push constants (dt, world bounds, single-flock steering params).</item>
    ///   <item>CSHash — bin every bird into a cell, write (cellId, boidId).</item>
    ///   <item>(P2 will insert: BitonicSort + CellStartPass.)</item>
    ///   <item>CSSteerBruteForce — read every bird, accumulate forces, integrate.</item>
    ///   <item>CSBuildMatrices — write per-bird TRS matrix from updated pos+vel.</item>
    /// </list>
    /// Then <see cref="Graphics.RenderMeshIndirect"/> (issued by
    /// <see cref="GpuIndirectFlockRenderer"/>) reads the matrices buffer directly —
    /// no readback at any point.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class GpuFlockSimulation : MonoBehaviour
    {
        // ── Inspector knobs ──────────────────────────────────────────────────────
        [Header("Population")]
        [SerializeField, Min(1)] private int birdCount = 20_000;
        [SerializeField] private uint randomSeed = 42;

        [Header("World bounds (where birds spawn / where the spatial grid lives)")]
        [SerializeField] private Vector3 worldBoundsCenter  = Vector3.zero;
        [SerializeField] private Vector3 worldBoundsExtents = new Vector3(50f, 25f, 50f);

        [Header("Steering")]
        [SerializeField, Min(0.01f)] private float perceptionRadius = 5f;
        [SerializeField, Min(0.01f)] private float separationRadius = 1.5f;
        [SerializeField, Min(0.01f)] private float minSpeed         = 1f;
        [SerializeField, Min(0.01f)] private float maxSpeed         = 10f;
        [SerializeField, Min(0.01f)] private float maxAcceleration  = 30f;
        [SerializeField] private float separationWeight = 1.5f;
        [SerializeField] private float alignmentWeight  = 1f;
        [SerializeField] private float cohesionWeight   = 1f;

        [Header("Soft preferred zone (gentle pull back into the box)")]
        [SerializeField] private Vector3 preferredCenter           = Vector3.zero;
        [SerializeField] private Vector3 preferredExtents          = new Vector3(20f, 10f, 20f);
        [SerializeField] private float   preferredAttractionWeight = 1f;

        [Header("Time")]
        [SerializeField, Min(1f / 240f)] private float maxSimDt = 1f / 30f;
        [SerializeField, Min(0f)]        private float simSpeedMultiplier = 1f;

        [Header("Rendering")]
        [SerializeField] private Mesh     birdMesh;
        [SerializeField] private Material birdMaterial;

        [Header("Compute shaders (assigned in inspector or auto-loaded)")]
        [SerializeField] private ComputeShader steeringShader;
        [SerializeField] private ComputeShader bitonicShader;
        [Tooltip("If true, the spatial-hash path is bypassed and a brute-force O(N²) kernel is used instead. P1 fallback for debugging — leave OFF for perf.")]
        [SerializeField] private bool useBruteForce = false;
        [Tooltip("If true (default), the steering kernel keeps only the K=8 nearest neighbours per bird via an in-register insertion sort, bounding per-bird cost regardless of cell density (Ballerini et al. 2008). Disable to compare against the metric (all-in-radius) path.")]
        [SerializeField] private bool useTopologicalK = true;

        // ── GPU buffers (Persistent — disposed in OnDisable) ────────────────────
        private GraphicsBuffer boidsBuffer;     // RWStructuredBuffer<Boid> — 48 B / element
        private GraphicsBuffer cellKeysBuffer;  // RWStructuredBuffer<uint2> — 8 B / element, padded to next-pow-2 for bitonic
        private GraphicsBuffer cellStartBuffer; // RWStructuredBuffer<uint>  — 4 B / element, sized to gridDim.x*y*z
        private GraphicsBuffer matricesBuffer;  // RWStructuredBuffer<float4x4> — 64 B / element (consumed by renderer)
        private GraphicsBuffer argsBuffer;      // 1 IndirectDrawIndexedArgs

        // Cached grid sizing — computed in OnEnable from world bounds + perception radius.
        private int paddedKeyCount;
        private int cellTotalCount;
        private uint3 cachedGridDim;

        // ── Renderer + helpers ──────────────────────────────────────────────────
        private GpuIndirectFlockRenderer renderer;
        private GraphicsBuffer.IndirectDrawIndexedArgs[] argsScratch =
            new GraphicsBuffer.IndirectDrawIndexedArgs[1];

        // ── Cached kernel handles ───────────────────────────────────────────────
        private int kHash;
        private int kSteerBruteForce;
        private int kClearCellStart;
        private int kCellStart;
        private int kSteerCellList;
        private int kSteerTopoK;
        private int kBuildMat;

        // ── Cached property ids ─────────────────────────────────────────────────
        private static readonly int IdBoids        = Shader.PropertyToID("_Boids");
        private static readonly int IdCellKeys     = Shader.PropertyToID("_CellKeys");
        private static readonly int IdCellStart    = Shader.PropertyToID("_CellStart");
        private static readonly int IdMatrices     = Shader.PropertyToID("_Matrices");
        private static readonly int IdBirdCount    = Shader.PropertyToID("_BirdCount");
        private static readonly int IdPaddedKey    = Shader.PropertyToID("_PaddedKeyCount");
        private static readonly int IdCellTotal    = Shader.PropertyToID("_CellTotalCount");
        private static readonly int IdDt           = Shader.PropertyToID("_Dt");
        private static readonly int IdWorldOrigin  = Shader.PropertyToID("_WorldOrigin");
        private static readonly int IdCellSize     = Shader.PropertyToID("_CellSize");
        private static readonly int IdGridDim      = Shader.PropertyToID("_GridDim");
        private static readonly int IdPercept      = Shader.PropertyToID("_PerceptionRadius");
        private static readonly int IdSepRadius    = Shader.PropertyToID("_SeparationRadius");
        private static readonly int IdMinSpd       = Shader.PropertyToID("_MinSpeed");
        private static readonly int IdMaxSpd       = Shader.PropertyToID("_MaxSpeed");
        private static readonly int IdMaxAcc       = Shader.PropertyToID("_MaxAcceleration");
        private static readonly int IdSepW         = Shader.PropertyToID("_SepWeight");
        private static readonly int IdAliW         = Shader.PropertyToID("_AliWeight");
        private static readonly int IdCohW         = Shader.PropertyToID("_CohWeight");
        private static readonly int IdPrefCenter   = Shader.PropertyToID("_PreferredCenter");
        private static readonly int IdPrefExtents  = Shader.PropertyToID("_PreferredExtents");
        private static readonly int IdPrefAttract  = Shader.PropertyToID("_PreferredAttractionWeight");

        // ── Profiler markers (read by FlockHUD if present) ──────────────────────
        private static readonly ProfilerMarker MkHash      = new ProfilerMarker("Gpu.Hash");
        private static readonly ProfilerMarker MkSort      = new ProfilerMarker("Gpu.Sort");
        private static readonly ProfilerMarker MkCellStart = new ProfilerMarker("Gpu.CellStart");
        private static readonly ProfilerMarker MkSteer     = new ProfilerMarker("Gpu.Steer");
        private static readonly ProfilerMarker MkMatrices  = new ProfilerMarker("Gpu.Matrices");
        private static readonly ProfilerMarker MkRender    = new ProfilerMarker("Gpu.Render");

        /// <summary>Live bird count — exposed read-only for the HUD.</summary>
        public int BirdCount => birdCount;

        // ── Lifecycle ───────────────────────────────────────────────────────────
        private void OnEnable()
        {
            if (steeringShader == null)
            {
                steeringShader = Resources.Load<ComputeShader>("FlockSteering");
                if (steeringShader == null)
                {
                    // Fallback: AssetDatabase only works in the Editor; in Player builds
                    // the user MUST drop the shader into the inspector slot OR drop a
                    // copy under Assets/Resources/.
#if UNITY_EDITOR
                    steeringShader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(
                        "Assets/Scripts/Flocking/Compute/FlockSteering.compute");
#endif
                }
            }
            if (steeringShader == null)
            {
                Debug.LogError("[GpuFlockSimulation] FlockSteering.compute not found. " +
                               "Assign it in the inspector or drop it under a Resources/ folder.");
                enabled = false;
                return;
            }

            kHash            = steeringShader.FindKernel("CSHash");
            kSteerBruteForce = steeringShader.FindKernel("CSSteerBruteForce");
            kClearCellStart  = steeringShader.FindKernel("CSClearCellStart");
            kCellStart       = steeringShader.FindKernel("CSCellStart");
            kSteerCellList   = steeringShader.FindKernel("CSSteerCellList");
            kSteerTopoK      = steeringShader.FindKernel("CSSteerTopoK");
            kBuildMat        = steeringShader.FindKernel("CSBuildMatrices");

            // BitonicSort.compute is required when useBruteForce==false. Try to load.
            if (bitonicShader == null)
            {
                bitonicShader = Resources.Load<ComputeShader>("BitonicSort");
#if UNITY_EDITOR
                if (bitonicShader == null)
                {
                    bitonicShader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(
                        "Assets/Scripts/Flocking/Compute/BitonicSort.compute");
                }
#endif
            }
            if (bitonicShader == null && !useBruteForce)
            {
                Debug.LogWarning("[GpuFlockSimulation] BitonicSort.compute not found; falling back to brute-force kernel.");
                useBruteForce = true;
            }

            AllocateBuffers();
            SeedBirds();
            BindBuffersToKernels();
            // ORDER MATTERS: EnsureArgsBuffer must come BEFORE EnsureRenderer so the
            // renderer's BindIndirectArgsBuffer call sees a non-null buffer. Otherwise
            // the renderer caches `argsBuffer == null` and every Render() early-outs.
            EnsureArgsBuffer();
            EnsureRenderer();

            // Prefer running while the editor isn't focused (else macOS throttles us).
            Application.runInBackground = true;
        }

        private void OnDisable()
        {
            if (renderer != null)
            {
                renderer.Dispose();
                renderer = null;
            }
            if (boidsBuffer != null)     { boidsBuffer.Dispose();     boidsBuffer     = null; }
            if (cellKeysBuffer != null)  { cellKeysBuffer.Dispose();  cellKeysBuffer  = null; }
            if (cellStartBuffer != null) { cellStartBuffer.Dispose(); cellStartBuffer = null; }
            if (matricesBuffer != null)  { matricesBuffer.Dispose();  matricesBuffer  = null; }
            if (argsBuffer != null)      { argsBuffer.Dispose();      argsBuffer      = null; }
        }

        private void AllocateBuffers()
        {
            // Compute grid sizing once. CellSize tracks PerceptionRadius so the 27-cell
            // walk is a tight superset of the perception sphere.
            float cellSize = math.max(0.01f, perceptionRadius);
            float3 sizeF   = ((float3)worldBoundsExtents) * 2f / cellSize;
            cachedGridDim  = new uint3(
                (uint)math.max(1, (int)math.ceil(sizeF.x)),
                (uint)math.max(1, (int)math.ceil(sizeF.y)),
                (uint)math.max(1, (int)math.ceil(sizeF.z)));
            cellTotalCount = (int)(cachedGridDim.x * cachedGridDim.y * cachedGridDim.z);
            paddedKeyCount = NextPowerOfTwo(birdCount);

            // Boids buffer — one element per bird, 48-byte stride matching BoidGpu.
            boidsBuffer     = new GraphicsBuffer(GraphicsBuffer.Target.Structured, birdCount, BoidGpu.Stride);

            // CellKeys — padded to next-pow-2 so BitonicSort works in place. The padded
            // tail (indices >= birdCount) is filled with 0xFFFFFFFF sentinels by CSHash
            // every frame, so the sort drops them at the end.
            cellKeysBuffer  = new GraphicsBuffer(GraphicsBuffer.Target.Structured, paddedKeyCount, sizeof(uint) * 2);

            // CellStart — one slot per grid cell. Reset each frame by CSClearCellStart.
            cellStartBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cellTotalCount, sizeof(uint));

            // Matrices — one float4x4 per bird.
            matricesBuffer  = new GraphicsBuffer(GraphicsBuffer.Target.Structured, birdCount, sizeof(float) * 16);
        }

        private void EnsureArgsBuffer()
        {
            if (argsBuffer == null)
            {
                argsBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.IndirectArguments,
                    1,
                    GraphicsBuffer.IndirectDrawIndexedArgs.size);
            }
            // Stamp once — P4 will let the GPU FrustumCullPass write this each frame.
            if (birdMesh != null)
            {
                argsScratch[0] = new GraphicsBuffer.IndirectDrawIndexedArgs
                {
                    indexCountPerInstance = birdMesh.GetIndexCount(0),
                    instanceCount         = (uint)birdCount,
                    startIndex            = birdMesh.GetIndexStart(0),
                    baseVertexIndex       = birdMesh.GetBaseVertex(0),
                    startInstance         = 0u,
                };
                argsBuffer.SetData(argsScratch);
            }
        }

        private void EnsureRenderer()
        {
            if (renderer == null)
            {
                renderer = new GpuIndirectFlockRenderer();
            }
            renderer.BindMatricesBuffer(matricesBuffer);
            renderer.BindIndirectArgsBuffer(argsBuffer);
        }

        private void SeedBirds()
        {
            // Initial positions: uniform random inside worldBoundsExtents.
            // Initial velocities: random direction × MinSpeed (so the speed-clamp doesn't
            // immediately re-normalize them). FlockId stays 0 for P1.
            // NativeArray here is the same C# 8 'using-disposable' shape that tripped
            // CS1654 elsewhere in the project — use try/finally so element writes are
            // legal modifications (a using-variable is treated as readonly).
            var rng = new Unity.Mathematics.Random(math.max(1u, randomSeed));
            var initial = new NativeArray<BoidGpu>(birdCount, Allocator.Temp);
            try
            {
                for (int i = 0; i < birdCount; i++)
                {
                    float3 p = new float3(
                        rng.NextFloat(-worldBoundsExtents.x, worldBoundsExtents.x),
                        rng.NextFloat(-worldBoundsExtents.y, worldBoundsExtents.y),
                        rng.NextFloat(-worldBoundsExtents.z, worldBoundsExtents.z))
                        + (float3)worldBoundsCenter;
                    float3 v = math.normalize(rng.NextFloat3Direction()) * minSpeed;
                    initial[i] = new BoidGpu
                    {
                        Pos      = p,
                        FlockId  = 0,
                        Vel      = v,
                        Pad0     = 0f,
                        Reserved = float4.zero,
                    };
                }
                boidsBuffer.SetData(initial);
            }
            finally
            {
                initial.Dispose();
            }
        }

        private void BindBuffersToKernels()
        {
            // Hash: writes (cellId, boidId) for live birds, sentinel for padded tail.
            steeringShader.SetBuffer(kHash, IdBoids,    boidsBuffer);
            steeringShader.SetBuffer(kHash, IdCellKeys, cellKeysBuffer);

            // ClearCellStart: writes sentinel value into _CellStart.
            steeringShader.SetBuffer(kClearCellStart, IdCellStart, cellStartBuffer);

            // CellStart: scans sorted _CellKeys, writes index into _CellStart.
            steeringShader.SetBuffer(kCellStart, IdCellKeys,  cellKeysBuffer);
            steeringShader.SetBuffer(kCellStart, IdCellStart, cellStartBuffer);

            // SteerCellList (P2 metric path): reads everything, writes back to _Boids.
            steeringShader.SetBuffer(kSteerCellList, IdBoids,    boidsBuffer);
            steeringShader.SetBuffer(kSteerCellList, IdCellKeys, cellKeysBuffer);
            steeringShader.SetBuffer(kSteerCellList, IdCellStart, cellStartBuffer);

            // SteerTopoK (P5 topological-K path): same buffers, K-NN inner loop.
            steeringShader.SetBuffer(kSteerTopoK, IdBoids,    boidsBuffer);
            steeringShader.SetBuffer(kSteerTopoK, IdCellKeys, cellKeysBuffer);
            steeringShader.SetBuffer(kSteerTopoK, IdCellStart, cellStartBuffer);

            // SteerBruteForce: P1 fallback path — only reads/writes Boids.
            steeringShader.SetBuffer(kSteerBruteForce, IdBoids, boidsBuffer);

            // BuildMatrices: reads Boids, writes Matrices.
            steeringShader.SetBuffer(kBuildMat, IdBoids,    boidsBuffer);
            steeringShader.SetBuffer(kBuildMat, IdMatrices, matricesBuffer);
        }

        // ── Per-frame ───────────────────────────────────────────────────────────
        private void LateUpdate()
        {
            if (steeringShader == null || boidsBuffer == null) return;

            float dt = math.min(Time.deltaTime, maxSimDt) * simSpeedMultiplier;
            if (dt <= 0f) return;

            // ── Push constants ──────────────────────────────────────────────────
            steeringShader.SetInt   (IdBirdCount,  birdCount);
            steeringShader.SetInt   (IdPaddedKey,  paddedKeyCount);
            steeringShader.SetInt   (IdCellTotal,  cellTotalCount);
            steeringShader.SetFloat (IdDt,         dt);

            float cellSize = math.max(0.01f, perceptionRadius);
            float3 origin  = (float3)worldBoundsCenter - (float3)worldBoundsExtents;
            steeringShader.SetVector(IdWorldOrigin, new Vector4(origin.x, origin.y, origin.z, 0f));
            steeringShader.SetFloat (IdCellSize,    cellSize);
            steeringShader.SetInts  (IdGridDim, (int)cachedGridDim.x, (int)cachedGridDim.y, (int)cachedGridDim.z);

            // Single-flock steering params (P3 will switch to a buffer indexed by flockId).
            steeringShader.SetFloat(IdPercept,     perceptionRadius);
            steeringShader.SetFloat(IdSepRadius,   separationRadius);
            steeringShader.SetFloat(IdMinSpd,      minSpeed);
            steeringShader.SetFloat(IdMaxSpd,      maxSpeed);
            steeringShader.SetFloat(IdMaxAcc,      maxAcceleration);
            steeringShader.SetFloat(IdSepW,        separationWeight);
            steeringShader.SetFloat(IdAliW,        alignmentWeight);
            steeringShader.SetFloat(IdCohW,        cohesionWeight);
            steeringShader.SetVector(IdPrefCenter,  new Vector4(preferredCenter.x, preferredCenter.y, preferredCenter.z, 0f));
            steeringShader.SetVector(IdPrefExtents, new Vector4(preferredExtents.x, preferredExtents.y, preferredExtents.z, 0f));
            steeringShader.SetFloat (IdPrefAttract, preferredAttractionWeight);

            // ── Dispatch ────────────────────────────────────────────────────────
            int groupsBird = (birdCount + 63) / 64;
            int groupsKey  = (paddedKeyCount + 63) / 64;
            int groupsCell = (cellTotalCount + 63) / 64;

            if (useBruteForce)
            {
                // P1 floor-perf path. Skips the spatial-hash chain entirely.
                using (MkSteer.Auto())    { steeringShader.Dispatch(kSteerBruteForce, groupsBird, 1, 1); }
                using (MkMatrices.Auto()) { steeringShader.Dispatch(kBuildMat,        groupsBird, 1, 1); }
            }
            else
            {
                // P2 production path: Hash → Sort → ClearCellStart → CellStart → SteerCellList → BuildMatrices.
                // Each Dispatch call is a global memory barrier on the GPU, so no explicit
                // synchronization is needed between the stages.
                using (MkHash.Auto())      { steeringShader.Dispatch(kHash, groupsKey, 1, 1); }
                using (MkSort.Auto())      { BitonicSort.Sort(bitonicShader, cellKeysBuffer, paddedKeyCount); }
                using (MkCellStart.Auto())
                {
                    steeringShader.Dispatch(kClearCellStart, groupsCell, 1, 1);
                    steeringShader.Dispatch(kCellStart,      groupsKey,  1, 1);
                }
                int kSteer = useTopologicalK ? kSteerTopoK : kSteerCellList;
                using (MkSteer.Auto())    { steeringShader.Dispatch(kSteer,    groupsBird, 1, 1); }
                using (MkMatrices.Auto()) { steeringShader.Dispatch(kBuildMat, groupsBird, 1, 1); }
            }

            // ── Render ──────────────────────────────────────────────────────────
            using (MkRender.Auto())
            {
                if (birdMesh != null && birdMaterial != null && renderer != null)
                {
                    // FlockSlice is an informational pass-through for the renderer; for the
                    // P1 single-flock case it covers the entire population.
                    var slice = new FlockSlice(0, birdCount, 0);
                    renderer.Render(slice, birdMesh, birdMaterial,
                        default, // visibleMatrices ignored — renderer reads buffer directly
                        birdCount,
                        Camera.main);
                }
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────
        private static int NextPowerOfTwo(int n)
        {
            if (n <= 1) return 1;
            n--;
            n |= n >> 1; n |= n >> 2; n |= n >> 4; n |= n >> 8; n |= n >> 16;
            return n + 1;
        }
    }
}
