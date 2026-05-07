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
using UnityEngine.InputSystem;
using UnityEngine.Profiling;
using Unity.Profiling;

namespace Bird_behiviour.Flocking.Compute
{
    /// <summary>
    /// One flock's GPU steering parameters as authored in the inspector. Maps
    /// 1:1 to <see cref="FlockSettingsGpu"/> at upload time.
    /// </summary>
    [System.Serializable]
    public struct FlockGpuConfig
    {
        public string name;
        [Min(1)] public int birdCount;
        public Color color;
        [Min(0.01f)] public float perceptionRadius;
        [Min(0.01f)] public float separationRadius;
        [Range(0f, math.PI)] public float perceptionConeHalfAngleRadians;
        [Min(0.01f)] public float minSpeed;
        [Min(0.01f)] public float maxSpeed;
        [Min(0.01f)] public float maxAcceleration;
        public float inSeparationWeight;
        public float inAlignmentWeight;
        public float inCohesionWeight;
        public float outSeparationWeight;
        public float outAlignmentWeight;
        public float outCohesionWeight;
        public float cursorReactionStrength;
        [Min(0.01f)] public float cursorReactionRadius;
        public Vector3 preferredCenter;
        public Vector3 preferredExtents;
        public float preferredAttractionWeight;
    }

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
        [Tooltip("If non-empty, replaces the legacy single-flock fields below. Each entry is a flock with its own count, color, and steering. Bird count = sum of all entries.")]
        [SerializeField] private FlockGpuConfig[] flocks = System.Array.Empty<FlockGpuConfig>();

        [Tooltip("Used only when 'flocks' is empty (single-flock fallback). Total bird count for the default flock 0.")]
        [SerializeField, Min(1)] private int birdCount = 20_000;
        [SerializeField] private uint randomSeed = 42;

        [Header("World bounds (where birds spawn / where the spatial grid lives)")]
        [SerializeField] private Vector3 worldBoundsCenter  = Vector3.zero;
        [SerializeField] private Vector3 worldBoundsExtents = new Vector3(50f, 25f, 50f);
        [SerializeField, Min(0f)] private float worldBoundsWeight = 8f;

        [Header("Default-flock steering (used when 'flocks' is empty)")]
        [SerializeField, Min(0.01f)] private float perceptionRadius = 5f;
        [SerializeField, Min(0.01f)] private float separationRadius = 1.5f;
        [SerializeField, Range(0f, math.PI)] private float perceptionConeHalfAngleRadians = 2.356194f; // 135°
        [SerializeField, Min(0.01f)] private float minSpeed         = 1f;
        [SerializeField, Min(0.01f)] private float maxSpeed         = 10f;
        [SerializeField, Min(0.01f)] private float maxAcceleration  = 30f;
        [SerializeField] private float separationWeight = 1.5f;
        [SerializeField] private float alignmentWeight  = 1f;
        [SerializeField] private float cohesionWeight   = 1f;
        [SerializeField] private float cursorReactionStrength = 0f;
        [SerializeField, Min(0.01f)] private float cursorReactionRadius = 10f;
        [SerializeField] private Color defaultFlockColor = new Color(0.7f, 0.8f, 1f, 1f);

        [Header("Soft preferred zone (default flock fallback)")]
        [SerializeField] private Vector3 preferredCenter           = Vector3.zero;
        [SerializeField] private Vector3 preferredExtents          = new Vector3(20f, 10f, 20f);
        [SerializeField] private float   preferredAttractionWeight = 1f;

        [Header("Time")]
        [SerializeField, Min(1f / 240f)] private float maxSimDt = 1f / 30f;
        [SerializeField, Min(0f)]        private float simSpeedMultiplier = 1f;

        [Header("Cursor (auto-projects mouse onto Y=cursorPlaneY)")]
        [SerializeField] private bool   cursorEnabled = true;
        [SerializeField] private float  cursorPlaneY  = 0f;

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
        private GraphicsBuffer boidsBuffer;          // RWStructuredBuffer<Boid> — 48 B / element
        private GraphicsBuffer cellKeysBuffer;       // RWStructuredBuffer<uint2> — 8 B / element, padded to next-pow-2 for bitonic
        private GraphicsBuffer cellStartBuffer;      // RWStructuredBuffer<uint>  — 4 B / element, sized to gridDim.x*y*z
        private GraphicsBuffer matricesBuffer;       // RWStructuredBuffer<float4x4> — 64 B / element (consumed by renderer)
        private GraphicsBuffer flockSettingsBuffer;  // StructuredBuffer<FlockKernelSettings> — 96 B / element, sized to flock count
        private GraphicsBuffer instanceFlockIdsBuffer; // StructuredBuffer<uint> — 4 B / bird, written once at init for shader tinting
        private GraphicsBuffer argsBuffer;           // 1 IndirectDrawIndexedArgs

        // Cached grid sizing — computed in OnEnable from world bounds + perception radius.
        private int paddedKeyCount;
        private int cellTotalCount;
        private uint3 cachedGridDim;

        // Resolved per-flock configs (== `flocks` if non-empty, else a single
        // synthesized entry from the legacy fallback fields).
        private FlockGpuConfig[] resolvedFlocks;
        private int resolvedTotalBirdCount;
        // Max perception radius across all flocks — drives cell size.
        private float resolvedMaxPerception;
        // RGBA palette (up to 8 flocks) pushed to the shader as _FlockColors[8].
        private Vector4[] flockColorPalette = new Vector4[8];

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
        private static readonly int IdBoids         = Shader.PropertyToID("_Boids");
        private static readonly int IdCellKeys      = Shader.PropertyToID("_CellKeys");
        private static readonly int IdCellStart     = Shader.PropertyToID("_CellStart");
        private static readonly int IdMatrices      = Shader.PropertyToID("_Matrices");
        private static readonly int IdFlockSettings = Shader.PropertyToID("_FlockSettings");
        private static readonly int IdBirdCount     = Shader.PropertyToID("_BirdCount");
        private static readonly int IdPaddedKey     = Shader.PropertyToID("_PaddedKeyCount");
        private static readonly int IdCellTotal     = Shader.PropertyToID("_CellTotalCount");
        private static readonly int IdDt            = Shader.PropertyToID("_Dt");
        private static readonly int IdWorldOrigin   = Shader.PropertyToID("_WorldOrigin");
        private static readonly int IdCellSize      = Shader.PropertyToID("_CellSize");
        private static readonly int IdGridDim       = Shader.PropertyToID("_GridDim");
        private static readonly int IdWBoundsCenter = Shader.PropertyToID("_WorldBoundsCenter");
        private static readonly int IdWBoundsExtents= Shader.PropertyToID("_WorldBoundsExtents");
        private static readonly int IdWBoundsWeight = Shader.PropertyToID("_WorldBoundsWeight");
        private static readonly int IdWBoundsMargin = Shader.PropertyToID("_WorldBoundsMargin");
        private static readonly int IdCursorPoint   = Shader.PropertyToID("_CursorWorldPoint");
        private static readonly int IdCursorOnScr   = Shader.PropertyToID("_CursorOnScreen");
        // Material-side (shader read).
        private static readonly int IdInstanceFlockIds = Shader.PropertyToID("_InstanceFlockIds");
        private static readonly int IdFlockColors      = Shader.PropertyToID("_FlockColors");
        private static readonly int IdUsePerFlockColor = Shader.PropertyToID("_UsePerFlockColor");
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

        /// <summary>Live total bird count (sum of all configured flocks).</summary>
        public int BirdCount => resolvedTotalBirdCount > 0 ? resolvedTotalBirdCount : birdCount;

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

            // Resolve flock list — empty list means "use the legacy single-flock fields".
            ResolveFlockConfigs();

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
            UploadFlockSettings();
            BindBuffersToKernels();
            // ORDER MATTERS: EnsureArgsBuffer must come BEFORE EnsureRenderer so the
            // renderer's BindIndirectArgsBuffer call sees a non-null buffer. Otherwise
            // the renderer caches `argsBuffer == null` and every Render() early-outs.
            EnsureArgsBuffer();
            EnsureRenderer();
            // P3: push the per-flock palette + InstanceFlockIds buffer to the cloned
            // material so the vertex shader can tint per bird.
            ApplyShaderFlockPalette();

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
            if (boidsBuffer != null)            { boidsBuffer.Dispose();            boidsBuffer            = null; }
            if (cellKeysBuffer != null)         { cellKeysBuffer.Dispose();         cellKeysBuffer         = null; }
            if (cellStartBuffer != null)        { cellStartBuffer.Dispose();        cellStartBuffer        = null; }
            if (matricesBuffer != null)         { matricesBuffer.Dispose();         matricesBuffer         = null; }
            if (flockSettingsBuffer != null)    { flockSettingsBuffer.Dispose();    flockSettingsBuffer    = null; }
            if (instanceFlockIdsBuffer != null) { instanceFlockIdsBuffer.Dispose(); instanceFlockIdsBuffer = null; }
            if (argsBuffer != null)             { argsBuffer.Dispose();             argsBuffer             = null; }
        }

        private void AllocateBuffers()
        {
            int total = resolvedTotalBirdCount;
            // Cell size tracks the LARGEST perception radius across registered flocks
            // so the 27-cell walk is a tight superset of every flock's perception sphere.
            float cellSize = math.max(0.01f, resolvedMaxPerception);
            float3 sizeF   = ((float3)worldBoundsExtents) * 2f / cellSize;
            cachedGridDim  = new uint3(
                (uint)math.max(1, (int)math.ceil(sizeF.x)),
                (uint)math.max(1, (int)math.ceil(sizeF.y)),
                (uint)math.max(1, (int)math.ceil(sizeF.z)));
            cellTotalCount = (int)(cachedGridDim.x * cachedGridDim.y * cachedGridDim.z);
            paddedKeyCount = NextPowerOfTwo(total);

            boidsBuffer            = new GraphicsBuffer(GraphicsBuffer.Target.Structured, total, BoidGpu.Stride);
            cellKeysBuffer         = new GraphicsBuffer(GraphicsBuffer.Target.Structured, paddedKeyCount, sizeof(uint) * 2);
            cellStartBuffer        = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cellTotalCount, sizeof(uint));
            matricesBuffer         = new GraphicsBuffer(GraphicsBuffer.Target.Structured, total, sizeof(float) * 16);
            flockSettingsBuffer    = new GraphicsBuffer(GraphicsBuffer.Target.Structured, resolvedFlocks.Length, FlockSettingsGpu.Stride);
            instanceFlockIdsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, total, sizeof(uint));
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
                    instanceCount         = (uint)resolvedTotalBirdCount,
                    startIndex            = birdMesh.GetIndexStart(0),
                    baseVertexIndex       = birdMesh.GetBaseVertex(0),
                    startInstance         = 0u,
                };
                argsBuffer.SetData(argsScratch);
            }
        }

        /// <summary>
        /// Live-tuning hot path. Mirrors <see cref="ResolveFlockConfigs"/> but only
        /// refreshes the *non-structural* fields (color, weights, radii, speeds,
        /// cursor params, preferred zone) — leaves <c>resolvedTotalBirdCount</c>,
        /// the buffer sizes, and the spawned bird positions alone. Structural
        /// changes (birdCount per flock, flock count) still require a Restart Sim.
        /// </summary>
        private void ResolveFlockSettingsLive()
        {
            if (resolvedFlocks == null || resolvedFlocks.Length == 0) return;

            if (flocks != null && flocks.Length > 0)
            {
                int n = math.min(resolvedFlocks.Length, flocks.Length);
                for (int f = 0; f < n; f++)
                {
                    // Preserve the originally-spawned birdCount so structural changes
                    // don't take effect mid-run; everything else is live.
                    int spawnedCount = resolvedFlocks[f].birdCount;
                    var src = flocks[f];
                    src.birdCount = spawnedCount;
                    resolvedFlocks[f] = src;
                }
            }
            else
            {
                // Legacy single-flock fallback path — refresh the synthesised entry's
                // tunables from the inspector fields each frame.
                int spawnedCount = resolvedFlocks[0].birdCount;
                resolvedFlocks[0] = new FlockGpuConfig
                {
                    name                            = resolvedFlocks[0].name,
                    birdCount                       = spawnedCount,
                    color                           = defaultFlockColor,
                    perceptionRadius                = perceptionRadius,
                    separationRadius                = separationRadius,
                    perceptionConeHalfAngleRadians  = perceptionConeHalfAngleRadians,
                    minSpeed                        = minSpeed,
                    maxSpeed                        = maxSpeed,
                    maxAcceleration                 = maxAcceleration,
                    inSeparationWeight              = separationWeight,
                    inAlignmentWeight               = alignmentWeight,
                    inCohesionWeight                = cohesionWeight,
                    outSeparationWeight             = separationWeight,
                    outAlignmentWeight              = 0f,
                    outCohesionWeight               = 0f,
                    cursorReactionStrength          = cursorReactionStrength,
                    cursorReactionRadius            = cursorReactionRadius,
                    preferredCenter                 = preferredCenter,
                    preferredExtents                = preferredExtents,
                    preferredAttractionWeight       = preferredAttractionWeight,
                };
            }

            // Refresh the palette so per-flock color tweaks land each frame.
            for (int f = 0; f < flockColorPalette.Length; f++)
            {
                int src = math.min(f, resolvedFlocks.Length - 1);
                Color c = resolvedFlocks[src].color;
                if (c.a <= 0f) c.a = 1f;
                flockColorPalette[f] = new Vector4(c.r, c.g, c.b, c.a);
            }
            // Re-bind palette to the renderer's cloned material so live color
            // changes take effect without a Restart.
            if (renderer != null && birdMaterial != null && instanceFlockIdsBuffer != null)
            {
                renderer.BindShaderFlockData(birdMaterial, instanceFlockIdsBuffer, flockColorPalette);
            }
        }

        /// <summary>
        /// Tears the GPU pipeline down + re-runs OnEnable. Triggered by the
        /// "Restart Sim" context-menu item on the GpuFlockSimulation component.
        /// Use this after a structural inspector change (birdCount per flock,
        /// adding/removing a flock entry).
        /// </summary>
        [ContextMenu("Restart Sim")]
        public void RestartSim()
        {
            if (!isActiveAndEnabled) return;
            OnDisable();
            OnEnable();
        }

        // Resolves the inspector-authored flock list. If `flocks` is empty (the
        // common single-flock case), synthesizes one entry from the legacy fallback
        // fields. Also computes resolvedTotalBirdCount and the per-flock palette.
        private void ResolveFlockConfigs()
        {
            if (flocks == null || flocks.Length == 0)
            {
                resolvedFlocks = new[]
                {
                    new FlockGpuConfig
                    {
                        name                            = "DefaultFlock",
                        birdCount                       = birdCount,
                        color                           = defaultFlockColor,
                        perceptionRadius                = perceptionRadius,
                        separationRadius                = separationRadius,
                        perceptionConeHalfAngleRadians  = perceptionConeHalfAngleRadians,
                        minSpeed                        = minSpeed,
                        maxSpeed                        = maxSpeed,
                        maxAcceleration                 = maxAcceleration,
                        inSeparationWeight              = separationWeight,
                        inAlignmentWeight               = alignmentWeight,
                        inCohesionWeight                = cohesionWeight,
                        outSeparationWeight             = separationWeight,
                        outAlignmentWeight              = 0f,
                        outCohesionWeight               = 0f,
                        cursorReactionStrength          = cursorReactionStrength,
                        cursorReactionRadius            = cursorReactionRadius,
                        preferredCenter                 = preferredCenter,
                        preferredExtents                = preferredExtents,
                        preferredAttractionWeight       = preferredAttractionWeight,
                    },
                };
            }
            else
            {
                resolvedFlocks = flocks;
            }

            int total = 0;
            float maxP = 0f;
            for (int f = 0; f < resolvedFlocks.Length; f++)
            {
                total += math.max(1, resolvedFlocks[f].birdCount);
                maxP   = math.max(maxP, resolvedFlocks[f].perceptionRadius);
            }
            resolvedTotalBirdCount = total;
            resolvedMaxPerception  = math.max(0.01f, maxP);

            // Palette for the shader. Up to 8 flocks rendered with distinct colors;
            // overflow flocks all reuse slot 7 (intentional — keeps the shader array
            // tiny; if you need >8, push them as a StructuredBuffer instead).
            for (int f = 0; f < flockColorPalette.Length; f++)
            {
                int src = math.min(f, resolvedFlocks.Length - 1);
                Color c = resolvedFlocks[src].color;
                if (c.a <= 0f) c.a = 1f;
                flockColorPalette[f] = new Vector4(c.r, c.g, c.b, c.a);
            }
        }

        // Uploads the resolved per-flock settings to the GPU. Called once at init
        // and again whenever the user re-applies inspector changes (P3a doesn't
        // expose a runtime re-apply; future inspector work will).
        private void UploadFlockSettings()
        {
            var arr = new NativeArray<FlockSettingsGpu>(resolvedFlocks.Length, Allocator.Temp);
            try
            {
                for (int f = 0; f < resolvedFlocks.Length; f++)
                {
                    var c = resolvedFlocks[f];
                    arr[f] = new FlockSettingsGpu
                    {
                        Color                     = new float3(c.color.r, c.color.g, c.color.b),
                        PerceptionRadius          = c.perceptionRadius,
                        SeparationRadius          = c.separationRadius,
                        PerceptionConeCos         = math.cos(c.perceptionConeHalfAngleRadians),
                        MinSpeed                  = c.minSpeed,
                        MaxSpeed                  = c.maxSpeed,
                        MaxAcceleration           = c.maxAcceleration,
                        InSeparationWeight        = c.inSeparationWeight,
                        InAlignmentWeight         = c.inAlignmentWeight,
                        InCohesionWeight          = c.inCohesionWeight,
                        OutSeparationWeight       = c.outSeparationWeight,
                        OutAlignmentWeight        = c.outAlignmentWeight,
                        OutCohesionWeight         = c.outCohesionWeight,
                        CursorReactionStrength    = c.cursorReactionStrength,
                        CursorReactionRadius      = c.cursorReactionRadius,
                        PreferredCenter           = (float3)c.preferredCenter,
                        PreferredAttractionWeight = c.preferredAttractionWeight,
                        PreferredExtents          = (float3)c.preferredExtents,
                    };
                }
                flockSettingsBuffer.SetData(arr);
            }
            finally { arr.Dispose(); }
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

        // P3: pushes the per-flock RGBA palette + the InstanceFlockIds buffer to the
        // shader's cloned material via the renderer. Called once after the renderer is
        // initialised, so the cloned material reference exists. The renderer doesn't
        // expose its cloned material directly (intentional encapsulation), so we ask
        // it to do the bind on our behalf via two new methods on the renderer.
        private void ApplyShaderFlockPalette()
        {
            if (renderer == null || birdMaterial == null) return;
            // Force a Render call's clone path by issuing a no-op render pre-load.
            // Cleaner: have the renderer expose Bind*Material methods directly.
            renderer.BindShaderFlockData(birdMaterial, instanceFlockIdsBuffer, flockColorPalette);
        }

        private void SeedBirds()
        {
            var rng = new Unity.Mathematics.Random(math.max(1u, randomSeed));
            int total = resolvedTotalBirdCount;
            var initial = new NativeArray<BoidGpu>(total, Allocator.Temp);
            var flockIds = new NativeArray<uint>(total, Allocator.Temp);
            try
            {
                int cursorIdx = 0;
                for (int f = 0; f < resolvedFlocks.Length; f++)
                {
                    var c = resolvedFlocks[f];
                    int n = math.max(1, c.birdCount);
                    Vector3 ext = c.preferredExtents.sqrMagnitude > 1e-6f
                        ? c.preferredExtents
                        : worldBoundsExtents;
                    Vector3 ctr = c.preferredCenter;
                    for (int i = 0; i < n; i++)
                    {
                        float3 p = new float3(
                            rng.NextFloat(-ext.x, ext.x),
                            rng.NextFloat(-ext.y, ext.y),
                            rng.NextFloat(-ext.z, ext.z))
                            + (float3)ctr;
                        float3 v = math.normalize(rng.NextFloat3Direction()) * c.minSpeed;
                        initial[cursorIdx] = new BoidGpu
                        {
                            Pos      = p,
                            FlockId  = (uint)f,
                            Vel      = v,
                            Pad0     = 0f,
                            Reserved = float4.zero,
                        };
                        flockIds[cursorIdx] = (uint)f;
                        cursorIdx++;
                    }
                }
                boidsBuffer.SetData(initial);
                instanceFlockIdsBuffer.SetData(flockIds);
            }
            finally
            {
                initial.Dispose();
                flockIds.Dispose();
            }
        }

        private void BindBuffersToKernels()
        {
            steeringShader.SetBuffer(kHash, IdBoids,    boidsBuffer);
            steeringShader.SetBuffer(kHash, IdCellKeys, cellKeysBuffer);

            steeringShader.SetBuffer(kClearCellStart, IdCellStart, cellStartBuffer);

            steeringShader.SetBuffer(kCellStart, IdCellKeys,  cellKeysBuffer);
            steeringShader.SetBuffer(kCellStart, IdCellStart, cellStartBuffer);

            steeringShader.SetBuffer(kSteerCellList, IdBoids,         boidsBuffer);
            steeringShader.SetBuffer(kSteerCellList, IdCellKeys,      cellKeysBuffer);
            steeringShader.SetBuffer(kSteerCellList, IdCellStart,     cellStartBuffer);

            // P3: topo-K kernel reads the per-flock settings buffer.
            steeringShader.SetBuffer(kSteerTopoK, IdBoids,         boidsBuffer);
            steeringShader.SetBuffer(kSteerTopoK, IdCellKeys,      cellKeysBuffer);
            steeringShader.SetBuffer(kSteerTopoK, IdCellStart,     cellStartBuffer);
            steeringShader.SetBuffer(kSteerTopoK, IdFlockSettings, flockSettingsBuffer);

            steeringShader.SetBuffer(kSteerBruteForce, IdBoids, boidsBuffer);

            steeringShader.SetBuffer(kBuildMat, IdBoids,    boidsBuffer);
            steeringShader.SetBuffer(kBuildMat, IdMatrices, matricesBuffer);
        }

        // ── Per-frame ───────────────────────────────────────────────────────────
        private void LateUpdate()
        {
            if (steeringShader == null || boidsBuffer == null) return;

            float dt = math.min(Time.deltaTime, maxSimDt) * simSpeedMultiplier;
            if (dt <= 0f) return;

            // P3 live-tuning: re-read the inspector's flock configs + re-upload
            // every frame so weights/colors/cursor/etc respond to slider drags
            // without a Restart. This is cheap — flockSettingsBuffer is at most
            // 256 entries × 96 bytes = 24 KB. Structural changes (birdCount per
            // flock, flock count) still require Restart Sim — see the context-
            // menu button below.
            ResolveFlockSettingsLive();
            UploadFlockSettings();

            int total = resolvedTotalBirdCount;

            // ── Push constants ──────────────────────────────────────────────────
            steeringShader.SetInt   (IdBirdCount,  total);
            steeringShader.SetInt   (IdPaddedKey,  paddedKeyCount);
            steeringShader.SetInt   (IdCellTotal,  cellTotalCount);
            steeringShader.SetFloat (IdDt,         dt);

            float cellSize = math.max(0.01f, resolvedMaxPerception);
            float3 origin  = (float3)worldBoundsCenter - (float3)worldBoundsExtents;
            steeringShader.SetVector(IdWorldOrigin, new Vector4(origin.x, origin.y, origin.z, 0f));
            steeringShader.SetFloat (IdCellSize,    cellSize);
            steeringShader.SetInts  (IdGridDim, (int)cachedGridDim.x, (int)cachedGridDim.y, (int)cachedGridDim.z);

            // Legacy single-flock constants — only the BruteForce + CellList kernels
            // read these. TopoK pulls from _FlockSettings instead.
            steeringShader.SetFloat (IdPercept,    perceptionRadius);
            steeringShader.SetFloat (IdSepRadius,  separationRadius);
            steeringShader.SetFloat (IdMinSpd,     minSpeed);
            steeringShader.SetFloat (IdMaxSpd,     maxSpeed);
            steeringShader.SetFloat (IdMaxAcc,     maxAcceleration);
            steeringShader.SetFloat (IdSepW,       separationWeight);
            steeringShader.SetFloat (IdAliW,       alignmentWeight);
            steeringShader.SetFloat (IdCohW,       cohesionWeight);
            steeringShader.SetVector(IdPrefCenter,  new Vector4(preferredCenter.x, preferredCenter.y, preferredCenter.z, 0f));
            steeringShader.SetVector(IdPrefExtents, new Vector4(preferredExtents.x, preferredExtents.y, preferredExtents.z, 0f));
            steeringShader.SetFloat (IdPrefAttract, preferredAttractionWeight);

            // P3 world-scoped constants — hard bounds + cursor.
            steeringShader.SetVector(IdWBoundsCenter,  new Vector4(worldBoundsCenter.x, worldBoundsCenter.y, worldBoundsCenter.z, 0f));
            steeringShader.SetVector(IdWBoundsExtents, new Vector4(worldBoundsExtents.x, worldBoundsExtents.y, worldBoundsExtents.z, 0f));
            steeringShader.SetFloat (IdWBoundsWeight,  worldBoundsWeight);
            // Margin = 5% of the smallest extent (matches CPU NaiveSteering).
            float wMargin = math.cmin((float3)worldBoundsExtents) * 0.05f;
            steeringShader.SetFloat (IdWBoundsMargin,  wMargin);

            // Cursor — mouse projected onto Y=cursorPlaneY.
            float3 cursorWP; bool onScreen;
            ComputeCursorWorldPoint(out cursorWP, out onScreen);
            steeringShader.SetVector(IdCursorPoint, new Vector4(cursorWP.x, cursorWP.y, cursorWP.z, 0f));
            steeringShader.SetInt   (IdCursorOnScr, onScreen ? 1 : 0);

            // ── Dispatch ────────────────────────────────────────────────────────
            int groupsBird = (total + 63) / 64;
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
                    var slice = new FlockSlice(0, total, 0);
                    renderer.Render(slice, birdMesh, birdMaterial,
                        default, total, Camera.main);
                }
            }
        }

        // Project the system mouse pointer onto the horizontal plane at y=cursorPlaneY.
        // Falls back to (offscreen, world-origin) when no main camera or cursor disabled.
        private void ComputeCursorWorldPoint(out float3 worldPoint, out bool onScreen)
        {
            worldPoint = float3.zero;
            onScreen = false;
            if (!cursorEnabled) return;
            Camera cam = Camera.main;
            if (cam == null) return;
            // Project uses the new Input System exclusively (project setting); the
            // legacy Input.mousePosition throws here. Mouse.current is null when no
            // physical mouse is attached (CI / build farms) — degrade silently.
            if (Mouse.current == null) return;
            Vector2 mp2 = Mouse.current.position.ReadValue();
            Vector3 mp = new Vector3(mp2.x, mp2.y, 0f);
            // Editor quirk: Mouse.current.position can report values outside
            // [0..Screen.width/height] when the cursor is over a different editor
            // panel. Project the ray anyway — distant projected points naturally
            // fall outside cursorReactionRadius so no birds react. Only reject the
            // edge case where the ray points away from the plane (would require
            // negative t).
            Ray r = cam.ScreenPointToRay(mp);
            if (math.abs(r.direction.y) < 1e-5f) return;
            float t = (cursorPlaneY - r.origin.y) / r.direction.y;
            if (t < 0f) return;
            Vector3 wp = r.origin + r.direction * t;
            worldPoint = (float3)wp;
            onScreen = true;
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
