// FlockSettings.cs — ScriptableObject implementation of IFlockSettings.
// Slice 2 ships the asset only with the default Unity inspector; the custom
// inspector + tunable / structural split lands in Slice 10 (M5-2).

using Bird_behiviour.Flocking.Core;
using Unity.Mathematics;
using UnityEngine;

namespace Bird_behiviour.Flocking.Tooling
{
    /// <summary>
    /// Per-flock authoring asset implementing <see cref="IFlockSettings"/>. Designers
    /// create one via <b>Assets ▸ Create ▸ Flocking ▸ Flock Settings</b> and assign it
    /// to a <c>FlockManager</c> in the scene.
    /// </summary>
    /// <remarks>
    /// Slice 2 deliberately uses the default Unity inspector — the custom inspector with
    /// tunable / structural split lands in Slice 10 (M5-2). <see cref="OnValidate"/> still
    /// clamps weights ≥ 0 and enforces <see cref="SeparationRadius"/> ≤
    /// <see cref="PerceptionRadius"/> so bad asset edits are caught immediately.
    /// </remarks>
    [CreateAssetMenu(menuName = "Flocking/Flock Settings", fileName = "FlockSettings")]
    public class FlockSettings : ScriptableObject, IFlockSettings
    {
        // ── Self-flock weights ────────────────────────────────────────────────────────

        [Header("Self-Flock Weights")]
        [SerializeField, Min(0f)] private float inSeparationWeight = 1f;
        [SerializeField, Min(0f)] private float inAlignmentWeight  = 1f;
        [SerializeField, Min(0f)] private float inCohesionWeight   = 1f;

        // ── Cross-flock weights (binary self-vs-other) ────────────────────────────────

        [Header("Cross-Flock Weights")]
        [SerializeField, Min(0f)] private float outSeparationWeight = 1f;
        [SerializeField, Min(0f)] private float outAlignmentWeight  = 0f;
        [SerializeField, Min(0f)] private float outCohesionWeight   = 0f;

        // ── Bounds (per-flock soft preferred zone) ────────────────────────────────────

        [Header("Bounds (Soft Preferred Zone)")]
        [SerializeField] private Vector3 preferredCenter  = Vector3.zero;
        [SerializeField] private Vector3 preferredExtents = new Vector3(20f, 10f, 20f);
        [SerializeField, Min(0f)] private float preferredAttractionWeight = 1f;

        // ── Perception ────────────────────────────────────────────────────────────────

        [Header("Perception")]
        [SerializeField, Min(0f)] private float perceptionRadius = 5f;
        [SerializeField, Min(0f)] private float separationRadius = 1.5f;
        [SerializeField, Range(0f, math.PI)] private float perceptionConeHalfAngleRadians = 2.356194f; // 135°

        // ── Motion ────────────────────────────────────────────────────────────────────

        [Header("Motion")]
        [SerializeField, Min(0f)] private float minSpeed        = 1f;
        [SerializeField, Min(0f)] private float maxSpeed        = 10f;
        [SerializeField, Min(0f)] private float maxAcceleration = 30f;

        // ── Cursor reaction ───────────────────────────────────────────────────────────

        [Header("Cursor Reaction")]
        [Tooltip("Positive attracts toward the cursor, negative repels, zero ignores.")]
        [SerializeField] private float cursorReactionStrength = 0f;
        [SerializeField, Min(0f)] private float cursorReactionRadius = 10f;

        // ── Visual + lifecycle ────────────────────────────────────────────────────────

        [Header("Visual + Lifecycle")]
        [SerializeField, Min(0)] private int birdCount = 100;
        [SerializeField] private Mesh birdMesh;
        [SerializeField] private Material birdMaterial;
        [Tooltip("0 = auto-derive from time at spawn; non-zero = deterministic seed.")]
        [SerializeField] private uint randomSeed = 0u;

        // ── IFlockSettings property surface ───────────────────────────────────────────

        /// <inheritdoc/>
        public float InSeparationWeight  => inSeparationWeight;
        /// <inheritdoc/>
        public float InAlignmentWeight   => inAlignmentWeight;
        /// <inheritdoc/>
        public float InCohesionWeight    => inCohesionWeight;

        /// <inheritdoc/>
        public float OutSeparationWeight => outSeparationWeight;
        /// <inheritdoc/>
        public float OutAlignmentWeight  => outAlignmentWeight;
        /// <inheritdoc/>
        public float OutCohesionWeight   => outCohesionWeight;

        /// <inheritdoc/>
        public float3 PreferredCenter           => preferredCenter;
        /// <inheritdoc/>
        public float3 PreferredExtents          => preferredExtents;
        /// <inheritdoc/>
        public float  PreferredAttractionWeight => preferredAttractionWeight;

        /// <inheritdoc/>
        public float PerceptionRadius              => perceptionRadius;
        /// <inheritdoc/>
        public float SeparationRadius              => separationRadius;
        /// <inheritdoc/>
        public float PerceptionConeHalfAngleRadians => perceptionConeHalfAngleRadians;

        /// <inheritdoc/>
        public float MinSpeed         => minSpeed;
        /// <inheritdoc/>
        public float MaxSpeed         => maxSpeed;
        /// <inheritdoc/>
        public float MaxAcceleration  => maxAcceleration;

        /// <inheritdoc/>
        public float CursorReactionStrength => cursorReactionStrength;
        /// <inheritdoc/>
        public float CursorReactionRadius   => cursorReactionRadius;

        /// <inheritdoc/>
        public int      BirdCount    => birdCount;
        /// <inheritdoc/>
        public Mesh     BirdMesh     => birdMesh;
        /// <inheritdoc/>
        public Material BirdMaterial => birdMaterial;
        /// <inheritdoc/>
        public uint     RandomSeed   => randomSeed;

        /// <summary>
        /// Clamps weights ≥ 0 (defensive — <c>[Min]</c> attribute already does this in the
        /// inspector, but raw asset edits or scripted writes bypass that) and asserts
        /// <see cref="SeparationRadius"/> ≤ <see cref="PerceptionRadius"/>. Also enforces
        /// <c>MinSpeed ≤ MaxSpeed</c>.
        /// </summary>
        private void OnValidate()
        {
            inSeparationWeight  = Mathf.Max(0f, inSeparationWeight);
            inAlignmentWeight   = Mathf.Max(0f, inAlignmentWeight);
            inCohesionWeight    = Mathf.Max(0f, inCohesionWeight);
            outSeparationWeight = Mathf.Max(0f, outSeparationWeight);
            outAlignmentWeight  = Mathf.Max(0f, outAlignmentWeight);
            outCohesionWeight   = Mathf.Max(0f, outCohesionWeight);
            preferredAttractionWeight = Mathf.Max(0f, preferredAttractionWeight);

            perceptionRadius     = Mathf.Max(0f, perceptionRadius);
            separationRadius     = Mathf.Max(0f, separationRadius);
            cursorReactionRadius = Mathf.Max(0f, cursorReactionRadius);
            maxAcceleration      = Mathf.Max(0f, maxAcceleration);

            // Enforce SeparationRadius ≤ PerceptionRadius.
            if (separationRadius > perceptionRadius)
            {
                Debug.LogWarning(
                    $"[FlockSettings:{name}] SeparationRadius ({separationRadius}) > " +
                    $"PerceptionRadius ({perceptionRadius}); clamping SeparationRadius down.",
                    this);
                separationRadius = perceptionRadius;
            }

            // Enforce MinSpeed ≤ MaxSpeed.
            if (minSpeed > maxSpeed)
            {
                Debug.LogWarning(
                    $"[FlockSettings:{name}] MinSpeed ({minSpeed}) > MaxSpeed ({maxSpeed}); " +
                    "clamping MinSpeed down.",
                    this);
                minSpeed = maxSpeed;
            }

            birdCount = Mathf.Max(0, birdCount);
        }
    }
}
