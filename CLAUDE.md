# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project state

Fresh Unity 6 URP template project (Editor `6000.1.15f1`). No gameplay code has been written yet — `Assets/` contains only the default Unity URP template contents (`SampleScene.unity`, `InputSystem_Actions.inputactions`, `TutorialInfo/Readme.cs` + its editor, TextMeshPro, URP Settings). The directory name `Bird_behiviour` indicates the intended subject (bird AI/flocking/behavior) but nothing has been implemented.

When adding new code, create a dedicated folder under `Assets/` (e.g. `Assets/Scripts/`) rather than placing scripts at the Assets root or inside `TutorialInfo/`.

## Build / run / test

There is no CLI build script in this repo. Day-to-day work happens in the Unity Editor:

- Open the project: launch Unity Hub and add `/Users/hitesh/Documents/Unity/Bird_behiviour`, then open with editor `6000.1.15f1` (must match `ProjectSettings/ProjectVersion.txt`).
- Play / iterate: open `Assets/Scenes/SampleScene.unity` and press Play.
- Tests: `com.unity.test-framework` is installed. Run via **Window → General → Test Runner** in the editor (EditMode / PlayMode). No test assemblies exist yet — adding tests requires creating an `.asmdef` with `"optionalUnityReferences": ["TestAssemblies"]`.
- Headless build (if needed later):
  ```
  /Applications/Unity/Hub/Editor/6000.1.15f1/Unity.app/Contents/MacOS/Unity \
    -batchmode -nographics -quit -projectPath . -logFile -
  ```

Do not edit files under `Library/`, `Temp/`, `Logs/`, or `UserSettings/` — these are Unity-generated and will be regenerated.

## Key project configuration

- **Render pipeline**: Universal Render Pipeline 17.1.0. Two renderer assets are wired up in `Assets/Settings/`: `PC_Renderer`/`PC_RPAsset` and `Mobile_Renderer`/`Mobile_RPAsset` — when adding shaders/materials, verify they work against both quality tiers.
- **Input**: New Input System (`com.unity.inputsystem` 1.14.0). Bindings live in `Assets/InputSystem_Actions.inputactions` — extend that asset rather than introducing legacy `Input.GetKey` calls.
- **AI Navigation**: `com.unity.ai.navigation` 2.0.8 is available (NavMesh components) — relevant if bird behavior involves navmesh-based movement.
- **Visual Scripting**: `com.unity.visualscripting` 1.9.7 is included; behavior could be authored as graphs as well as C#.
- **Multiplayer**: `com.unity.multiplayer.center` is present but no transport/netcode package is installed — single-player by default.

## Notes for code changes

- The only existing scripts are `Assets/TutorialInfo/Scripts/Readme.cs` (a `ScriptableObject` for the Unity welcome screen) and its editor `ReadmeEditor.cs`. These are template boilerplate and safe to delete once real gameplay code is added.
- Every asset in `Assets/` has a paired `.meta` file. When renaming/moving/deleting an asset via shell, move/delete the `.meta` alongside it; otherwise Unity will regenerate a new GUID and break references.
