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
- Open an indexed `Mesh` object in an interactive static geometry viewport with orbit, pan, zoom, and wireframe controls.
- Filter discovered objects by likely role: model geometry, animation, visual effects, material/texture, scene hierarchy, audio, or game-specific component. Role labels are classification hints; preview readiness is shown separately.
- Save a scan report and author/save/reopen manually timed skill-sequence projects.
- Prevent scan reports and new timeline files from being saved inside known imported source roots.

## What is not implemented yet

This is a foundation build, **not yet the complete live simulator**. It does not currently provide:
- a reconstructed, interactive 3D scene for the complete MLBB character hierarchy (only standalone static `Mesh` geometry can currently be viewed);
- real-time AnimationClip playback or rig binding;
- full Unity ParticleSystem / TrailRenderer simulation and custom shader reproduction;
- execution of the skill timeline against animated models and effects.

Those require a separate preview-runtime adapter and validation against an actual multi-bundle MLBB package. A listed reference or resolved pointer does not mean an asset is renderable or its complete gameplay behavior is available.

## Source preservation

Visual Lab inventory and object analysis use temporary copies of selected files; normal scan/index actions do not write to original Unity packages. Save reports and sequences outside imported source folders. Existing **Juancho → Replace Selected Texture2D** is a separate explicit editing feature: saving that texture replacement changes the opened Unity file and retains a `.bak` backup.

Do not use Visual Lab scan/preview actions as a substitute for source backups when using other tools that edit bundle files.

## Platform compatibility

MLBB assets commonly come from Android-targeted bundles. Unity AssetBundles are platform-specific, so do not assume a Windows standalone player can load an Android bundle directly. Visual Lab currently parses serialized data and has not yet connected the real-time renderer. Each target package's Unity version and shader/texture encodings must be inspected before selecting the runtime compatibility profile.

The implementation plan records the runtime approach, acceptance gates, risks, and the review of the external SkinForgeID reference in `docs/MLBB_VISUAL_LAB_IMPLEMENTATION.md` and `docs/REFERENCE_PROJECT_REVIEW.md`.

## Build

The GitHub Actions workflow tests the Visual Lab core, clones upstream AssetStudio, applies Juancho's patches, builds required native texture helpers, and packages a self-contained Windows x64 ZIP for .NET 8. Pull-request builds run CI tests and create an artifact without publishing over the latest release.

## Reference boundary

The SkinForgeID community Unity repository was inspected for high-level requirements such as named animation states, effect composition, and a controlled test scene. Its source code, Unity workspace, shaders, prefabs, scripts, and game assets are not part of this implementation. The YouTube link provided for the reference could not be retrieved as a reliable transcript, so no video-specific behavior is claimed as verified.
