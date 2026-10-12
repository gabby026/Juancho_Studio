# Reference Review: SkinForgeID MLBB MODDING BASE

**Reviewed:** 2026-10-11  
**Reference:** https://github.com/SkinForgeID/MLBB-MODDING-BASE  
**Purpose:** Learn interaction and runtime requirements for the independently implemented Juancho MLBB Visual Lab. This review records observations and design requirements; it does not import or copy the reference project's source code, Unity project, models, textures, shaders, prefabs, scenes, or other bundled assets into Juancho Studio.

## What the reference repository is

The repository describes itself as a community Unity workspace for making and testing MLBB cosmetic assets, with an AssetBundle build workflow and test environment. Its README requests Unity **2019.4.33f1** and Android Build Support. The project contains InGame and InLobby scenes, character model resources in FBX/texture form, a sample Android `.unity3d` bundle, and Unity-side effect/animation component declarations.

It is useful as a reference for building a Unity runtime scene and organizing character/effect objects. It is **not documented as a standalone arbitrary-`.unity3d` reader with an asset dependency browser and an animation/effect timeline editor**. Those remain Juancho's own responsibilities.

## Relevant behavior concepts observed

- **Separate character/effect objects:** the sample project contains model and texture resources separately from the sample bundle. Juancho needs to distinguish the original bundle from any converted preview resources and maintain stable links to source files.
- **Animation and event lifecycle:** component declarations refer to both the legacy `Animation` component and `Animator`, along with clip dictionaries and animation-event delegate fields. This is evidence that simply finding an AnimationClip is not sufficient to reproduce the full skill sequence.
- **Effect composition:** the effect component data references particle systems, animation components, trail renderers, skinned mesh renderers, transforms, per-trail times, follow/bone-follow flags, delay/rotation/position settings, atmosphere data, and team-color data. Juancho's effect model therefore needs to represent effect hierarchies, timing, attachment, trail lifetime, and material/rendering requirements—not only a single particle count.
- **Unity subsystems:** the project manifest includes Unity animation, AssetBundle, particle system, audio, and Timeline modules. That supports investigating a Unity-based preview runtime, but does not prove every MLBB component or shader will reproduce automatically.
- **Scene testing:** the existence of InGame and InLobby scenes demonstrates the value of a repeatable test scene, consistent lighting, and controlled camera movement for visual comparisons.

These are behavioral observations and engineering requirements. Juancho should implement its own models, UI, diagnostics, and runtime bridge.

## Compatibility finding: editor version is not platform compatibility

The Nana fixture's raw bundle headers identify Unity **2019.4.33f1**, which matches the Unity version requested by the reference project. That is useful for choosing a reconstruction target, but it does not mean an Android-built bundle can be loaded directly by a Windows standalone player.

Unity's AssetBundle documentation explains that runtime bundles are platform-specific. The Editor can open bundles from other platforms during development, but individual assets may still fail to load or render when they depend on platform-specific formats such as shaders or texture encodings. See the official [AssetBundle platform considerations](https://docs.unity.com/en-us/engine/7000.0/manual/assets-and-media/assets-managing-runtime/assetbundles-section/platforms) and [AssetBundle introduction](https://docs.unity.cn/2023.1/Documentation/Manual/AssetBundlesIntro.html).

This changes the runtime plan:
- Do not implement the Windows Visual Lab player as only `AssetBundle.LoadFromFile(androidBundle)`.
- Use AssetStudio to parse the Android-targeted serialized objects, preserve their provenance, and reconstruct supported data into a separate preview workspace.
- Build the renderer and sequence player for the chosen desktop runtime, with explicit adapters for mesh/skinning, animation curves, particle properties, material/texture assignment, trails, and transforms.
- Allow a development-only Unity Editor path for compatible experiments, but report platform/shader limitations and do not count an editor-only load as proof of a distributable Windows player.
- Select Unity 2019.4.33f1 as an initial compatibility hypothesis for the Nana fixture, not a universal hard-coded engine version. Verify each package's header and serialized data.

## Design choices for Juancho

1. Keep the current Juancho Studio workflow, parser provenance, and Windows packaging. Do not import the reference project's Unity workspace or binaries.
2. Use AssetStudio parsing only as the current serialized-file reader already present in Juancho Studio. The new Visual Lab indexing and editor code belongs to Juancho's repository and build.
3. Model discovered references as `source object → serialized field → target object`, preserving file identity and Path ID. A resolved pointer is not proof that the target is visually renderable.
4. Reconstruct scene nodes using source transforms, renderers, skeletons, materials, textures, animation curves, and references where the parser exposes those data.
5. Implement an effect-runtime adapter with explicit support states for ParticleSystem, TrailRenderer, Animation, Animator, attachment/follow flags, timing, team colors, and unknown/custom components.
6. The Unity version in the reference project is only a candidate baseline. Verify it against the Unity version metadata and component requirements from the selected test package before committing to a runtime target.
7. Keep all original MLBB packages read-only. Store reconstructed preview data, cache files, and timeline projects in a separate workspace. Do not include MLBB game assets in Juancho releases.

## Video review status

The provided YouTube URL was attempted through the available web access, but the video page could not be fetched and no reliable transcript or exact video metadata was returned. Therefore this document does **not** claim that the video itself was watched. The repository structure and README were inspected directly. If a transcript or screenshots of the relevant playback controls are available, they can be used to refine the UI behavior requirements without copying implementation code.

## Test fixtures

A Nana 10th Anniversary package already available in the user's files is suitable for later local testing. Its ZIP inventory contains 58 `.unity3d` bundles, including `hero_nana_skin.unity3d`, `hero_nana_skin_high_add.unity3d`, hero-form resources, and separately named Skill 1/2/3, cast, warning, spread, and impact-style bundles. It is a useful integration fixture because it exercises multiple related bundles. Do not commit the package or its game assets to the source repository.

## Next acceptance gate

Before attempting a complete skill simulation, pass a staged test with this local fixture:

1. Parse all relevant bundles using the current AssetStudio reader.
2. List real serialized objects with class, name, source file, and Path ID.
3. Resolve and report cross-bundle PPtr references.
4. Identify the hero renderer/skeleton and one AnimationClip, if present.
5. Identify one effect hierarchy and its particle/trail/material/texture dependencies, if present.
6. Render one object, play one clip, and preview one effect in a separate runtime workspace.
7. Record the original source hashes before and after and verify they did not change.

Only after that should the tool claim that it can play a given class of MLBB animation or effect.
