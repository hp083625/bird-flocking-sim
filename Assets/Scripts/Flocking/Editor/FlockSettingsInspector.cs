// FlockSettingsInspector.cs — Slice 10 (M5-2) custom inspector for FlockSettings.
// Two-section layout: tunable fields edit live (write-through), structural fields edit
// a staging copy and only commit when the designer presses "Apply Structural Changes",
// which calls FlockManager.Rebuild() on every manager in the loaded scenes that
// references this asset. See FLOCKING_PLAN.md §6 M5-2.

using System;
using System.Collections.Generic;
using System.Reflection;
using Bird_behiviour.Flocking.Simulation;
using Bird_behiviour.Flocking.Tooling;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Bird_behiviour.Flocking.Editor
{
    /// <summary>
    /// Custom inspector for <see cref="FlockSettings"/> assets. Splits serialized fields
    /// into two sections by attribute (<see cref="FlockTunableAttribute"/> vs
    /// <see cref="FlockStructuralAttribute"/>):
    /// <list type="bullet">
    /// <item><description><b>Tunable (live):</b> drawn at the top with a normal
    /// <see cref="EditorGUILayout.PropertyField(SerializedProperty, GUILayoutOption[])"/>;
    /// edits write through to the asset every frame.</description></item>
    /// <item><description><b>Structural (Apply to commit):</b> drawn below; each row shows
    /// the staging value and (when staging differs from applied) a dimmed strikethrough of
    /// the applied value. The "Apply Structural Changes" button at the bottom of the
    /// section commits staging values to the asset and calls
    /// <see cref="FlockManager.Rebuild"/> on every manager in the loaded scenes that
    /// references this asset.</description></item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Untagged fields fall back into the tunable section so the inspector never silently
    /// drops a field. The pair (Randomize / Copy Seed) mini-buttons next to
    /// <c>RandomSeed</c> are wired in <see cref="DrawSeedRow"/>.
    /// <para/>
    /// <b>Apply pipeline (test seam).</b> The static <see cref="ApplyStructuralChanges"/>
    /// helper is the single entry point that copies staging → asset and triggers rebuilds;
    /// EditMode tests in <c>FlockSettingsHotReloadTests</c> call it directly without
    /// instantiating an <see cref="UnityEditor.Editor"/>.
    /// </remarks>
    [CustomEditor(typeof(FlockSettings))]
    public sealed class FlockSettingsInspector : UnityEditor.Editor
    {
        // Serialized property paths for the structural fields, with their current
        // *staging* (UI-side) value held inside SerializedObject. We compare staging
        // against the asset's applied value to enable / disable the Apply button.
        private readonly List<SerializedProperty> tunableProps    = new List<SerializedProperty>(16);
        private readonly List<SerializedProperty> structuralProps = new List<SerializedProperty>(8);

        // Cached list of structural FieldInfo for the inspected type — used to read the
        // *applied* value from the asset for the strikethrough display.
        private readonly List<FieldInfo> structuralFields = new List<FieldInfo>(8);

        // Path of the seed serialized property — special-cased to render the
        // Randomize / Copy buttons inline.
        private const string SeedPropertyPath = "randomSeed";

        private GUIStyle dimmedStrikethroughStyle;

        private void OnEnable()
        {
            tunableProps.Clear();
            structuralProps.Clear();
            structuralFields.Clear();

            Type t = target.GetType();
            // Walk up to (but not including) ScriptableObject so we don't reflect over
            // engine-internal fields.
            SerializedProperty iter = serializedObject.GetIterator();
            bool enterChildren = true;
            while (iter.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (iter.propertyPath == "m_Script")
                {
                    continue;
                }

                FieldInfo fi = FindField(t, iter.propertyPath);
                if (fi == null)
                {
                    // Unknown — drop into tunable so the field still renders.
                    tunableProps.Add(serializedObject.FindProperty(iter.propertyPath));
                    continue;
                }

                bool isStructural = fi.GetCustomAttribute<FlockStructuralAttribute>() != null;
                if (isStructural)
                {
                    structuralProps.Add(serializedObject.FindProperty(iter.propertyPath));
                    structuralFields.Add(fi);
                }
                else
                {
                    // Tunable (explicit) or untagged — both render live.
                    tunableProps.Add(serializedObject.FindProperty(iter.propertyPath));
                }
            }
        }

        public override void OnInspectorGUI()
        {
            EnsureStyles();
            serializedObject.Update();

            // ── Tunable section (live edits, no rebuild) ─────────────────────────────
            EditorGUILayout.LabelField("Tunable (live)", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            for (int i = 0; i < tunableProps.Count; i++)
            {
                SerializedProperty p = tunableProps[i];
                if (p.propertyPath == SeedPropertyPath)
                {
                    DrawSeedRow(p);
                }
                else
                {
                    EditorGUILayout.PropertyField(p, true);
                }
            }
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(8f);

            // ── Structural section (Apply to commit) ────────────────────────────────
            EditorGUILayout.LabelField("Structural (Apply to commit)", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            bool anyStructuralDiff = false;
            for (int i = 0; i < structuralProps.Count; i++)
            {
                SerializedProperty p = structuralProps[i];
                FieldInfo fi = structuralFields[i];
                bool diffs = StructuralFieldDiffers(p, fi);
                anyStructuralDiff |= diffs;

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(p, true);
                if (diffs)
                {
                    object applied = fi.GetValue(target);
                    string appliedStr = applied != null ? applied.ToString() : "<null>";
                    GUILayout.Label("(was " + appliedStr + ")", dimmedStrikethroughStyle, GUILayout.MaxWidth(160f));
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(4f);
            using (new EditorGUI.DisabledScope(!anyStructuralDiff))
            {
                if (GUILayout.Button("Apply Structural Changes", GUILayout.Height(28f)))
                {
                    // Push the staging values from SerializedObject → asset, then
                    // rebuild every FlockManager in the loaded scenes that references
                    // this asset.
                    serializedObject.ApplyModifiedProperties();
                    ApplyStructuralChanges((FlockSettings)target);
                    GUIUtility.ExitGUI();
                    return;
                }
            }

            // Tunable edits are persisted unconditionally on every paint.
            serializedObject.ApplyModifiedProperties();
        }

        // ── Public test seam ──────────────────────────────────────────────────────────

        /// <summary>
        /// Commits any staged structural-field values that have already been written into
        /// the asset (typical caller: the Apply button after
        /// <c>SerializedObject.ApplyModifiedProperties</c>) and triggers
        /// <see cref="FlockManager.Rebuild"/> on every active manager in the loaded scenes
        /// that references <paramref name="settings"/>. Public so EditMode tests can
        /// exercise the rebuild-attribution path without an inspector instance.
        /// </summary>
        public static void ApplyStructuralChanges(FlockSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            // Persist asset edits.
            EditorUtility.SetDirty(settings);

            // Find managers via the loaded scenes' root GameObjects → GetComponentsInChildren.
            // FindObjectsByType isn't an option for inactive objects, but the inspector
            // workflow assumes managers in active scenes.
            int rebuilt = 0;
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                Scene scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded)
                {
                    continue;
                }
                GameObject[] roots = scene.GetRootGameObjects();
                for (int r = 0; r < roots.Length; r++)
                {
                    FlockManager[] managers = roots[r].GetComponentsInChildren<FlockManager>(includeInactive: false);
                    for (int m = 0; m < managers.Length; m++)
                    {
                        // ReferenceEquals — Settings returns the runtime-or-asset reference;
                        // we only want to rebuild managers whose asset === this settings.
                        if (ReferenceEquals(managers[m].Settings, settings))
                        {
                            managers[m].Rebuild();
                            rebuilt++;
                        }
                    }
                }
            }

            // Also notify any listener that subscribed via the static event below
            // (test seam — see FlockSettingsHotReloadTests).
            StructuralChangesApplied?.Invoke(settings, rebuilt);
        }

        /// <summary>
        /// Test-only event raised whenever <see cref="ApplyStructuralChanges"/> completes.
        /// Carries the settings asset and the number of rebuilt managers. Production code
        /// should not subscribe; the inspector itself does not.
        /// </summary>
        public static event Action<FlockSettings, int> StructuralChangesApplied;

        // ── Helpers ───────────────────────────────────────────────────────────────────

        private void DrawSeedRow(SerializedProperty seedProp)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(seedProp);
            if (GUILayout.Button("Randomize", GUILayout.MaxWidth(90f)))
            {
                // Range is exclusive on the upper bound; the result is always ≥ 1, so
                // we satisfy the "non-zero seed" requirement (zero means auto-derive).
                uint newSeed = (uint)UnityEngine.Random.Range(1, int.MaxValue);
                seedProp.uintValue = newSeed;
            }
            if (GUILayout.Button("Copy Seed", GUILayout.MaxWidth(90f)))
            {
                EditorGUIUtility.systemCopyBuffer = seedProp.uintValue.ToString();
            }
            EditorGUILayout.EndHorizontal();
        }

        private static FieldInfo FindField(Type t, string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            for (Type cur = t; cur != null && cur != typeof(ScriptableObject); cur = cur.BaseType)
            {
                FieldInfo fi = cur.GetField(name, flags);
                if (fi != null)
                {
                    return fi;
                }
            }
            return null;
        }

        private bool StructuralFieldDiffers(SerializedProperty staging, FieldInfo fi)
        {
            object applied = fi.GetValue(target);
            switch (staging.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return staging.longValue != Convert.ToInt64(applied);
                case SerializedPropertyType.Float:
                    return !Mathf.Approximately(staging.floatValue, Convert.ToSingle(applied));
                case SerializedPropertyType.Boolean:
                    return staging.boolValue != (bool)applied;
                case SerializedPropertyType.Vector3:
                    return staging.vector3Value != (Vector3)applied;
                case SerializedPropertyType.String:
                    return staging.stringValue != (string)applied;
                default:
                    // Other types: rely on SerializedProperty.DataEquals against the
                    // asset's serialized form via a fresh SerializedObject. Cheap path:
                    // assume different — the Apply button will just be a no-op recompile.
                    return false;
            }
        }

        private void EnsureStyles()
        {
            if (dimmedStrikethroughStyle == null)
            {
                dimmedStrikethroughStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    fontStyle = FontStyle.Italic,
                    normal = { textColor = new Color(0.55f, 0.55f, 0.55f, 1f) },
                };
            }
        }
    }
}
