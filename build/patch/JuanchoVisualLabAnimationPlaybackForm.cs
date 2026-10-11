using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows.Forms;
using AssetStudio;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using UnityObject = AssetStudio.Object;
using NMatrix = System.Numerics.Matrix4x4;
using NQuaternion = System.Numerics.Quaternion;
using NVector3 = System.Numerics.Vector3;

namespace Juancho.VisualLab
{
    public sealed class PlaybackNode
    {
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public int ParentIndex { get; set; } = -1;
        public NVector3 Position { get; set; }
        public NQuaternion Rotation { get; set; } = NQuaternion.Identity;
        public NVector3 Scale { get; set; } = NVector3.One;
    }

    public sealed class PlaybackVectorKey
    {
        public float Time { get; set; }
        public NVector3 Value { get; set; }
    }

    public sealed class PlaybackQuaternionKey
    {
        public float Time { get; set; }
        public NQuaternion Value { get; set; }
    }

    public sealed class PlaybackTransformTrack
    {
        public string Path { get; set; } = "";
        public PlaybackVectorKey[] Positions { get; set; } = Array.Empty<PlaybackVectorKey>();
        public PlaybackQuaternionKey[] Rotations { get; set; } = Array.Empty<PlaybackQuaternionKey>();
        public PlaybackVectorKey[] EulerRotations { get; set; } = Array.Empty<PlaybackVectorKey>();
        public PlaybackVectorKey[] Scales { get; set; } = Array.Empty<PlaybackVectorKey>();
    }

    public sealed class PlaybackMeshPart
    {
        public string Name { get; set; } = "";
        public int NodeIndex { get; set; } = -1;
        public int VertexOffset { get; set; }
        public bool IsSkinned { get; set; }
        public float[] Vertices { get; set; } = Array.Empty<float>();
        public float[] Normals { get; set; } = Array.Empty<float>();
        public uint[] Indices { get; set; } = Array.Empty<uint>();
        public float[] Weights { get; set; } = Array.Empty<float>();
        public int[] BoneIndices { get; set; } = Array.Empty<int>();
        public int[] BoneNodeIndices { get; set; } = Array.Empty<int>();
        public NMatrix[] BindPoses { get; set; } = Array.Empty<NMatrix>();
    }

    public sealed class VisualLabPlaybackScene
    {
        public string ModelName { get; set; } = "";
        public string ClipName { get; set; } = "";
        public float Duration { get; set; }
        public float SampleRate { get; set; }
        public int ClipEventCount { get; set; }
        public string PlaybackNotes { get; set; } = "";
        public List<PlaybackNode> Nodes { get; set; } = new List<PlaybackNode>();
        public List<PlaybackTransformTrack> Tracks { get; set; } = new List<PlaybackTransformTrack>();
        public List<PlaybackMeshPart> MeshParts { get; set; } = new List<PlaybackMeshPart>();
        public float[] Positions { get; set; } = Array.Empty<float>();
        public float[] Normals { get; set; } = Array.Empty<float>();
        public uint[] Indices { get; set; } = Array.Empty<uint>();
        public int VertexCount { get; set; }
        public int TriangleCount { get; set; }
        public int DirectCurveCount { get; set; }
        public int SkinnedPartCount { get; set; }
        public int UnboundCurveCount { get; set; }

        public void Evaluate(float timeSeconds, bool loop)
        {
            if (Nodes.Count == 0 || MeshParts.Count == 0) return;
            var duration = Math.Max(Duration, 0.0001f);
            var time = loop ? (float)(timeSeconds % duration) : Math.Max(0, Math.Min(duration, timeSeconds));
            var positions = new NVector3[Nodes.Count];
            var rotations = new NQuaternion[Nodes.Count];
            var scales = new NVector3[Nodes.Count];
            var worlds = new NMatrix[Nodes.Count];

            for (int i = 0; i < Nodes.Count; i++)
            {
                var node = Nodes[i];
                var p = node.Position;
                var r = node.Rotation;
                var s = node.Scale;
                var track = Tracks.FirstOrDefault(t => string.Equals(t.Path, node.Path, StringComparison.Ordinal));
                if (track != null)
                {
                    p = SampleVector(track.Positions, time, p);
                    s = SampleVector(track.Scales, time, s);
                    if (track.Rotations.Length > 0)
                        r = SampleQuaternion(track.Rotations, time, r);
                    else if (track.EulerRotations.Length > 0)
                    {
                        var euler = SampleVector(track.EulerRotations, time, NVector3.Zero) * (float)(Math.PI / 180.0);
                        r = NQuaternion.CreateFromYawPitchRoll(euler.Y, euler.X, euler.Z);
                    }
                }
                positions[i] = p;
                rotations[i] = NormalizeOrIdentity(r);
                scales[i] = s;
                var local = NMatrix.CreateScale(s) * NMatrix.CreateFromQuaternion(rotations[i]) * NMatrix.CreateTranslation(p);
                worlds[i] = node.ParentIndex >= 0 && node.ParentIndex < i
                    ? local * worlds[node.ParentIndex]
                    : local;
            }

            foreach (var part in MeshParts)
            {
                var nodeWorld = part.NodeIndex >= 0 && part.NodeIndex < worlds.Length ? worlds[part.NodeIndex] : NMatrix.Identity;
                var isActuallySkinned = part.IsSkinned &&
                    part.Weights.Length == part.Vertices.Length / 3 * 4 &&
                    part.BoneIndices.Length == part.Weights.Length &&
                    part.BoneNodeIndices.Length > 0 &&
                    part.BindPoses.Length > 0;

                for (int vertex = 0; vertex < part.Vertices.Length / 3; vertex++)
                {
                    var localPosition = new NVector3(part.Vertices[vertex * 3], part.Vertices[vertex * 3 + 1], part.Vertices[vertex * 3 + 2]);
                    var localNormal = new NVector3(part.Normals[vertex * 3], part.Normals[vertex * 3 + 1], part.Normals[vertex * 3 + 2]);
                    NVector3 p = NVector3.Zero;
                    NVector3 n = NVector3.Zero;
                    float totalWeight = 0;

                    if (isActuallySkinned)
                    {
                        for (int influence = 0; influence < 4; influence++)
                        {
                            var slot = vertex * 4 + influence;
                            var weight = part.Weights[slot];
                            var boneId = part.BoneIndices[slot];
                            if (weight <= 0 || boneId < 0 || boneId >= part.BindPoses.Length || boneId >= part.BoneNodeIndices.Length)
                                continue;
                            var boneNode = part.BoneNodeIndices[boneId];
                            if (boneNode < 0 || boneNode >= worlds.Length) continue;
                            var skinMatrix = part.BindPoses[boneId] * worlds[boneNode];
                            p += NVector3.Transform(localPosition, skinMatrix) * weight;
                            n += NVector3.TransformNormal(localNormal, skinMatrix) * weight;
                            totalWeight += weight;
                        }
                    }

                    if (!isActuallySkinned || totalWeight < 0.0001f)
                    {
                        p = NVector3.Transform(localPosition, nodeWorld);
                        n = NVector3.TransformNormal(localNormal, nodeWorld);
                    }
                    else if (totalWeight < 0.9999f)
                    {
                        var fallback = 1 - totalWeight;
                        p += NVector3.Transform(localPosition, nodeWorld) * fallback;
                        n += NVector3.TransformNormal(localNormal, nodeWorld) * fallback;
                    }

                    if (n.LengthSquared() > 1e-10f) n = NVector3.Normalize(n);
                    else n = NVector3.UnitY;
                    var target = (part.VertexOffset + vertex) * 3;
                    Positions[target] = IsFinite(p.X) ? p.X : 0;
                    Positions[target + 1] = IsFinite(p.Y) ? p.Y : 0;
                    Positions[target + 2] = IsFinite(p.Z) ? p.Z : 0;
                    Normals[target] = n.X;
                    Normals[target + 1] = n.Y;
                    Normals[target + 2] = n.Z;
                }
            }
        }

        private static NVector3 SampleVector(PlaybackVectorKey[] keys, float time, NVector3 fallback)
        {
            if (keys == null || keys.Length == 0) return fallback;
            if (time <= keys[0].Time) return keys[0].Value;
            if (time >= keys[keys.Length - 1].Time) return keys[keys.Length - 1].Value;
            int next = Array.FindIndex(keys, x => x.Time >= time);
            if (next <= 0) return keys[0].Value;
            var a = keys[next - 1]; var b = keys[next];
            var span = Math.Max(0.000001f, b.Time - a.Time);
            return NVector3.Lerp(a.Value, b.Value, Math.Max(0, Math.Min(1, (time - a.Time) / span)));
        }

        private static NQuaternion SampleQuaternion(PlaybackQuaternionKey[] keys, float time, NQuaternion fallback)
        {
            if (keys == null || keys.Length == 0) return fallback;
            if (time <= keys[0].Time) return NormalizeOrIdentity(keys[0].Value);
            if (time >= keys[keys.Length - 1].Time) return NormalizeOrIdentity(keys[keys.Length - 1].Value);
            int next = Array.FindIndex(keys, x => x.Time >= time);
            if (next <= 0) return NormalizeOrIdentity(keys[0].Value);
            var a = keys[next - 1]; var b = keys[next];
            var span = Math.Max(0.000001f, b.Time - a.Time);
            return NQuaternion.Slerp(NormalizeOrIdentity(a.Value), NormalizeOrIdentity(b.Value),
                Math.Max(0, Math.Min(1, (time - a.Time) / span)));
        }

        private static NQuaternion NormalizeOrIdentity(NQuaternion q)
        {
            if (!IsFinite(q.X) || !IsFinite(q.Y) || !IsFinite(q.Z) || !IsFinite(q.W) || q.LengthSquared() < 1e-10f)
                return NQuaternion.Identity;
            return NQuaternion.Normalize(q);
        }

        private static bool IsFinite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
    }

    /// <summary>
    /// Extracts a read-only GameObject hierarchy, mesh skin data, and directly keyed AnimationClip curves.
    /// It deliberately marks muscle-only clips as unavailable rather than fabricating a pose.
    /// </summary>
    public static class VisualLabAnimationPlaybackLoader
    {
        private static readonly System.Reflection.BindingFlags Fields =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

        public static Task<VisualLabPlaybackScene> LoadAsync(
            IEnumerable<string> sourcePaths, VisualLabUnityAssetItem rootItem, VisualLabUnityAssetItem clipItem)
        {
            if (rootItem == null || !string.Equals(rootItem.ClassName, "GameObject", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choose a GameObject as the model root.", nameof(rootItem));
            if (clipItem == null || !string.Equals(clipItem.ClassName, "AnimationClip", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choose an AnimationClip to play.", nameof(clipItem));
            var inputs = (sourcePaths ?? Enumerable.Empty<string>())
                .Where(File.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (inputs.Length == 0) throw new ArgumentException("No scanned package files are still available.", nameof(sourcePaths));
            return Task.Run(() => Load(inputs, rootItem, clipItem));
        }

        private static VisualLabPlaybackScene Load(string[] inputs, VisualLabUnityAssetItem rootItem, VisualLabUnityAssetItem clipItem)
        {
            var manager = new AssetsManager();
            var workspace = Path.Combine(Path.GetTempPath(), "JuanchoMLBBVisualLab", Guid.NewGuid().ToString("N"));
            var workspaceToOriginal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                Directory.CreateDirectory(workspace);
                var isolated = CopyToWorkspace(inputs, workspace, workspaceToOriginal);
                MergeSplitParts(workspace);
                manager.LoadFiles(isolated);

                var root = FindObject<AssetStudio.GameObject>(manager, workspaceToOriginal, inputs, rootItem.SourceBundlePath, rootItem.PathId, workspace);
                var clip = FindObject<AssetStudio.AnimationClip>(manager, workspaceToOriginal, inputs, clipItem.SourceBundlePath, clipItem.PathId, workspace);
                if (root == null) throw new InvalidDataException("The selected GameObject could not be reloaded from this package. Re-scan, then select it again.");
                if (clip == null) throw new InvalidDataException("The selected AnimationClip could not be reloaded from this package. Re-scan, then select it again.");
                var rootTransform = FindTransform(root);
                if (rootTransform == null) throw new InvalidDataException("The selected GameObject has no resolvable Transform.");

                var scene = new VisualLabPlaybackScene { ModelName = string.IsNullOrWhiteSpace(root.m_Name) ? rootItem.Name : root.m_Name };
                var nodeByIdentity = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var transforms = new List<AssetStudio.Transform>();
                BuildNodes(rootTransform, -1, "", scene, transforms, nodeByIdentity, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);
                if (scene.Nodes.Count == 0) throw new InvalidDataException("The selected GameObject hierarchy contained no usable Transform nodes.");
                AddMeshes(scene, transforms, nodeByIdentity, rootTransform, workspaceToOriginal, inputs, workspace);

                BuildClip(scene, clip, clipItem);
                if (scene.MeshParts.Count == 0) throw new InvalidDataException("No usable MeshFilter or SkinnedMeshRenderer geometry was found below the selected root. Choose the actual hero model root, not a parent/helper object.");
                scene.VertexCount = scene.MeshParts.Sum(x => x.Vertices.Length / 3);
                scene.TriangleCount = scene.MeshParts.Sum(x => x.Indices.Length / 3);
                scene.SkinnedPartCount = scene.MeshParts.Count(x => x.IsSkinned);
                scene.Positions = new float[scene.VertexCount * 3];
                scene.Normals = new float[scene.VertexCount * 3];
                var allIndices = new List<uint>();
                int offset = 0;
                foreach (var part in scene.MeshParts)
                {
                    part.VertexOffset = offset;
                    foreach (var index in part.Indices) allIndices.Add((uint)offset + index);
                    offset += part.Vertices.Length / 3;
                }
                scene.Indices = allIndices.ToArray();
                scene.Evaluate(0, false);
                var partsWithSkinWeights = scene.MeshParts.Count(x => x.IsSkinned && x.Weights.Length == x.Vertices.Length / 3 * 4 && x.BindPoses.Length > 0);
                scene.PlaybackNotes =
                    "Direct Transform curves: " + scene.DirectCurveCount +
                    "; curve paths not found in selected hierarchy: " + scene.UnboundCurveCount +
                    "; skinned mesh parts: " + scene.SkinnedPartCount +
                    "; parts with usable skin weights: " + partsWithSkinWeights +
                    "; clip events: " + scene.ClipEventCount + ". " +
                    (scene.DirectCurveCount == 0
                        ? "This clip has no directly keyed Transform curves supported by this player. Humanoid/muscle-only or controller-driven playback is not yet supported for this clip."
                        : "Preview uses linear vector interpolation and quaternion slerp between serialized key values; original tangents, Avatar muscle reconstruction, material animation and skill events are not simulated.");
                return scene;
            }
            catch (Exception ex) when (!(ex is InvalidDataException))
            {
                throw new InvalidDataException("Could not build the interactive model/AnimationClip preview from isolated package copies. Source files were not changed. Details: " + ex.Message, ex);
            }
            finally
            {
                try { manager.Clear(); } catch { }
                try { if (Directory.Exists(workspace)) Directory.Delete(workspace, true); } catch { }
            }
        }

        private static void BuildNodes(
            AssetStudio.Transform transform, int parentIndex, string path, VisualLabPlaybackScene scene,
            List<AssetStudio.Transform> transforms, Dictionary<string, int> nodeByIdentity, HashSet<string> visited, int depth)
        {
            if (transform == null) return;
            if (depth > 512) throw new InvalidDataException("Transform hierarchy depth exceeded safety limit.");
            var key = Identity(transform);
            if (!visited.Add(key)) return;
            string name = transform.m_GameObject != null && transform.m_GameObject.TryGet(out AssetStudio.GameObject go) && go != null
                ? (go.m_Name ?? "")
                : "Transform_" + transform.m_PathID;
            string ownPath = parentIndex < 0 ? "" : (string.IsNullOrEmpty(path) ? name : path + "/" + name);
            int nodeIndex = scene.Nodes.Count;
            var p = transform.m_LocalPosition;
            var r = transform.m_LocalRotation;
            var s = transform.m_LocalScale;
            scene.Nodes.Add(new PlaybackNode
            {
                Name = name, Path = ownPath, ParentIndex = parentIndex,
                Position = new NVector3(p.X, p.Y, p.Z),
                Rotation = Normalize(new NQuaternion(r.X, r.Y, r.Z, r.W)),
                Scale = new NVector3(s.X, s.Y, s.Z)
            });
            transforms.Add(transform);
            nodeByIdentity[key] = nodeIndex;
            foreach (var pointer in transform.m_Children ?? Array.Empty<PPtr<AssetStudio.Transform>>())
                if (pointer != null && pointer.TryGet(out AssetStudio.Transform child) && child != null)
                    BuildNodes(child, nodeIndex, ownPath, scene, transforms, nodeByIdentity, visited, depth + 1);
        }

        private static void AddMeshes(
            VisualLabPlaybackScene scene, List<AssetStudio.Transform> transforms,
            Dictionary<string, int> nodeByIdentity, AssetStudio.Transform root,
            Dictionary<string, string> workspaceToOriginal, string[] inputs, string workspace)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int sourceVertices = 0;
            foreach (var transform in transforms)
            {
                var nodeIndex = nodeByIdentity[Identity(transform)];
                if (transform.m_GameObject == null || !transform.m_GameObject.TryGet(out AssetStudio.GameObject gameObject) || gameObject == null) continue;
                foreach (var pointer in gameObject.m_Components ?? Array.Empty<PPtr<AssetStudio.Component>>())
                {
                    if (pointer == null || !pointer.TryGet(out AssetStudio.Component component) || component == null) continue;
                    AssetStudio.Mesh mesh = null;
                    AssetStudio.SkinnedMeshRenderer skinned = null;
                    if (component is AssetStudio.MeshFilter filter) filter.m_Mesh?.TryGet(out mesh);
                    else if (component is AssetStudio.SkinnedMeshRenderer skinRenderer)
                    {
                        skinRenderer.m_Mesh?.TryGet(out mesh);
                        skinned = skinRenderer;
                    }
                    if (mesh == null) continue;
                    var meshKey = Identity(transform) + "|" + Identity(mesh);
                    if (!seen.Add(meshKey)) continue;
                    if (mesh.m_VertexCount <= 0 || mesh.m_Vertices == null || mesh.m_Vertices.Length < mesh.m_VertexCount * 3) continue;
                    sourceVertices += mesh.m_VertexCount;
                    if (sourceVertices > 1500000) throw new InvalidDataException("The hierarchy exceeds 1,500,000 vertices. Select a smaller model root.");
                    var stride = mesh.m_Vertices.Length / mesh.m_VertexCount;
                    if (stride < 3) continue;
                    var vertices = new float[mesh.m_VertexCount * 3];
                    for (int i = 0; i < mesh.m_VertexCount; i++)
                        for (int axis = 0; axis < 3; axis++)
                            vertices[i * 3 + axis] = mesh.m_Vertices[i * stride + axis];
                    var indices = (mesh.m_Indices ?? new List<uint>()).Where(x => x < mesh.m_VertexCount).ToArray();
                    int usable = indices.Length - indices.Length % 3;
                    if (usable < 3) continue;
                    if (usable != indices.Length) Array.Resize(ref indices, usable);
                    var normals = ReadNormals(mesh.m_Normals, mesh.m_VertexCount);
                    if (normals == null) normals = MakeNormals(vertices, indices);
                    var part = new PlaybackMeshPart
                    {
                        Name = string.IsNullOrWhiteSpace(mesh.m_Name) ? gameObject.m_Name : mesh.m_Name,
                        NodeIndex = nodeIndex,
                        IsSkinned = skinned != null,
                        Vertices = vertices, Normals = normals, Indices = indices
                    };
                    if (skinned != null && mesh.m_Skin != null && mesh.m_Skin.Length == mesh.m_VertexCount && mesh.m_BindPose != null && mesh.m_BindPose.Length > 0)
                    {
                        part.Weights = new float[mesh.m_VertexCount * 4];
                        part.BoneIndices = new int[mesh.m_VertexCount * 4];
                        for (int v = 0; v < mesh.m_VertexCount; v++)
                            for (int k = 0; k < 4; k++)
                            {
                                part.Weights[v * 4 + k] = mesh.m_Skin[v].weight[k];
                                part.BoneIndices[v * 4 + k] = mesh.m_Skin[v].boneIndex[k];
                            }
                        part.BindPoses = mesh.m_BindPose.Select(ToNumerics).ToArray();
                        var bones = skinned.m_Bones ?? Array.Empty<PPtr<AssetStudio.Transform>>();
                        part.BoneNodeIndices = bones.Select(b =>
                        {
                            if (b != null && b.TryGet(out AssetStudio.Transform bone) && bone != null && nodeByIdentity.TryGetValue(Identity(bone), out int index)) return index;
                            return -1;
                        }).ToArray();
                    }
                    scene.MeshParts.Add(part);
                }
            }
        }

        private static void BuildClip(VisualLabPlaybackScene scene, AssetStudio.AnimationClip clip, VisualLabUnityAssetItem item)
        {
            scene.ClipName = string.IsNullOrWhiteSpace(clip.m_Name) ? item.Name : clip.m_Name;
            scene.SampleRate = clip.m_SampleRate > 0 && IsFinite(clip.m_SampleRate) ? clip.m_SampleRate : 30;
            float duration = 0;
            foreach (var c in clip.m_PositionCurves ?? Array.Empty<AssetStudio.Vector3Curve>())
                duration = Math.Max(duration, MaxTime(c.curve?.m_Curve?.Select(k => k.time)));
            foreach (var c in clip.m_RotationCurves ?? Array.Empty<AssetStudio.QuaternionCurve>())
                duration = Math.Max(duration, MaxTime(c.curve?.m_Curve?.Select(k => k.time)));
            foreach (var c in clip.m_EulerCurves ?? Array.Empty<AssetStudio.Vector3Curve>())
                duration = Math.Max(duration, MaxTime(c.curve?.m_Curve?.Select(k => k.time)));
            foreach (var c in clip.m_ScaleCurves ?? Array.Empty<AssetStudio.Vector3Curve>())
                duration = Math.Max(duration, MaxTime(c.curve?.m_Curve?.Select(k => k.time)));
            foreach (var evt in clip.m_Events ?? Array.Empty<AssetStudio.AnimationEvent>())
                if (evt != null && IsFinite(evt.time)) duration = Math.Max(duration, evt.time);
            if (clip.m_MuscleClip != null && IsFinite(clip.m_MuscleClip.m_StopTime))
                duration = Math.Max(duration, clip.m_MuscleClip.m_StopTime);
            scene.ClipEventCount = clip.m_Events?.Length ?? 0;
            scene.Duration = Math.Max(duration, scene.SampleRate > 0 ? 1f / scene.SampleRate : 0.0333f);

            var byPath = new Dictionary<string, PlaybackTransformTrack>(StringComparer.Ordinal);
            PlaybackTransformTrack Track(string path)
            {
                path = path ?? "";
                if (!byPath.TryGetValue(path, out var track))
                {
                    track = new PlaybackTransformTrack { Path = path };
                    byPath[path] = track;
                }
                return track;
            }

            foreach (var c in clip.m_PositionCurves ?? Array.Empty<AssetStudio.Vector3Curve>())
            {
                var keys = (c.curve?.m_Curve ?? Array.Empty<AssetStudio.Keyframe<AssetStudio.Vector3>>())
                    .Where(k => k != null && IsFinite(k.time))
                    .OrderBy(k => k.time).Select(k => new PlaybackVectorKey { Time = k.time, Value = new NVector3(k.value.X, k.value.Y, k.value.Z) }).ToArray();
                if (keys.Length > 0) { Track(c.path).Positions = keys; scene.DirectCurveCount++; }
            }
            foreach (var c in clip.m_ScaleCurves ?? Array.Empty<AssetStudio.Vector3Curve>())
            {
                var keys = (c.curve?.m_Curve ?? Array.Empty<AssetStudio.Keyframe<AssetStudio.Vector3>>())
                    .Where(k => k != null && IsFinite(k.time))
                    .OrderBy(k => k.time).Select(k => new PlaybackVectorKey { Time = k.time, Value = new NVector3(k.value.X, k.value.Y, k.value.Z) }).ToArray();
                if (keys.Length > 0) { Track(c.path).Scales = keys; scene.DirectCurveCount++; }
            }
            foreach (var c in clip.m_EulerCurves ?? Array.Empty<AssetStudio.Vector3Curve>())
            {
                var keys = (c.curve?.m_Curve ?? Array.Empty<AssetStudio.Keyframe<AssetStudio.Vector3>>())
                    .Where(k => k != null && IsFinite(k.time))
                    .OrderBy(k => k.time).Select(k => new PlaybackVectorKey { Time = k.time, Value = new NVector3(k.value.X, k.value.Y, k.value.Z) }).ToArray();
                if (keys.Length > 0 && Track(c.path).Rotations.Length == 0) { Track(c.path).EulerRotations = keys; scene.DirectCurveCount++; }
            }
            foreach (var c in clip.m_RotationCurves ?? Array.Empty<AssetStudio.QuaternionCurve>())
            {
                var keys = (c.curve?.m_Curve ?? Array.Empty<AssetStudio.Keyframe<AssetStudio.Quaternion>>())
                    .Where(k => k != null && IsFinite(k.time))
                    .OrderBy(k => k.time).Select(k => new PlaybackQuaternionKey
                    {
                        Time = k.time, Value = Normalize(new NQuaternion(k.value.X, k.value.Y, k.value.Z, k.value.W))
                    }).ToArray();
                if (keys.Length > 0) { Track(c.path).Rotations = keys; scene.DirectCurveCount++; }
            }
            foreach (var c in clip.m_CompressedRotationCurves ?? Array.Empty<AssetStudio.CompressedAnimationCurve>())
            {
                try
                {
                    var times = c.m_Times?.UnpackInts() ?? Array.Empty<int>();
                    var quaternions = c.m_Values?.UnpackQuats() ?? Array.Empty<AssetStudio.Quaternion>();
                    int count = Math.Min(times.Length, quaternions.Length);
                    if (count == 0) continue;
                    double maxPacked = Math.Pow(2, Math.Max(1, (int)c.m_Times.m_BitSize)) - 1;
                    var keys = new PlaybackQuaternionKey[count];
                    for (int i = 0; i < count; i++)
                        keys[i] = new PlaybackQuaternionKey
                        {
                            Time = Math.Max(0, Math.Min(scene.Duration, (float)(times[i] / maxPacked * scene.Duration))),
                            Value = Normalize(new NQuaternion(quaternions[i].X, quaternions[i].Y, quaternions[i].Z, quaternions[i].W))
                        };
                    keys = keys.OrderBy(k => k.Time).ToArray();
                    if (keys.Length > 0) { Track(c.m_Path).Rotations = keys; scene.DirectCurveCount++; }
                }
                catch { /* This clip still retains every other readable transform channel. */ }
            }

            var nodePaths = new HashSet<string>(scene.Nodes.Select(n => n.Path), StringComparer.Ordinal);
            foreach (var track in byPath.Values)
                if (!nodePaths.Contains(track.Path)) scene.UnboundCurveCount++;
            scene.Tracks = byPath.Values.OrderBy(x => x.Path, StringComparer.Ordinal).ToList();
        }

        private static T FindObject<T>(AssetsManager manager, Dictionary<string, string> workspaceToOriginal, string[] originals, string sourcePath, long pathId, string workspace) where T : UnityObject
        {
            string wanted = SafeFullPath(sourcePath);
            foreach (var file in manager.assetsFileList)
            {
                string source = ResolveOriginalPath(file, workspaceToOriginal, originals, workspace);
                if (string.IsNullOrEmpty(source) || !string.Equals(SafeFullPath(source), wanted, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var obj in file.Objects ?? new List<UnityObject>())
                    if (obj is T found && obj.m_PathID == pathId) return found;
            }
            return null;
        }

        private static AssetStudio.Transform FindTransform(AssetStudio.GameObject go)
        {
            foreach (var pointer in go.m_Components ?? Array.Empty<PPtr<AssetStudio.Component>>())
                if (pointer != null && pointer.TryGet(out AssetStudio.Component comp) && comp is AssetStudio.Transform tr) return tr;
            return null;
        }

        private static string Identity(UnityObject obj) =>
            (obj?.assetsFile?.fullName ?? obj?.assetsFile?.originalPath ?? "?") + "|" + (obj?.m_PathID.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0");

        // Unity serializes column-vector matrices; transpose to System.Numerics row-vector convention.
        private static NMatrix ToNumerics(AssetStudio.Matrix4x4 m) =>
            new NMatrix(m.M00, m.M10, m.M20, m.M30, m.M01, m.M11, m.M21, m.M31,
                m.M02, m.M12, m.M22, m.M32, m.M03, m.M13, m.M23, m.M33);

        private static float[] ReadNormals(float[] source, int vertexCount)
        {
            if (source == null || source.Length < vertexCount * 3) return null;
            int stride = source.Length / vertexCount;
            if (stride < 3) return null;
            var result = new float[vertexCount * 3];
            for (int i = 0; i < vertexCount; i++)
                for (int a = 0; a < 3; a++) result[i * 3 + a] = source[i * stride + a];
            return result;
        }

        private static float[] MakeNormals(float[] vertices, uint[] indices)
        {
            int count = vertices.Length / 3;
            var normals = new float[vertices.Length];
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                int a = (int)indices[i], b = (int)indices[i + 1], c = (int)indices[i + 2];
                var va = new NVector3(vertices[a * 3], vertices[a * 3 + 1], vertices[a * 3 + 2]);
                var vb = new NVector3(vertices[b * 3], vertices[b * 3 + 1], vertices[b * 3 + 2]);
                var vc = new NVector3(vertices[c * 3], vertices[c * 3 + 1], vertices[c * 3 + 2]);
                var n = NVector3.Cross(vb - va, vc - va);
                normals[a * 3] += n.X; normals[a * 3 + 1] += n.Y; normals[a * 3 + 2] += n.Z;
                normals[b * 3] += n.X; normals[b * 3 + 1] += n.Y; normals[b * 3 + 2] += n.Z;
                normals[c * 3] += n.X; normals[c * 3 + 1] += n.Y; normals[c * 3 + 2] += n.Z;
            }
            for (int i = 0; i < count; i++)
            {
                var n = new NVector3(normals[i * 3], normals[i * 3 + 1], normals[i * 3 + 2]);
                if (n.LengthSquared() < 1e-10f) n = NVector3.UnitY; else n = NVector3.Normalize(n);
                normals[i * 3] = n.X; normals[i * 3 + 1] = n.Y; normals[i * 3 + 2] = n.Z;
            }
            return normals;
        }

        private static float MaxTime(IEnumerable<float> values)
        {
            float max = 0;
            foreach (var value in values ?? Enumerable.Empty<float>()) if (IsFinite(value)) max = Math.Max(max, value);
            return max;
        }

        private static NQuaternion Normalize(NQuaternion q) =>
            !IsFinite(q.X) || !IsFinite(q.Y) || !IsFinite(q.Z) || !IsFinite(q.W) || q.LengthSquared() < 1e-10f ? NQuaternion.Identity : NQuaternion.Normalize(q);

        private static bool IsFinite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
        private static string SafeFullPath(string path) { try { return string.IsNullOrWhiteSpace(path) ? "" : Path.GetFullPath(path); } catch { return ""; } }

        private static string[] CopyToWorkspace(string[] sourceFiles, string workspace, Dictionary<string, string> map)
        {
            // Preserve relative folder layout so Unity cross-file dependencies and .split0 companions
            // remain distinguishable while AssetStudio parses disposable copies.
            var commonRoot = GetCommonDirectory(sourceFiles);
            var copied = new List<string>();
            for (int i = 0; i < sourceFiles.Length; i++)
            {
                var source = Path.GetFullPath(sourceFiles[i]);
                var relative = Path.GetRelativePath(commonRoot, source);
                if (Path.IsPathRooted(relative) || relative == ".." ||
                    relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                    relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
                    relative = Path.Combine("_external_inputs", i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture), Path.GetFileName(source));

                var destination = Path.GetFullPath(Path.Combine(workspace, relative));
                var fullWorkspace = Path.GetFullPath(workspace);
                if (!string.Equals(destination, fullWorkspace, StringComparison.OrdinalIgnoreCase) &&
                    !destination.StartsWith(fullWorkspace.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A selected input resolved outside the temporary parse workspace.");

                var directory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.Copy(source, destination, true);
                map[destination] = source;
                copied.Add(destination);
            }
            return copied.ToArray();
        }

        private static string GetCommonDirectory(string[] files)
        {
            if (files == null || files.Length == 0) return Path.GetTempPath();
            var common = Path.GetDirectoryName(Path.GetFullPath(files[0]));
            if (string.IsNullOrWhiteSpace(common)) common = Path.GetPathRoot(Path.GetFullPath(files[0]));
            foreach (var file in files.Skip(1))
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(file));
                while (!string.IsNullOrWhiteSpace(common) &&
                    !VisualLabPackageScanner.IsWithinDirectory(directory, common))
                {
                    var parent = Directory.GetParent(common);
                    if (parent == null) { common = Path.GetPathRoot(common); break; }
                    common = parent.FullName;
                }
            }
            return string.IsNullOrWhiteSpace(common) ? Path.GetPathRoot(Path.GetFullPath(files[0])) : common;
        }

        private static void MergeSplitParts(string workspace)
        {
            foreach (var split0 in Directory.GetFiles(workspace, "*.split0", SearchOption.AllDirectories))
            {
                var destinationName = Path.GetFileNameWithoutExtension(split0);
                var directory = Path.GetDirectoryName(split0);
                if (string.IsNullOrWhiteSpace(directory)) continue;
                var destination = Path.Combine(directory, destinationName);
                if (File.Exists(destination)) continue;
                var parts = Directory.GetFiles(directory, destinationName + ".split*")
                    .OrderBy(path =>
                    {
                        var ext = Path.GetExtension(path);
                        return int.TryParse(ext.Length > 6 ? ext.Substring(6) : "", out int n) ? n : int.MaxValue;
                    }).ToArray();
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    foreach (var part in parts) using (var input = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read)) input.CopyTo(output);
            }
        }

        private static string ResolveOriginalPath(SerializedFile file, Dictionary<string, string> map, string[] originals, string workspace)
        {
            foreach (var candidate in new[] { file.originalPath, file.fullName })
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                var full = SafeFullPath(candidate);
                if (map.TryGetValue(full, out var original)) return original;
            }
            var name = Path.GetFileName(file.originalPath ?? "");
            if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileName(file.fullName ?? "");
            var matches = originals.Where(p => string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase)).ToArray();
            return matches.Length == 1 ? matches[0] : "";
        }
    }
}

namespace AssetStudioGUI
{
    public sealed class JuanchoVisualLabAnimationPlaybackForm : Form
    {
        private readonly string[] _inputFiles;
        private readonly List<Juancho.VisualLab.VisualLabUnityAssetItem> _assets;
        private readonly ComboBox _rootCombo = new ComboBox();
        private readonly ComboBox _clipCombo = new ComboBox();
        private readonly Button _loadButton = new Button();
        private readonly Button _playButton = new Button { Text = "Play", AutoSize = true };
        private readonly Button _stopButton = new Button { Text = "Stop", AutoSize = true };
        private readonly CheckBox _loopCheck = new CheckBox { Text = "Loop", Checked = true, AutoSize = true };
        private readonly ComboBox _speedCombo = new ComboBox();
        private readonly TrackBar _timeBar = new TrackBar { Minimum = 0, Maximum = 1000, TickFrequency = 100, SmallChange = 1, LargeChange = 25, Dock = DockStyle.Fill };
        private readonly Label _timeLabel = new Label { AutoSize = true };
        private readonly Label _status = new Label { AutoSize = true, MaximumSize = new Size(1400, 0) };
        private readonly OpenTK.GLControl _gl = new OpenTK.GLControl();
        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer { Interval = 33 };
        private readonly Stopwatch _clock = new Stopwatch();
        private Juancho.VisualLab.VisualLabPlaybackScene _scene;
        private bool _glReady;
        private bool _playing;
        private bool _scrubbing;
        private int _program, _vao, _positionBuffer, _normalBuffer, _indexBuffer;
        private int _positionLocation, _normalLocation, _modelLocation, _viewLocation, _projectionLocation;
        private int _leftMouseX, _leftMouseY;
        private bool _rotating, _panning;
        private float _yaw = -0.55f, _pitch = -0.32f, _distance = 3.2f, _panX, _panY, _panZ;
        private float _time;
        private bool _wireframe;

        public JuanchoVisualLabAnimationPlaybackForm(IEnumerable<string> inputFiles, IEnumerable<Juancho.VisualLab.VisualLabUnityAssetItem> assets)
        {
            _inputFiles = (inputFiles ?? Enumerable.Empty<string>()).Where(File.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            _assets = (assets ?? Enumerable.Empty<Juancho.VisualLab.VisualLabUnityAssetItem>()).ToList();
            if (_inputFiles.Length == 0) throw new ArgumentException("Scan a package folder before opening the animation player.", nameof(inputFiles));
            Text = "Juancho MLBB Visual Lab — Interactive Model & Animation Player";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1420, 940);
            MinimumSize = new Size(1000, 680);
            Font = new System.Drawing.Font("Segoe UI", 9F);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var top = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            top.Controls.Add(new Label { Text = "Model root (GameObject):", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            _rootCombo.DropDownStyle = ComboBoxStyle.DropDownList; _rootCombo.Width = 370;
            foreach (var item in _assets.Where(a => string.Equals(a.ClassName, "GameObject", StringComparison.OrdinalIgnoreCase))
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
                _rootCombo.Items.Add(new AssetChoice(item));
            if (_rootCombo.Items.Count > 0)
            {
                int suggested = Enumerable.Range(0, _rootCombo.Items.Count)
                    .FirstOrDefault(i => ((AssetChoice)_rootCombo.Items[i]).Item.Name.IndexOf("skin", StringComparison.OrdinalIgnoreCase) >= 0);
                _rootCombo.SelectedIndex = suggested;
            }
            top.Controls.Add(_rootCombo);
            top.Controls.Add(new Label { Text = "AnimationClip:", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
            _clipCombo.DropDownStyle = ComboBoxStyle.DropDownList; _clipCombo.Width = 370;
            foreach (var item in _assets.Where(a => string.Equals(a.ClassName, "AnimationClip", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(a =>
                {
                    var hint = (a.Name + " " + a.SourceBundlePath).ToLowerInvariant();
                    return hint.Contains("skill") || hint.Contains("ultimate") || hint.Contains("attack") || hint.Contains("spell") ? 1 : 0;
                })
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
                _clipCombo.Items.Add(new AssetChoice(item));
            if (_clipCombo.Items.Count > 0) _clipCombo.SelectedIndex = 0;
            _loadButton.Text = "Load Model + Clip"; _loadButton.AutoSize = true;
            _loadButton.Click += async (s, e) => await LoadPreviewAsync();
            top.Controls.Add(_loadButton);

            var viewportHost = new Panel { Dock = DockStyle.Fill };
            _gl.Dock = DockStyle.Fill; _gl.BackColor = System.Drawing.Color.FromArgb(20, 23, 30);
            _gl.Load += Gl_Load; _gl.Paint += Gl_Paint; _gl.Resize += (s, e) => _gl.Invalidate();
            _gl.MouseDown += Gl_MouseDown; _gl.MouseUp += (s, e) => { _rotating = false; _panning = false; };
            _gl.MouseMove += Gl_MouseMove; _gl.MouseWheel += Gl_MouseWheel; _gl.DoubleClick += (s, e) => ResetView();
            viewportHost.Controls.Add(_gl);

            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, AutoSize = true };
            bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            _playButton.Click += (s, e) => TogglePlayback();
            _stopButton.Click += (s, e) => { _playing = false; _timer.Stop(); _time = 0; UpdateFrame(); };
            _loopCheck.CheckedChanged += (s, e) => UpdateFrame();
            _speedCombo.DropDownStyle = ComboBoxStyle.DropDownList; _speedCombo.Width = 86;
            foreach (var s in new[] { "0.25x", "0.5x", "1x", "1.5x", "2x" }) _speedCombo.Items.Add(s);
            _speedCombo.SelectedIndex = 2;
            var wire = new CheckBox { Text = "Wireframe", AutoSize = true };
            wire.CheckedChanged += (s, e) => { _wireframe = wire.Checked; _gl.Invalidate(); };
            var reset = new Button { Text = "Reset View", AutoSize = true }; reset.Click += (s, e) => ResetView();
            controls.Controls.Add(_playButton); controls.Controls.Add(_stopButton); controls.Controls.Add(_loopCheck);
            controls.Controls.Add(new Label { Text = "Speed:", AutoSize = true, Padding = new Padding(4, 6, 0, 0) });
            controls.Controls.Add(_speedCombo); controls.Controls.Add(wire); controls.Controls.Add(reset); controls.Controls.Add(_timeLabel);
            _timeBar.Scroll += (s, e) =>
            {
                if (_scene == null) return;
                _scrubbing = true; _time = _scene.Duration * _timeBar.Value / _timeBar.Maximum; UpdateFrame();
                _scrubbing = false;
            };
            bottom.Controls.Add(controls, 0, 0); bottom.Controls.Add(_timeBar, 0, 1); bottom.Controls.Add(_status, 0, 2);

            root.Controls.Add(top, 0, 0); root.Controls.Add(viewportHost, 0, 1); root.Controls.Add(bottom, 0, 2);
            Controls.Add(root);
            Shown += async (s, e) =>
            {
                if (_rootCombo.SelectedItem != null && _clipCombo.SelectedItem != null)
                    await LoadPreviewAsync();
            };
            _timer.Tick += Timer_Tick;
            FormClosed += (s, e) => { _timer.Stop(); _gl.Dispose(); };
        }

        private sealed class AssetChoice
        {
            public Juancho.VisualLab.VisualLabUnityAssetItem Item { get; }
            public AssetChoice(Juancho.VisualLab.VisualLabUnityAssetItem item) { Item = item; }
            public override string ToString() => Item.Name + "  [" + Path.GetFileName(Item.SourceBundlePath) + " · " + Item.ClassName + " · " + Item.PathId + "]";
        }

        private async Task LoadPreviewAsync()
        {
            if (!(_rootCombo.SelectedItem is AssetChoice root) || !(_clipCombo.SelectedItem is AssetChoice clip))
            {
                MessageBox.Show(this, "Select both a GameObject model root and an AnimationClip.", "Juancho Visual Lab", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _loadButton.Enabled = false; _status.Text = "Reconstructing model hierarchy, skin weights, bind poses, and clip curves from temporary bundle copies…";
            try
            {
                _scene = await Juancho.VisualLab.VisualLabAnimationPlaybackLoader.LoadAsync(_inputFiles, root.Item, clip.Item);
                _time = 0; _playing = false; _timer.Stop();
                _scene.Evaluate(0, _loopCheck.Checked);
                FitViewToScene();
                UpdateFrame();
                if (_glReady) UploadDynamicBuffers();
                _status.Text = _scene.ModelName + " · " + _scene.ClipName + " · " +
                    _scene.VertexCount.ToString("N0") + " vertices · " + _scene.TriangleCount.ToString("N0") + " triangles · " + _scene.Duration.ToString("0.###") + " s. " +
                    _scene.PlaybackNotes;
            }
            catch (Exception ex)
            {
                _scene = null; _playing = false; _timer.Stop();
                _status.Text = "Preview failed: " + ex.Message;
                MessageBox.Show(this, "Could not load the model and AnimationClip. The source package was not changed.\r\n\r\n" + ex.Message,
                    "Juancho MLBB Visual Lab", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { _loadButton.Enabled = true; }
        }

        private void Gl_Load(object sender, EventArgs e)
        {
            try { _gl.MakeCurrent(); InitializeGL(); _glReady = true; CreateBuffers(); if (_scene != null) UploadDynamicBuffers(); }
            catch (Exception ex) { MessageBox.Show(this, "OpenGL preview failed to initialize: " + ex.Message, "Juancho Visual Lab", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void InitializeGL()
        {
            string vertex = @"#version 330 core
layout(location=0) in vec3 aPosition;
layout(location=1) in vec3 aNormal;
uniform mat4 uModel; uniform mat4 uView; uniform mat4 uProjection;
out vec3 vNormal; out vec3 vPosition;
void main(){ vec4 world=uModel*vec4(aPosition,1.0); vPosition=world.xyz; vNormal=mat3(transpose(inverse(uModel)))*aNormal; gl_Position=uProjection*uView*world; }";
            string fragment = @"#version 330 core
in vec3 vNormal; in vec3 vPosition; out vec4 FragColor;
void main(){ vec3 N=normalize(vNormal); vec3 L=normalize(vec3(0.45,0.8,0.35)); float d=max(dot(N,L),0.0); float rim=pow(1.0-max(dot(N,normalize(-vPosition)),0.0),2.0); vec3 base=vec3(0.48,0.62,0.78); FragColor=vec4(base*(0.22+0.72*d)+vec3(0.11,0.16,0.23)*rim,1.0); }";
            int vs = CompileShader(ShaderType.VertexShader, vertex);
            int fs = CompileShader(ShaderType.FragmentShader, fragment);
            _program = GL.CreateProgram(); GL.AttachShader(_program, vs); GL.AttachShader(_program, fs); GL.LinkProgram(_program);
            GL.GetProgram(_program, GetProgramParameterName.LinkStatus, out int linked);
            if (linked == 0) throw new InvalidOperationException(GL.GetProgramInfoLog(_program));
            GL.DeleteShader(vs); GL.DeleteShader(fs);
            _positionLocation = 0; _normalLocation = 1;
            _modelLocation = GL.GetUniformLocation(_program, "uModel");
            _viewLocation = GL.GetUniformLocation(_program, "uView");
            _projectionLocation = GL.GetUniformLocation(_program, "uProjection");
        }

        private static int CompileShader(ShaderType type, string source)
        {
            int shader = GL.CreateShader(type); GL.ShaderSource(shader, source); GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out int ok);
            if (ok == 0) throw new InvalidOperationException(GL.GetShaderInfoLog(shader));
            return shader;
        }

        private void CreateBuffers()
        {
            GL.GenVertexArrays(1, out _vao); GL.GenBuffers(1, out _positionBuffer); GL.GenBuffers(1, out _normalBuffer); GL.GenBuffers(1, out _indexBuffer);
            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _positionBuffer); GL.BufferData(BufferTarget.ArrayBuffer, IntPtr.Zero, IntPtr.Zero, BufferUsageHint.DynamicDraw);
            GL.EnableVertexAttribArray(0); GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 0, 0);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _normalBuffer); GL.BufferData(BufferTarget.ArrayBuffer, IntPtr.Zero, IntPtr.Zero, BufferUsageHint.DynamicDraw);
            GL.EnableVertexAttribArray(1); GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 0, 0);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);
            GL.BindVertexArray(0);
        }

        private void UploadDynamicBuffers()
        {
            if (!_glReady || _scene == null || _vao == 0) return;
            _gl.MakeCurrent(); GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _positionBuffer);
            GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(_scene.Positions.Length * sizeof(float)), _scene.Positions, BufferUsageHint.DynamicDraw);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _normalBuffer);
            GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(_scene.Normals.Length * sizeof(float)), _scene.Normals, BufferUsageHint.DynamicDraw);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);
            GL.BufferData(BufferTarget.ElementArrayBuffer, (IntPtr)(_scene.Indices.Length * sizeof(uint)), _scene.Indices, BufferUsageHint.DynamicDraw);
            GL.BindVertexArray(0);
        }

        private void UpdateFrame()
        {
            if (_scene == null) { _timeLabel.Text = "Load a model and clip to begin."; _gl.Invalidate(); return; }
            _scene.Evaluate(_time, _loopCheck.Checked);
            _timeBar.Value = Math.Max(_timeBar.Minimum, Math.Min(_timeBar.Maximum, (int)Math.Round((_time / Math.Max(_scene.Duration, 0.0001f)) * _timeBar.Maximum)));
            _timeLabel.Text = _time.ToString("0.000") + " / " + _scene.Duration.ToString("0.000") + " s";
            UploadDynamicBuffers();
            _gl.Invalidate();
        }

        private void TogglePlayback()
        {
            if (_scene == null) return;
            _playing = !_playing; _playButton.Text = _playing ? "Pause" : "Play";
            if (_playing) { _clock.Restart(); _timer.Start(); } else { _timer.Stop(); _clock.Stop(); }
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (!_playing || _scene == null) return;
            var elapsed = Math.Min(0.25, _clock.Elapsed.TotalSeconds); _clock.Restart();
            float speed = ParseSpeed(); _time += (float)elapsed * speed;
            if (_time >= _scene.Duration)
            {
                if (_loopCheck.Checked) _time %= Math.Max(_scene.Duration, 0.0001f);
                else { _time = _scene.Duration; _playing = false; _timer.Stop(); _playButton.Text = "Play"; }
            }
            UpdateFrame();
        }

        private float ParseSpeed()
        {
            var text = _speedCombo.SelectedItem as string ?? "1x";
            return float.TryParse(text.Replace("x", ""), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var speed) ? Math.Max(0.01f, Math.Min(8, speed)) : 1f;
        }

        private void Gl_Paint(object sender, PaintEventArgs e)
        {
            if (!_glReady) { e.Graphics.Clear(System.Drawing.Color.FromArgb(20, 23, 30)); return; }
            _gl.MakeCurrent();
            GL.Viewport(0, 0, Math.Max(1, _gl.Width), Math.Max(1, _gl.Height));
            GL.ClearColor(0.075f, 0.09f, 0.12f, 1f); GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            GL.Enable(EnableCap.DepthTest); GL.UseProgram(_program);
            float aspect = Math.Max(0.1f, _gl.Width / (float)Math.Max(1, _gl.Height));
            var eye = new OpenTK.Mathematics.Vector3(_panX + (float)(Math.Cos(_yaw) * Math.Cos(_pitch) * _distance),
                _panY + (float)(Math.Sin(_pitch) * _distance),
                _panZ + (float)(Math.Sin(_yaw) * Math.Cos(_pitch) * _distance));
            var view = Matrix4.LookAt(eye, new OpenTK.Mathematics.Vector3(_panX, _panY, _panZ), OpenTK.Mathematics.Vector3.UnitY);
            var projection = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(42), aspect, 0.01f, 10000f);
            var identity = Matrix4.Identity;
            GL.UniformMatrix4(_modelLocation, false, ref identity); GL.UniformMatrix4(_viewLocation, false, ref view); GL.UniformMatrix4(_projectionLocation, false, ref projection);
            if (_scene != null && _scene.Indices.Length > 0 && _vao != 0)
            {
                GL.BindVertexArray(_vao);
                GL.PolygonMode(MaterialFace.FrontAndBack, _wireframe ? PolygonMode.Line : PolygonMode.Fill);
                GL.DrawElements(PrimitiveType.Triangles, _scene.Indices.Length, DrawElementsType.UnsignedInt, IntPtr.Zero);
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
                GL.BindVertexArray(0);
            }
            GL.UseProgram(0); _gl.SwapBuffers();
        }

        private void Gl_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) _rotating = true;
            if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle) _panning = true;
            _leftMouseX = e.X; _leftMouseY = e.Y; _gl.Focus();
        }

        private void Gl_MouseMove(object sender, MouseEventArgs e)
        {
            int dx = e.X - _leftMouseX, dy = e.Y - _leftMouseY; _leftMouseX = e.X; _leftMouseY = e.Y;
            if (_rotating) { _yaw += dx * 0.009f; _pitch = Math.Max(-1.45f, Math.Min(1.45f, _pitch + dy * 0.009f)); }
            if (_panning) { _panX -= dx * _distance * 0.0017f; _panY += dy * _distance * 0.0017f; }
            if (_rotating || _panning) _gl.Invalidate();
        }

        private void Gl_MouseWheel(object sender, MouseEventArgs e)
        {
            _distance = Math.Max(0.05f, Math.Min(5000f, _distance * (e.Delta > 0 ? 0.88f : 1.14f))); _gl.Invalidate();
        }

        private void FitViewToScene()
        {
            if (_scene == null || _scene.Positions.Length < 3) return;
            float minX = float.PositiveInfinity, minY = float.PositiveInfinity, minZ = float.PositiveInfinity;
            float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity, maxZ = float.NegativeInfinity;
            for (int i = 0; i + 2 < _scene.Positions.Length; i += 3)
            {
                float x = _scene.Positions[i], y = _scene.Positions[i + 1], z = _scene.Positions[i + 2];
                if (float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(y) || float.IsInfinity(y) || float.IsNaN(z) || float.IsInfinity(z)) continue;
                minX = Math.Min(minX, x); minY = Math.Min(minY, y); minZ = Math.Min(minZ, z);
                maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); maxZ = Math.Max(maxZ, z);
            }
            if (float.IsInfinity(minX)) return;
            _panX = (minX + maxX) * 0.5f; _panY = (minY + maxY) * 0.5f; _panZ = (minZ + maxZ) * 0.5f;
            float diameter = (float)Math.Sqrt((maxX-minX)*(maxX-minX) + (maxY-minY)*(maxY-minY) + (maxZ-minZ)*(maxZ-minZ));
            _distance = Math.Max(0.25f, Math.Min(5000f, Math.Max(0.5f, diameter) * 1.55f));
        }

        private void ResetView() { _yaw = -0.55f; _pitch = -0.32f; if (_scene != null) FitViewToScene(); else { _distance = 3.2f; _panX = _panY = _panZ = 0; } _gl.Invalidate(); }
    }
}
