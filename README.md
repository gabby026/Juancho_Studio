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
- Open **Interactive Model + Animation Player**, choose a `GameObject` model root and an `AnimationClip`, then play/pause/stop, scrub, loop, and change playback speed. It evaluates directly keyed position/scale/rotation curves and compressed rotation keys where available; supported skinned meshes are CPU-deformed from recovered skin weights, bind poses, bone references, and the evaluated hierarchy.
- Filter discovered objects by likely role: model geometry, animation, visual effects, material/texture, scene hierarchy, audio, or game-specific component. Role labels are classification hints; preview readiness is shown separately.
- Save a scan report and author/save/reopen manually timed skill-sequence projects.
- Prevent scan reports and new timeline files from being saved inside known imported source roots.

## What is not implemented yet

This is a foundation build, **not yet the complete live simulator**. Interactive playback is an initial reconstruction adapter, not a replacement for Unity's full Animator. It does not yet:
- render recovered materials, textures, UV animation, or the original MLBB shader pipeline;
- deform skinned meshes using the correct skeleton, bind poses, and animation curves;
- play AnimationClips in real time;
- simulate Unity ParticleSystem / TrailRenderer effects;
- play particle/trail effects or the saved skill timeline as an executable multi-track sequence.

Skinned meshes included by the current static GameObject preview are shown without bone deformation. A listed reference or resolved pointer does not mean an asset is fully renderable or its gameplay behavior is available.

## Source preservation

Visual Lab inventory and object analysis use temporary copies of selected files; normal scan/index/preview actions do not write to original Unity packages. Save reports and sequences outside imported source folders. Existing **Juancho → Replace Selected Texture2D** is a separate explicit editing feature: saving that texture replacement changes the opened Unity file and retains a `.bak` backup.

Do not use Visual Lab scan/preview actions as a substitute for source backups when using other tools that edit bundle files.

## Platform compatibility

MLBB assets commonly come from Android-targeted bundles. Unity AssetBundles are platform-specific, so do not assume a Windows standalone player can load an Android bundle directly. Visual Lab parses serialized data and reconstructs the static geometry subset; shader, texture, and target Unity-version compatibility must be validated for each package.

The implementation plan records the runtime approach, acceptance gates, risks, and the review of the external SkinForgeID reference in `docs/MLBB_VISUAL_LAB_IMPLEMENTATION.md` and `docs/REFERENCE_PROJECT_REVIEW.md`.

## Build

The GitHub Actions workflow tests the Visual Lab core, clones upstream AssetStudio, applies Juancho's patches, builds required native texture helpers, and packages a self-contained Windows x64 ZIP for .NET 8. Pull-request builds run CI tests and create an artifact without publishing over the latest release.

## Reference boundary

The SkinForgeID community Unity repository was inspected for high-level requirements such as named animation states, effect composition, and a controlled test scene. Its source code, Unity workspace, shaders, prefabs, scripts, and game assets are not part of this implementation. The YouTube link provided for the reference could not be retrieved as a reliable transcript, so no video-specific behavior is claimed as verified.
