# Juancho Studio

Juancho's Windows x64 AssetStudio build with an independently implemented **MLBB Visual Lab** foundation.

## Visual Lab — current foundation

Open **AssetStudioGUI.exe**, then choose **MLBB Visual Lab** from the menu.

Current capabilities:
- Scan a folder or import multiple Unity bundle/serialized asset files.
- Create a read-only inventory with file hashes, sizes, duplicate detection, source paths, and folder-group hints.
- Parse selected Unity inputs using isolated temporary copies.
- Inspect serialized object names, Unity classes, source file, Path ID, Unity version, platform, and object-reference counts.
- Inspect resolved and unresolved PPtr dependencies across the selected package.
- Inspect AnimationClip duration, sample rate, curve bindings/key counts, embedded events, and playback-readiness diagnostics.
- Preview one static `Mesh` or combine an indexed `GameObject` hierarchy into an interactive 3D viewport.
- The interactive player specifically discovers `hero_<heroname>_skin.unity3d` and requires that same bundle to contain a `Mesh`, a `GameObject` hierarchy, and `AnimationClip` objects. It lists model roots and all AnimationClips from that hero-skin bundle only; selecting another clip loads its preview so you can inspect skills individually. Playback supports play/pause/stop, scrubbing, loop, speed, orbit/pan/zoom, and best-effort CPU skinning from recovered weights, bind poses, bone references, and hierarchy transforms. It decodes direct curves plus supported streamed, dense, constant and compressed-rotation channels.
- Filter discovered objects by likely role: model geometry, animation, visual effects, material/texture, scene hierarchy, audio, or game-specific component. Role labels are classification hints; preview readiness is shown separately.
- Save a scan report and author/save/reopen manually timed skill-sequence projects.
- Prevent scan reports and new timeline files from being saved inside known imported source roots.

## What is not implemented yet

This is a foundation build, **not yet the complete live simulator**. Interactive playback is an initial reconstruction adapter, not a replacement for Unity's full Animator. It does not yet:
- render recovered materials, textures, UV animation, or the original MLBB shader pipeline;
- guarantee correct skinned-mesh binding when bone references or dependencies are missing/mismatched; the first CPU skinning adapter is still unvalidated against a real MLBB package;
- reproduce Unity's exact curve tangents/interpolation, AnimatorControllers, or Humanoid/muscle-only clips requiring Avatar reconstruction;
- simulate Unity ParticleSystem / TrailRenderer effects;
- play particle/trail effects or the saved skill timeline as an executable multi-track sequence.

The separate static Mesh/GameObject preview remains static; use the **Interactive Model + Animation Player** for supported curve playback and best-effort CPU skinning. A listed reference or resolved pointer does not mean an asset is fully renderable or its gameplay behavior is available.

## Source preservation

Visual Lab inventory and object analysis use temporary copies of selected files; normal scan/index/preview actions do not write to original Unity packages. Save reports and sequences outside imported source folders. Existing **Juancho → Replace Selected Texture2D** is a separate explicit editing feature: saving that texture replacement changes the opened Unity file and retains a `.bak` backup.

Do not use Visual Lab scan/preview actions as a substitute for source backups when using other tools that edit bundle files.

## Platform compatibility

MLBB assets commonly come from Android-targeted bundles. Unity AssetBundles are platform-specific, so do not assume a Windows standalone player can load an Android bundle directly. Visual Lab parses serialized data and reconstructs supported model/animation subsets in its desktop preview; shader, texture, skeleton, and target Unity-version compatibility must be validated for each package.

The implementation plan records the runtime approach, acceptance gates, risks, and the review of the external SkinForgeID reference in `docs/MLBB_VISUAL_LAB_IMPLEMENTATION.md` and `docs/REFERENCE_PROJECT_REVIEW.md`.

## Build

The GitHub Actions workflow tests the Visual Lab core, clones upstream AssetStudio, applies Juancho's patches, builds required native texture helpers, and packages a self-contained Windows x64 ZIP for .NET 8. Pull-request builds run CI tests and create an artifact without publishing over the latest release.

## Reference boundary

The SkinForgeID community Unity repository was inspected for high-level requirements such as named animation states, effect composition, and a controlled test scene. Its source code, Unity workspace, shaders, prefabs, scripts, and game assets are not part of this implementation. The YouTube link provided for the reference could not be retrieved as a reliable transcript, so no video-specific behavior is claimed as verified.
