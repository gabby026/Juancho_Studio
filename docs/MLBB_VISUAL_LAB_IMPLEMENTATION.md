# Juancho MLBB Visual Lab — Implementation Plan

## Mission

Extend the existing Juancho Studio / AssetStudio build overlay into a Windows desktop tool that can inventory real MLBB Unity packages, reconstruct supported assets in a 3D runtime, play AnimationClips, preview visual effects, and manually author skill sequences. Preserve the upstream AssetStudio workflow and keep imported source packages unchanged during analysis.

## Repository baseline (audited 2026-10-11)

- Repository: `gabby026/Juancho_Studio`, default branch `main`.
- The repository is an overlay/build project, not a full vendored copy of AssetStudio. Its GitHub Actions build clones upstream `perfare/AssetStudio`, applies `build/patch.ps1`, builds native texture helpers, and publishes a Windows x64 ZIP.
- Existing feature: `Juancho → Replace Selected Texture2D`, which edits the opened asset file and keeps a backup. Visual Lab must be a separate read-only analysis path; do not route its scan or preview code through this write-back feature.
- The build workflow currently targets .NET 8 Windows and runs on `windows-latest`.
- A Nana 10th Anniversary ZIP exists in the user's local file library and contains 58 `.unity3d` bundles. It is an integration candidate but has not yet been run through the packaged Windows Visual Lab runtime.
- The SkinForgeID review is recorded in `docs/REFERENCE_PROJECT_REVIEW.md`. No reference-project code, workspace, or assets were copied into Juancho.

## Architecture decision

Keep the AssetStudio code-reading layer as the authoritative source of serialized asset data. Treat Visual Lab as a separate analysis and preview workspace connected through an adapter, rather than mutating or replacing the existing texture editing code.

### Layer A — Source inventory and safety

Implemented in the foundation branch:
- Recursive folder and multi-file scans.
- Inventory of common Unity bundles, serialized asset files, texture companions, audio companions, and manifests.
- SHA-256 fingerprints, file sizes, timestamps, relative paths, and suggested folder groups.
- Duplicate-content flags.
- Warning diagnostics for unreadable or missing paths.
- JSON scan report export.
- Output-path protection that blocks saving reports and sequence projects inside imported source directories.
- Smoke tests covering scanning, fingerprints, timeline persistence, invalid timing, and source preservation.

Important limitation: folder grouping is a heuristic only; it is not a dependency graph extracted from Unity serialized object references.

### Layer B — Serialized Unity asset index

Foundation implementation now added:
1. Load selected bundle/file inputs through a separate `AssetStudio.AssetsManager` instance, leaving the main AssetStudio GUI state separate.
2. Record parsed serialized objects by file identity, Path ID, class/type, best-effort object name, Unity version, platform, and serialized size.
3. Walk serialized object fields to find `PPtr<T>` references and resolve them against the selected loaded package where possible.
4. Show object references and unresolved counts in the new Serialized Objects tab.
5. Keep duplicate display names distinct through file identity plus Path ID.
6. Mark pointers that cannot be resolved within the loaded set as unresolved instead of silently substituting another object.

Still required: validate the index against the local Nana multi-bundle fixture, handle basename collisions more robustly, test nested asset wrappers and unusual type trees, and persist a versioned index sidecar. A compile or a resolved PPtr is not proof that the target can be rendered.

### Layer C — Reconstruction adapter

Create a model independent of WinForms so the renderer can be replaced without changing asset discovery:
- scene nodes and transforms;
- mesh and skinned-mesh data;
- skeleton / bone mappings;
- material and texture assignments;
- animation curve bindings and event markers;
- particle system, renderer, trail, and shader descriptors;
- dependency edges and unsupported-feature diagnostics.

Every reconstructed feature has a support state: Exact-from-data, Approximate, Missing-dependency, or Unsupported. Do not silently label a visual fallback as exact.

### Layer D — Real-time viewer runtime

Do a proof-of-concept comparison before choosing the final implementation:
- Reuse any compatible model / animation preview functionality available in current AssetStudio.
- Evaluate a separate Unity runtime for skeletal animation and Unity particle systems.
- Treat Android-targeted `.unity3d` bundles as serialized-data input, not Windows Player runtime files. Unity documents AssetBundles as platform-specific; the Editor may load them during development, but individual assets can still fail due to platform-specific shaders/textures. Reconstruct supported data into a separate desktop preview workspace rather than relying only on `AssetBundle.LoadFromFile` in a Windows player.
- The current Nana 10th Anniversary fixture headers identify Unity `2019.4.33f1`, matching the reference project's stated editor version. Use this as a fixture-specific hypothesis; inspect each package header instead of hard-coding one engine version globally.
- Export or bridge only the data needed for preview, with a manifest that retains source-file/path-ID provenance.
- Verify the target Unity serialization version per package before selecting a runtime version; do not assume that all MLBB bundles use the same version.
- Keep runtime output in a disposable workspace. The original bundle stays read-only.

Acceptance evidence for the runtime choice: a real hero mesh renders with the correct texture/material, one real AnimationClip moves the intended skeleton, and one effect renders without a material/alpha regression.

### Layer E — Manual skill timeline

The foundation branch defines a versioned JSON sequence format with Animation, Effect, Projectile, Activation, and Marker event kinds. Times are in seconds. The current UI allows event authoring and save/reopen; it does not yet execute the timeline.

Next:
- Add per-track lanes and horizontal zoom.
- Add playhead, snapping, selection, drag-to-move, duration resizing, looping, and speed controls.
- Connect timeline events to runtime objects and AnimationClip/effect players.
- Save the timeline and editor state outside source directories.
- Preserve disabled events and unresolved references so incomplete sequences remain diagnosable.

### Layer F — Effects and skill behavior

Implement in slices:
1. Individual recovered effect object preview.
2. Particle system properties and playback controls.
3. Trails, animated textures, transparency, sorting, and supported shaders.
4. Multiple simultaneous effects in one scene.
5. Parent/child transforms and activation events.
6. Manual skill sequence playback.
7. Where data exists, event markers and controller transitions.
8. Clearly label game logic not present in the assets. Collision, hit detection, target tracking, server logic, and other game behaviors cannot be inferred merely from a visual asset bundle.

## UI layout target

- **Package Explorer:** root selection, multi-bundle import, scan results, grouping, duplicate indicators, dependency status.
- **Hierarchy / 3D Viewport:** selectable scene tree, orbit/pan/zoom camera, axis/grid, skeleton overlay, selected-object inspector.
- **Animation:** clip list, play/pause/stop, frame stepping, scrubbing, loop and playback speed.
- **Effects:** effect object list, property inspector, visibility, playback/reset/loop/speed.
- **Skill Timeline:** multiple tracks, start/duration, source asset, enabled state, playhead and sequence playback.
- **Diagnostics:** missing pointers, unsupported serialized classes, missing textures/materials, rig-binding failures, and runtime compatibility notes.

The current foundation UI exposes Package Explorer, Skill Timeline, and an explicit Runtime Status tab. The status tab states the current playback limitation so users are not misled.

## Safety and source-preservation rules

1. Scanning and preview never writes into the source path.
2. Keep source file paths and SHA-256 hashes in diagnostics.
3. Scan outputs and timeline files must be saved outside every imported source directory.
4. If a runtime requires temporary conversion, use a separate workspace and retain a provenance map to the source.
5. Never delete or rewrite source files as part of import, rendering, or cache cleanup.
6. The existing Texture2D replacement feature remains a separate explicit edit action with its own confirmation/backup behavior.
7. Use test fixture copies when validating transformations.
8. Don't package user game assets in releases; the test package should be supplied locally by the maintainer.

## Milestones and exit criteria

### M0 — Baseline and smoke tests
- [x] Confirm repo architecture and build workflow.
- [x] Add a testable inventory / timeline core.
- [x] Add source-protection tests; the GitHub Actions smoke-test step passed on the foundation commit.
- [x] Windows x64 CI build completed successfully after serialized-object UI/indexer integration.
- [ ] Validate indexing against the local Nana fixture in the Windows package; CI does not have access to the user's private asset package.

### M1 — Package inventory
- [x] Folder scan and multi-file scan.
- [x] Hashes, categories, folder heuristics, duplicate detection.
- [x] Report export outside source roots.
- [ ] Validate with real package directory containing interdependent bundles.
- [ ] Improve scan memory/performance on very large package folders.

### M2 — Unity serialized object catalog
- [x] AssetStudio-backed object index and Serialized Objects UI added.
- [x] Serialized class/type, name, source file, Path ID, Unity version, and platform shown where available.
- [x] PPtr dependencies enumerated from serialized fields and resolution status displayed.
- [x] Dependency inspector is backed by parsed object references rather than folder-name heuristics.
- [ ] Run against the Nana package and verify realistic cross-bundle resolution, duplicate names, and missing-reference diagnostics.

### M3 — Static geometry preview and first real 3D proof of concept
- [x] Add an interactive OpenGL viewport for indexed standalone Mesh objects, with orbit, pan, zoom, reset, and wireframe controls.
- [x] Extract standalone Mesh vertex/index data from isolated source copies without writing to original bundles.
- [x] Walk a selected GameObject's Transform descendants and combine available MeshFilter / SkinnedMeshRenderer meshes into one static preview, applying their transform hierarchy.
- [ ] Validate both standalone Mesh and GameObject hierarchy previews against the real Nana fixture.
- [ ] Compare bind-pose static geometry against expected model shape; skinning deformation remains disabled.
- [ ] Assign the correct recovered texture and material.
- [ ] Verify rig transforms and coordinate handedness.

### M4 — AnimationClip runtime
- [ ] Recover clip duration and animation curves.
- [ ] Bind to the correct rig.
- [ ] Play/pause/stop/loop/scrub/speed.
- [ ] Verify with at least one real clip and compare against source metadata.

### M5 — Effects runtime
- [ ] Render one recoverable effect.
- [ ] Preserve alpha and blending behavior where recoverable.
- [ ] Support multiple simultaneous effects.
- [ ] Surface missing shaders, textures, and references.

### M6 — Timeline execution
- [ ] Connect timeline events to runtime clips/effects.
- [ ] Playback and loop full sequences.
- [ ] Save/reopen sequence with stable references.
- [ ] Regression-test event ordering and time offsets.

### M7 — Foundation build and release gate
- [x] Windows x64 foundation build and ZIP packaging succeeded in GitHub Actions.
- [x] Automated smoke tests in CI passed for package scanning, SHA-256 inventory, source-preservation checks, export path protection, sequence save/reload, and timeline validation.
- [ ] Manually launch the packaged app on a clean Windows machine.
- [ ] Import and analyze the complete local Nana fixture in the GUI, then verify a selected Mesh in the static viewport.
- [ ] Record source hashes before and after the GUI-level test to confirm the files remain unchanged.
- [x] Document supported/unsupported asset classes and current playback limitations.

## Local Nana fixture details

A real integration candidate is present in the local file library: `Default - Nana 10th Anniversary Script - XirenXCC.zip`. Container inspection confirmed 75 archive entries, 58 files ending in `.unity3d`, and sampled bundle headers beginning with `UnityFS` and reporting Unity `2019.4.33f1`. Representative candidates include:
- `Art/android/h/hero_nana_skin.unity3d` (base hero package)
- `Art/android/h/hero_nana_skin_high_add.unity3d` (additional hero resources)
- `Art/android/n/nana_skill01.unity3d` and `nana_skill01_shifa.unity3d`
- `Art/android/n/nana_skill02.unity3d`, `nana_skill02_shifa.unity3d`, `nana_skill2_hit.unity3d`
- `Art/android/n/nana_skill03_01.unity3d`, `nana_skill03_shifa.unity3d`, and `nana_skill03_sing.unity3d`

These are filenames and header observations only; no serialized object counts, render tests, animation playback, or effects playback have been measured yet. The full archive is not committed to the repo and is not available to GitHub Actions. Run the packaged Windows tool against a working copy of this fixture for the next validation gate.

## Real-package test protocol

Use a package that includes the hero/skinned model, one AnimationClip, the necessary rig/bones, a material/texture, and one skill/effect bundle. Keep a pristine copy and record its hash manifest.

For each test run record:
- selected input bundles and detected Unity version;
- bundle and serialized asset counts;
- mesh/renderer/skeleton names;
- AnimationClip name and duration;
- referenced materials and textures;
- unresolved references;
- effect components and shader requirements;
- expected versus observed viewport behavior;
- before/after source hashes.

If a requested package has no standalone AnimationClip or particle data, report that clearly and use a different representative sample rather than claiming the feature works.


## External technical references

- [SkinForgeID README — community Unity workspace scope](https://github.com/SkinForgeID/MLBB-MODDING-BASE/blob/main/README.md)
- [Juancho reference review and non-copying boundary](REFERENCE_PROJECT_REVIEW.md)
- [Unity AssetBundle platform considerations](https://docs.unity.com/en-us/engine/7000.0/manual/assets-and-media/assets-managing-runtime/assetbundles-section/platforms)
- [Unity AssetBundles introduction and platform version notes](https://docs.unity.cn/2023.1/Documentation/Manual/AssetBundlesIntro.html)
