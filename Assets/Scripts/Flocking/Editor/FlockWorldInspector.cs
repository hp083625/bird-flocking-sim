// FlockWorldInspector.cs — Slice 10 (M5-3) custom inspector for FlockWorld.
// Sliders for MaxSimDt + SimSpeedMultiplier; world bounds editable as structural fields
// gated by an Apply button (calls FlockWorld.Rebuild()); read-only display of total
// bird count, registered flock count, and current spatial cell size; "Restart Sim"
// button at the bottom. See FLOCKING_PLAN.md §6 M5-3.

using Bird_behiviour.Flocking.Simulation;
using UnityEditor;
using UnityEngine;

namespace Bird_behiviour.Flocking.Editor
{
    /// <summary>
    /// Custom inspector for <see cref="FlockWorld"/>. Provides:
    /// <list type="bullet">
    /// <item><description><b>Sim time sliders.</b> <c>MaxSimDt</c> (1/240 .. 1/15 s) and
    /// <c>SimSpeedMultiplier</c> (0 .. 4×). Edits write through immediately.</description></item>
    /// <item><description><b>World bounds (structural).</b> Center / extents / weight render with
    /// staging-vs-applied diff display; the Apply button at the bottom of the section
    /// calls <see cref="FlockWorld.Rebuild"/>.</description></item>
    /// <item><description><b>Read-only stats.</b> Total bird count, registered flock count,
    /// current spatial-grid cell size (or a "Slice 3 not landed yet" guard).</description></item>
    /// <item><description><b>Restart Sim button.</b> Big button at the bottom that calls
    /// <see cref="FlockWorld.Rebuild"/> unconditionally.</description></item>
    /// </list>
    /// </summary>
    [CustomEditor(typeof(FlockWorld))]
    public sealed class FlockWorldInspector : UnityEditor.Editor
    {
        // Field paths (must match the private field names in FlockWorld).
        private const string PathBoundsCenter      = "worldBoundsCenter";
        private const string PathBoundsExtents     = "worldBoundsExtents";
        private const string PathBoundsWeight      = "worldBoundsWeight";
        private const string PathMaxSimDt          = "maxSimDt";
        private const string PathSimSpeedMultiplier = "simSpeedMultiplier";

        private SerializedProperty boundsCenterProp;
        private SerializedProperty boundsExtentsProp;
        private SerializedProperty boundsWeightProp;
        private SerializedProperty maxSimDtProp;
        private SerializedProperty simSpeedProp;

        // Snapshot of the bounds values at last Apply — used to detect "differs from applied".
        private Vector3 appliedBoundsCenter;
        private Vector3 appliedBoundsExtents;
        private float appliedBoundsWeight;

        private void OnEnable()
        {
            boundsCenterProp   = serializedObject.FindProperty(PathBoundsCenter);
            boundsExtentsProp  = serializedObject.FindProperty(PathBoundsExtents);
            boundsWeightProp   = serializedObject.FindProperty(PathBoundsWeight);
            maxSimDtProp       = serializedObject.FindProperty(PathMaxSimDt);
            simSpeedProp       = serializedObject.FindProperty(PathSimSpeedMultiplier);

            // Initial applied snapshot = whatever's already on the asset/component.
            CaptureAppliedSnapshot();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            FlockWorld world = (FlockWorld)target;

            // ── Sim time (live edits) ────────────────────────────────────────────────
            EditorGUILayout.LabelField("Simulation Time (live)", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            maxSimDtProp.floatValue = EditorGUILayout.Slider(
                new GUIContent("Max Sim Δt", "Upper bound on per-tick dt (seconds)."),
                maxSimDtProp.floatValue, 1f / 240f, 1f / 15f);
            simSpeedProp.floatValue = EditorGUILayout.Slider(
                new GUIContent("Sim Speed Multiplier", "1 = real-time, 0 = paused."),
                simSpeedProp.floatValue, 0f, 4f);
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(8f);

            // ── World bounds (structural — Apply to commit) ─────────────────────────
            EditorGUILayout.LabelField("World Bounds (Apply to commit)", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(boundsCenterProp);
            EditorGUILayout.PropertyField(boundsExtentsProp);
            EditorGUILayout.PropertyField(boundsWeightProp);

            bool boundsDiffer =
                boundsCenterProp.vector3Value  != appliedBoundsCenter
                || boundsExtentsProp.vector3Value != appliedBoundsExtents
                || !Mathf.Approximately(boundsWeightProp.floatValue, appliedBoundsWeight);

            if (boundsDiffer)
            {
                EditorGUILayout.HelpBox(
                    "Pending changes — press Apply to rebuild the world arrays + spatial index.",
                    MessageType.Info);
            }
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(2f);
            using (new EditorGUI.DisabledScope(!boundsDiffer))
            {
                if (GUILayout.Button("Apply Bounds Changes", GUILayout.Height(24f)))
                {
                    serializedObject.ApplyModifiedProperties();
                    world.Rebuild();
                    CaptureAppliedSnapshot();
                    GUIUtility.ExitGUI();
                    return;
                }
            }

            EditorGUILayout.Space(10f);

            // ── Read-only stats ──────────────────────────────────────────────────────
            EditorGUILayout.LabelField("Live Stats (read-only)", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField("Total Bird Count", world.TotalBirdCount.ToString());
            EditorGUILayout.LabelField("Registered Flocks", world.RegisteredFlockCount.ToString());
            EditorGUILayout.LabelField("Current Cell Size", DescribeCellSize(world));
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(10f);

            // ── Restart Sim ──────────────────────────────────────────────────────────
            Color prev = GUI.backgroundColor;
            GUI.backgroundColor = new Color(1f, 0.55f, 0.35f);
            if (GUILayout.Button("Restart Sim", GUILayout.Height(32f)))
            {
                serializedObject.ApplyModifiedProperties();
                world.Rebuild();
                CaptureAppliedSnapshot();
                GUI.backgroundColor = prev;
                GUIUtility.ExitGUI();
                return;
            }
            GUI.backgroundColor = prev;

            // Persist live edits (sliders) on every paint.
            serializedObject.ApplyModifiedProperties();
        }

        private void CaptureAppliedSnapshot()
        {
            appliedBoundsCenter  = boundsCenterProp.vector3Value;
            appliedBoundsExtents = boundsExtentsProp.vector3Value;
            appliedBoundsWeight  = boundsWeightProp.floatValue;
        }

        private static string DescribeCellSize(FlockWorld world)
        {
            // Slice 3 will give FlockWorld a SpatialHashGrid field; until then there is
            // no public surface that exposes the cell size, so fall back to the guard
            // string the spec asks for.
            // Forward-compat hook: reflectively look for a "SpatialIndex" or "CellSize"
            // member so the inspector lights up the moment Slice 3 lands.
            System.Type t = world.GetType();
            System.Reflection.PropertyInfo cellSizeProp = t.GetProperty(
                "CellSize",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            if (cellSizeProp != null && cellSizeProp.PropertyType == typeof(float))
            {
                float cs = (float)cellSizeProp.GetValue(world);
                return cs.ToString("0.000");
            }
            return "n/a — Slice 3 not landed yet";
        }
    }
}
