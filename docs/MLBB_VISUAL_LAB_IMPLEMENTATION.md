# Juancho MLBB Visual Lab — Implementation Plan

## Mission

Extend the existing Juancho Studio / AssetStudio build overlay into a Windows desktop tool that can inventory real MLBB Unity packages, reconstruct supported assets in a 3D runtime, play AnimationClips, preview visual effects, and manually author skill sequences. Preserve the upstream AssetStudio workflow and keep imported source packages unchanged during analysis.

## Repository baseline (audited 2026-10-11)

- Repository: `gabby026/Juancho_Studio`, default branch `main`.
- The repository is an overlay/build project, not a full vendored copy of AssetStudio. Its GitHub Actions build clones upstream `perfare/AssetStudio`, applies `build/patch.ps1`, builds native texture helpers, and publishes a Windows x64 ZIP.
- Existing feature: `Juancho → Replace Selected Texture2D`, which edits the opened asset file and keeps a backup. Visual Lab must be a separate read-only analysis path; do not route its scan or preview code through this write-back feature.
- The build workflow currently targets .NET 8 Windows and runs on `windows-latest`.
- The repository did not contain a real full MLBB hero/skill test package at this audit. Runtime compatibility cannot be asserted until a sample package with its related bundles is tested.

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

Next implementation milestone:
1. Load each supported bundle/file through the existing AssetStudio/AssetsTools reader.
2. Record each serialized object's source file, path ID, class/type, name, and parse status.
3. Resolve PPtr references across all selected bundles using file identity plus path ID.
4. Keep duplicate display names distinct by stable source identity.
5. Flag unresolved pointers and unsupported serialized types.
6. Cache the index in a sidecar file with a versioned schema; never write it back into a source bundle.

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
- [x] Add source-protection tests.
- [ ] Run the Windows CI build and resolve all failures.

### M1 — Package inventory
- [x] Folder scan and multi-file scan.
- [x] Hashes, categories, folder heuristics, duplicate detection.
- [x] Report export outside source roots.
- [ ] Validate with real package directory containing interdependent bundles.
- [ ] Improve scan memory/performance on very large package folders.

### M2 — Unity serialized object catalog
- [ ] AssetStudio/AssetsTools-backed object index.
- [ ] Cross-bundle references resolved by identity/path ID.
- [ ] Accurate asset type and object names.
- [ ] Dependency inspector backed by serialized references, not folder guesses.

### M3 — First real 3D proof of concept
- [ ] Render one real MLBB character model.
- [ ] Assign the correct recovered texture and material.
- [ ] Add orbit, pan, zoom and reset.
- [ ] Verify scene transforms and coordinate handedness.

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

### M7 — Windows release
- [ ] Clean x64 build.
- [ ] Automated smoke tests in CI.
- [ ] Manual test on a clean Windows machine.
- [ ] No source mutation; diagnostics and sample sequence verified.
- [ ] Document supported/unsupported asset classes.

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
