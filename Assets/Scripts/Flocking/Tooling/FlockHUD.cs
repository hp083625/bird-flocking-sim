// FlockHUD.cs — Slice 11 (M5-5) on-screen IMGUI debug overlay.
// Polls FlockWorld every frame and renders a small top-left text block showing FPS,
// total/per-flock bird counts, mean ms per profiler marker (BuildGrid / Cull /
// Matrices / Neighbor / Bounds / Cursor / Integrate), and per-flock visible counts.
// Toggleable with F3 (configurable).

using System.Collections.Generic;
using System.Text;
using Bird_behiviour.Flocking.Simulation;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Bird_behiviour.Flocking.Tooling
{
    /// <summary>
    /// Tiny IMGUI overlay (top-left of the game view) that surfaces the simulation's
    /// runtime telemetry: smoothed FPS, total / per-flock bird counts, per-flock visible
    /// (post-cull) counts, and the mean ms-per-tick of every steering / rendering
    /// <see cref="ProfilerMarker"/> declared by the Flocking modules.
    /// </summary>
    /// <remarks>
    /// <b>Marker capture.</b> Each marker is read via <see cref="ProfilerRecorder"/> in
    /// the marker's category (<see cref="ProfilerCategory.Scripts"/>), with a 60-sample
    /// rolling buffer. We average the samples to get mean ms — so the displayed numbers
    /// represent the last second of frames at 60 Hz.
    /// <para/>
    /// <b>FPS smoothing.</b> Exponentially-weighted moving average (EWMA) on
    /// <c>1f / Time.unscaledDeltaTime</c> with a 0.1 weight on the new sample. Rejects
    /// the first frame's spike (very small <c>unscaledDeltaTime</c> on Awake).
    /// <para/>
    /// <b>Toggle key.</b> <see cref="ToggleKey"/> defaults to F3. Read directly from
    /// <see cref="Keyboard.current"/> so the controller doesn't require an Input Actions
    /// asset binding (matches the convention used by <see cref="FlyCameraController"/>).
    /// <para/>
    /// <b>IMGUI cost.</b> A single <see cref="GUI.Label"/> draw plus a background box.
    /// IMGUI is per-event so this fires twice per frame (Layout + Repaint) — &lt;0.1 ms
    /// total even on a Pixel 6. Acceptable for a debug HUD; promote to UI Toolkit if/when
    /// it grows beyond a few labels.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class FlockHUD : MonoBehaviour
    {
        [Tooltip("FlockWorld whose stats are surfaced. If null, FindFirstObjectByType is tried in Awake.")]
        [SerializeField] private FlockWorld world;

        [Tooltip("Show the HUD on Start. F3 toggles at runtime regardless of this setting.")]
        [SerializeField] private bool visibleOnStart = true;

        [Tooltip("Key used to toggle the HUD on/off at runtime.")]
        [SerializeField] private Key toggleKey = Key.F3;

        [Tooltip("Top-left screen position of the HUD, in pixels.")]
        [SerializeField] private Vector2 screenOrigin = new Vector2(8f, 8f);

        [Tooltip("HUD panel width in pixels. Height auto-grows with content.")]
        [SerializeField, Min(120f)] private float panelWidth = 320f;

        // ── Runtime state ─────────────────────────────────────────────────────────────
        private bool visible;
        private float smoothedFps;

        // One ProfilerRecorder per marker the FLOCKING_PLAN names. All live in the
        // Scripts category since that's where ProfilerMarker emits by default.
        // Buffer of 60 samples → averaged into the displayed mean ms.
        private const int SampleCount = 60;

        private ProfilerRecorder buildGridRec;
        private ProfilerRecorder neighborRec;
        private ProfilerRecorder boundsRec;
        private ProfilerRecorder cursorRec;
        private ProfilerRecorder integrateRec;
        private ProfilerRecorder cullRec;
        private ProfilerRecorder matricesRec;

        // Re-used StringBuilder so the per-frame OnGUI text build doesn't allocate.
        // Cleared each frame; capacity grows organically once and stays put thereafter.
        private readonly StringBuilder sb = new StringBuilder(1024);

        // Cached GUIStyle / GUIContent to avoid per-OnGUI allocations of these wrappers.
        private GUIStyle labelStyle;
        private GUIStyle boxStyle;

        // ── Unity lifecycle ───────────────────────────────────────────────────────────

        private void Awake()
        {
            if (world == null)
            {
                world = FindFirstObjectByType<FlockWorld>();
            }
            visible = visibleOnStart;
        }

        private void OnEnable()
        {
            // ProfilerRecorder.StartNew allocates an unmanaged ring buffer; one per marker
            // is fine on the heap (7 markers × 60 samples × 8 bytes = 3.4 KB). Disposed in
            // OnDisable so domain reload / scene unload doesn't leak.
            buildGridRec = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Flock.BuildGrid",  SampleCount);
            neighborRec  = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Flock.Neighbor",   SampleCount);
            boundsRec    = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Flock.Bounds",     SampleCount);
            cursorRec    = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Flock.Cursor",     SampleCount);
            integrateRec = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Flock.Integrate",  SampleCount);
            cullRec      = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Flock.Cull",       SampleCount);
            matricesRec  = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Flock.Matrices",   SampleCount);
        }

        private void OnDisable()
        {
            if (buildGridRec.Valid) buildGridRec.Dispose();
            if (neighborRec.Valid)  neighborRec.Dispose();
            if (boundsRec.Valid)    boundsRec.Dispose();
            if (cursorRec.Valid)    cursorRec.Dispose();
            if (integrateRec.Valid) integrateRec.Dispose();
            if (cullRec.Valid)      cullRec.Dispose();
            if (matricesRec.Valid)  matricesRec.Dispose();
        }

        private void Update()
        {
            // FPS smoothing — EWMA so a single dropped frame doesn't make the readout jump.
            float dt = Time.unscaledDeltaTime;
            if (dt > 1e-6f)
            {
                float instantFps = 1f / dt;
                // Seed on the first sane frame; afterwards low-pass at α = 0.1.
                smoothedFps = smoothedFps <= 0f ? instantFps : Mathf.Lerp(smoothedFps, instantFps, 0.1f);
            }

            // Toggle. Use Keyboard.current so the HUD doesn't require an Input Actions asset.
            Keyboard kb = Keyboard.current;
            if (kb != null && kb[toggleKey].wasPressedThisFrame)
            {
                visible = !visible;
            }
        }

        // ── IMGUI render ──────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            if (!visible || world == null) return;

            // Lazy style init — must happen inside OnGUI so GUI.skin is non-null.
            EnsureStyles();

            BuildHudText();

            // Measure required height from the label content so the panel grows with the
            // flock count rather than being a hard-coded constant.
            GUIContent content = new GUIContent(sb.ToString());
            float height = labelStyle.CalcHeight(content, panelWidth - 12f) + 12f;
            Rect panel = new Rect(screenOrigin.x, screenOrigin.y, panelWidth, height);

            GUI.Box(panel, GUIContent.none, boxStyle);
            GUI.Label(new Rect(panel.x + 6f, panel.y + 6f, panel.width - 12f, panel.height - 12f),
                      content, labelStyle);
        }

        private void EnsureStyles()
        {
            if (labelStyle == null)
            {
                labelStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize  = 12,
                    richText  = true,
                    wordWrap  = false,
                    alignment = TextAnchor.UpperLeft,
                };
                labelStyle.normal.textColor = new Color(0.95f, 0.95f, 0.95f, 1f);
            }
            if (boxStyle == null)
            {
                // Translucent dark background so the HUD is readable over both bright sky
                // and dark terrain. Built once, re-used forever.
                boxStyle = new GUIStyle(GUI.skin.box);
                Texture2D bg = new Texture2D(1, 1);
                bg.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.6f));
                bg.Apply();
                boxStyle.normal.background = bg;
            }
        }

        private void BuildHudText()
        {
            sb.Length = 0;

            // Header: FPS + frame ms.
            float frameMs = (Time.unscaledDeltaTime > 0f) ? Time.unscaledDeltaTime * 1000f : 0f;
            sb.Append("<b>Flock HUD</b>  (");
            sb.Append(toggleKey).Append(" toggles)\n");
            sb.Append("FPS: ").Append(smoothedFps.ToString("0.0"))
              .Append("   frame: ").Append(frameMs.ToString("0.00")).Append(" ms\n");

            // Total + per-flock counts.
            int flockCount = world.RegisteredFlockCount;
            sb.Append("Birds: ").Append(world.TotalBirdCount)
              .Append("   flocks: ").Append(flockCount)
              .Append("   ×").Append(world.BirdCountMultiplier.ToString("0.##")).Append('\n');

            // Slices is only allocated after the first RegisterFlock call. Guard so the
            // HUD doesn't throw on the bootstrap frame before any manager has registered.
            bool slicesReady = world.Slices.IsCreated && world.Slices.Length >= flockCount;
            for (int f = 0; f < flockCount; f++)
            {
                int birdCount = slicesReady ? world.Slices[f].Count : 0;
                int visibleBirds = world.GetVisibleCount(f);
                sb.Append("  flock ").Append(f)
                  .Append(": ").Append(birdCount).Append(" birds, ")
                  .Append(visibleBirds).Append(" visible\n");
            }

            // Cell size (one number, useful when tuning PerceptionRadius).
            sb.Append("Cell size: ").Append(world.CellSize.ToString("0.00")).Append('\n');

            // Profiler marker means. Order matches the per-tick chain so the readout reads
            // top-to-bottom in pipeline order.
            sb.Append("\n<b>Mean ms / tick</b>\n");
            AppendMarker("BuildGrid", buildGridRec);
            AppendMarker("Neighbor ", neighborRec);
            AppendMarker("Bounds   ", boundsRec);
            AppendMarker("Cursor   ", cursorRec);
            AppendMarker("Integrate", integrateRec);
            AppendMarker("Cull     ", cullRec);
            AppendMarker("Matrices ", matricesRec);
        }

        private void AppendMarker(string label, ProfilerRecorder rec)
        {
            sb.Append("  ").Append(label).Append(": ");
            if (!rec.Valid)
            {
                sb.Append("n/a\n");
                return;
            }
            double meanNs = MeanNanoseconds(rec);
            sb.Append((meanNs * 1e-6).ToString("0.000")).Append(" ms\n");
        }

        /// <summary>
        /// Averages the up-to-<see cref="SampleCount"/> samples in <paramref name="rec"/>'s
        /// ring buffer, returning 0 if no samples have been captured yet. Allocates a small
        /// list per call (Profiler API requirement) — acceptable on a HUD that only fires
        /// during interactive sessions.
        /// </summary>
        private static double MeanNanoseconds(ProfilerRecorder rec)
        {
            int n = rec.Capacity;
            if (n <= 0) return 0.0;
            // CopyTo writes Count entries, not Capacity — so size the scratch list to
            // Capacity once and let the API populate [0, Count).
            var samples = new List<ProfilerRecorderSample>(n);
            rec.CopyTo(samples, false);
            int count = samples.Count;
            if (count == 0) return 0.0;
            long total = 0;
            for (int i = 0; i < count; i++)
            {
                total += samples[i].Value;
            }
            return (double)total / count;
        }
    }
}
