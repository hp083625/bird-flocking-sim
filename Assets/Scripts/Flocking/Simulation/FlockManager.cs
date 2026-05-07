// FlockManager.cs — one-per-flock MonoBehaviour. Holds a FlockSettings reference, registers
// with the FlockWorld in OnEnable, owns the IFlockRenderer instance, and seeds initial
// positions / velocities for its slice.
// M1-2 / M1-5 in FLOCKING_PLAN.md.

using Bird_behiviour.Flocking.Core;
using Bird_behiviour.Flocking.Rendering;
using Unity.Mathematics;
using UnityEngine;

namespace Bird_behiviour.Flocking.Simulation
{
    /// <summary>
    /// One-per-flock authoring MonoBehaviour. Sits beside (or under) the
    /// <see cref="FlockWorld"/> in the scene, references a per-flock
    /// <see cref="IFlockSettings"/>-implementing <see cref="ScriptableObject"/> asset, and
    /// on <see cref="OnEnable"/> registers with the world to obtain its
    /// <see cref="FlockSlice"/>.
    /// </summary>
    /// <remarks>
    /// <b>Settings reference shape.</b> The serialized field is typed as
    /// <see cref="ScriptableObject"/> rather than a concrete <c>FlockSettings</c> type because
    /// <c>FlockSettings</c> lives in the <c>Tooling</c> asmdef and <c>Simulation</c> cannot
    /// reference <c>Tooling</c> (that would create a cycle: Tooling already depends on
    /// Simulation). At runtime the asset is cast to <see cref="IFlockSettings"/> (the only
    /// stable contract in <c>Core</c>); a future Editor-only property drawer in Slice 10
    /// will narrow the inspector slot to "ScriptableObjects implementing IFlockSettings".
    /// <para/>
    /// <b>Renderer ownership.</b> The manager owns its <see cref="IFlockRenderer"/> instance
    /// (created in <see cref="OnEnable"/>, disposed in <see cref="OnDisable"/>). Slice 2 hard-
    /// codes the concrete <see cref="InstancedFlockRenderer"/>; future slices will swap to
    /// indirect-draw via either a strategy enum or a factory interface in
    /// <c>Bird_behiviour.Flocking.Core</c>.
    /// <para/>
    /// <b>World discovery.</b> The serialized <see cref="world"/> field is auto-populated from
    /// a parent / scene search in <see cref="OnEnable"/> if left null in the inspector.
    /// </remarks>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-50)] // Register before FlockWorld's first LateUpdate.
    public sealed class FlockManager : MonoBehaviour
    {
        [Tooltip("Per-flock tuning asset (a ScriptableObject implementing IFlockSettings, " +
                 "e.g. the FlockSettings asset from the Tooling module).")]
        [SerializeField] private ScriptableObject settingsAsset;

        [Tooltip("FlockWorld this manager registers with. If null, GetComponentInParent + " +
                 "FindFirstObjectByType are tried in that order.")]
        [SerializeField] private FlockWorld world;

        // Optional runtime-only override (used by tests or factory code that builds
        // settings in memory). When set, takes precedence over settingsAsset.
        private IFlockSettings runtimeSettingsOverride;

        /// <summary>The settings asset this manager spawns + steers from. Null if unset or wrong type.</summary>
        public IFlockSettings Settings =>
            runtimeSettingsOverride ?? (settingsAsset as IFlockSettings);

        /// <summary>
        /// Programmatic settings setter — used by tests that build the scene in code. Must be
        /// called <em>before</em> the GameObject is enabled (e.g. immediately after
        /// <c>AddComponent</c> while the parent GameObject is still inactive). Pass a
        /// <see cref="ScriptableObject"/> implementing <see cref="IFlockSettings"/> for it to
        /// also round-trip through the inspector; pass any other implementation as a
        /// runtime-only override.
        /// </summary>
        public void SetSettings(IFlockSettings settings)
        {
            if (settings is ScriptableObject so)
            {
                settingsAsset = so;
                runtimeSettingsOverride = null;
            }
            else
            {
                runtimeSettingsOverride = settings;
            }
        }

        /// <summary>The renderer instance owned by this manager (allocated in OnEnable).</summary>
        public IFlockRenderer Renderer { get; private set; }

        /// <summary>The slice assigned by FlockWorld (valid after OnEnable).</summary>
        public FlockSlice Slice { get; private set; }

        private bool registered;

        private void OnEnable()
        {
            IFlockSettings s = Settings;
            if (s == null)
            {
                if (settingsAsset == null)
                {
                    Debug.LogError(
                        $"[FlockManager:{name}] No settings asset assigned; manager will not register.",
                        this);
                }
                else
                {
                    Debug.LogError(
                        $"[FlockManager:{name}] Settings asset '{settingsAsset.name}' does not implement " +
                        "IFlockSettings; manager will not register.",
                        this);
                }
                return;
            }

            if (world == null)
            {
                world = GetComponentInParent<FlockWorld>();
                if (world == null)
                {
                    world = FindFirstObjectByType<FlockWorld>();
                }
            }

            if (world == null)
            {
                Debug.LogError(
                    $"[FlockManager:{name}] No FlockWorld found in scene; manager will not register.",
                    this);
                return;
            }

            Renderer = new InstancedFlockRenderer();
            Slice = world.RegisterFlock(this); // Triggers OnSliceAllocated → SpawnIntoSlice.
            registered = true;
        }

        private void OnDisable()
        {
            if (registered && world != null)
            {
                world.DeregisterFlock(this);
                registered = false;
            }
            if (Renderer != null)
            {
                Renderer.Dispose();
                Renderer = null;
            }
        }

        /// <summary>
        /// Called by <see cref="FlockWorld"/> after it has (re-)allocated its per-bird arrays
        /// and assigned this manager its slice. Spawns BirdCount birds inside
        /// <c>PreferredCenter ± PreferredExtents</c> with random velocities in
        /// <c>[MinSpeed, MaxSpeed]</c>.
        /// </summary>
        internal void OnSliceAllocated(FlockSlice slice)
        {
            Slice = slice;
            IFlockSettings s = Settings;
            if (s == null || world == null || slice.Count == 0)
            {
                return;
            }
            SpawnIntoSlice(slice, s);
        }

        private void SpawnIntoSlice(FlockSlice slice, IFlockSettings s)
        {
            uint seed = s.RandomSeed != 0u
                ? s.RandomSeed
                : (uint)math.max(1L, (long)(Time.realtimeSinceStartup * 1e6));
            var rng = new Unity.Mathematics.Random(seed);

            float3 center  = s.PreferredCenter;
            float3 extents = s.PreferredExtents;
            float minSpeed = s.MinSpeed;
            float maxSpeed = math.max(minSpeed, s.MaxSpeed);

            for (int i = 0; i < slice.Count; i++)
            {
                int idx = slice.StartIndex + i;

                float3 pos = center + rng.NextFloat3(-extents, extents);
                world.Positions[idx] = pos;

                // Uniform direction on the unit sphere × random speed in [MinSpeed, MaxSpeed].
                float3 dir = rng.NextFloat3Direction();
                float speed = rng.NextFloat(minSpeed, maxSpeed);
                world.Velocities[idx] = dir * speed;
            }
        }

        private void OnDrawGizmosSelected()
        {
            IFlockSettings s = Settings;
            if (s == null) return;
            Gizmos.color = new Color(0.4f, 0.8f, 1f, 0.5f);
            Gizmos.DrawWireCube((Vector3)(float3)s.PreferredCenter,
                                (Vector3)(float3)s.PreferredExtents * 2f);
        }
    }
}
