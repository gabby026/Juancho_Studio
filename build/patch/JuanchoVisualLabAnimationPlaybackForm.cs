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
        public NVector3 InSlope { get; set; }
        public NVector3 OutSlope { get; set; }
        public bool HasTangents { get; set; }
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
        public bool SkinningReliable { get; set; }
        public int SkinningMatrixOrder { get; set; }
        public string SkinningStatus { get; set; } = "Skinning data not validated";
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
        public string CurveExtractionNotes { get; set; } = "";

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
                var isActuallySkinned = part.IsSkinned && part.SkinningReliable &&
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
                    float validWeightTotal = 0;

                    if (isActuallySkinned)
                    {
                        // Normalize only the influences that actually resolve to bones in this
                        // bundle. Never mix a partially skinned vertex with the renderer-space
                        // fallback; that blend puts the same vertex in two coordinate spaces and
                        // is a common cause of long, stretched spikes.
                        for (int influence = 0; influence < 4; influence++)
                        {
                            var slot = vertex * 4 + influence;
                            var weight = part.Weights[slot];
                            var boneId = part.BoneIndices[slot];
                            if (!IsFinite(weight) || weight <= 0 || boneId < 0 ||
                                boneId >= part.BindPoses.Length || boneId >= part.BoneNodeIndices.Length)
                                continue;
                            var boneNode = part.BoneNodeIndices[boneId];
                            if (boneNode < 0 || boneNode >= worlds.Length) continue;
                            validWeightTotal += weight;
                        }

                        if (validWeightTotal > 0.0001f)
                        {
                            for (int influence = 0; influence < 4; influence++)
                            {
                                var slot = vertex * 4 + influence;
                                var weight = part.Weights[slot];
                                var boneId = part.BoneIndices[slot];
                                if (!IsFinite(weight) || weight <= 0 || boneId < 0 ||
                                    boneId >= part.BindPoses.Length || boneId >= part.BoneNodeIndices.Length)
                                    continue;
                                var boneNode = part.BoneNodeIndices[boneId];
                                if (boneNode < 0 || boneNode >= worlds.Length) continue;
                                var normalizedWeight = weight / validWeightTotal;
                                // Unity's bind pose is bone.worldToLocal * renderer.localToWorld.
                                // Matrices are transposed into System.Numerics row-vector space,
                                // so the canonical vertex transform is bindPose * currentBoneWorld.
                                // Do not guess the multiplication order per mesh: an order chosen
                                // from a coarse rigid-pose score can produce extreme deformation
                                // even when the rest pose looked superficially acceptable.
                                var skinMatrix = part.BindPoses[boneId] * worlds[boneNode];
                                p += NVector3.Transform(localPosition, skinMatrix) * normalizedWeight;
                                n += NVector3.TransformNormal(localNormal, skinMatrix) * normalizedWeight;
                            }
                        }
                    }

                    if (!isActuallySkinned || validWeightTotal <= 0.0001f)
                    {
                        p = NVector3.Transform(localPosition, nodeWorld);
                        n = NVector3.TransformNormal(localNormal, nodeWorld);
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
            var u = Math.Max(0, Math.Min(1, (time - a.Time) / span));
            if (a.HasTangents && b.HasTangents && IsFiniteVector(a.OutSlope) && IsFiniteVector(b.InSlope))
            {
                var u2 = u * u;
                var u3 = u2 * u;
                var h00 = 2f * u3 - 3f * u2 + 1f;
                var h10 = u3 - 2f * u2 + u;
                var h01 = -2f * u3 + 3f * u2;
                var h11 = u3 - u2;
                return a.Value * h00 + a.OutSlope * (h10 * span) +
                    b.Value * h01 + b.InSlope * (h11 * span);
            }
            return NVector3.Lerp(a.Value, b.Value, u);
        }

        private static bool IsFiniteVector(NVector3 value) =>
            IsFinite(value.X) && IsFinite(value.Y) && IsFinite(value.Z);

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
        private sealed class PlaybackScalarKey
        {
            public float Time;
            public float Value;
            public float InSlope;
            public float OutSlope;
            public bool HasTangents;
        }

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
                var visitedTransforms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var selectedBundlePath = SafeFullPath(rootItem.SourceBundlePath);
                var selectedBundleFiles = manager.assetsFileList
                    .Where(file => string.Equals(
                        SafeFullPath(ResolveOriginalPath(file, workspaceToOriginal, inputs, workspace)),
                        selectedBundlePath, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var selectedBundleObjects = selectedBundleFiles
                    .SelectMany(file => file.Objects ?? new List<UnityObject>())
                    .ToArray();
                var transformMap = selectedBundleObjects
                    .OfType<AssetStudio.Transform>()
                    .GroupBy(Identity, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

                // A few MLBB bundles reference the SkinnedMeshRenderer bones from a companion
                // serialized file. Pull in the referenced bones and their ancestors so weighted
                // vertices can still follow the AnimationClip rather than falling back rigidly.
                foreach (var gameObject in selectedBundleObjects.OfType<AssetStudio.GameObject>())
                {
                    foreach (var componentPointer in gameObject.m_Components ?? Array.Empty<PPtr<AssetStudio.Component>>())
                    {
                        if (componentPointer == null ||
                            !componentPointer.TryGet(out AssetStudio.Component component) ||
                            !(component is AssetStudio.SkinnedMeshRenderer renderer))
                            continue;

                        foreach (var bonePointer in renderer.m_Bones ?? Array.Empty<PPtr<AssetStudio.Transform>>())
                        {
                            if (bonePointer == null || !bonePointer.TryGet(out AssetStudio.Transform bone) || bone == null)
                                continue;

                            var ancestryVisited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            while (bone != null && ancestryVisited.Add(Identity(bone)))
                            {
                                transformMap[Identity(bone)] = bone;
                                if (bone.m_Father == null ||
                                    !bone.m_Father.TryGet(out AssetStudio.Transform parent) ||
                                    parent == null)
                                    break;
                                bone = parent;
                            }
                        }
                    }
                }

                var bundleTransforms = transformMap.Values.ToArray();
                var bundleTransformIds = new HashSet<string>(bundleTransforms.Select(Identity), StringComparer.OrdinalIgnoreCase);

                // Preserve the selected GameObject as the animation path anchor, while bringing
                // every connected hierarchy in the same hero skin bundle into the scene. This
                // includes renderer objects and bones that sit outside the originally chosen subtree.
                var anchorAncestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var anchorCursor = rootTransform;
                while (anchorCursor != null && anchorAncestors.Add(Identity(anchorCursor)))
                {
                    if (anchorCursor.m_Father == null ||
                        !anchorCursor.m_Father.TryGet(out AssetStudio.Transform parent) ||
                        parent == null)
                        break;
                    anchorCursor = parent;
                }
                var bundleRoots = bundleTransforms.Where(transform =>
                {
                    return transform.m_Father == null ||
                        !transform.m_Father.TryGet(out AssetStudio.Transform parent) ||
                        parent == null ||
                        !bundleTransformIds.Contains(Identity(parent));
                }).OrderByDescending(transform => anchorAncestors.Contains(Identity(transform))).ToArray();

                foreach (var hierarchyRoot in bundleRoots)
                {
                    if (nodeByIdentity.ContainsKey(Identity(hierarchyRoot))) continue;
                    string rootIdentity = Identity(hierarchyRoot);
                    string anchorIdentity = Identity(rootTransform);
                    string rootPathPrefix = string.Equals(rootIdentity, anchorIdentity, StringComparison.OrdinalIgnoreCase)
                        ? ""
                        : anchorAncestors.Contains(rootIdentity) ? "@anchorParent" : "@bundle";
                    // Only the selected animation anchor may have the empty path. Giving a
                    // helper/root Transform above it the same empty path replays the same root
                    // curve on both nodes, compounding scales and making the mesh stretch.
                    BuildNodes(hierarchyRoot, -1, rootPathPrefix, scene, transforms, nodeByIdentity, visitedTransforms, 0, anchorIdentity);
                }
                // Some game bundles have incomplete parent pointers. Add any remaining transform
                // as a detached hierarchy so its mesh and bone references are not silently dropped.
                foreach (var transform in bundleTransforms)
                {
                    if (nodeByIdentity.ContainsKey(Identity(transform))) continue;
                    BuildNodes(transform, -1, "@bundle", scene, transforms, nodeByIdentity, visitedTransforms, 0, Identity(rootTransform));
                }
                if (!nodeByIdentity.ContainsKey(Identity(rootTransform)))
                    BuildNodes(rootTransform, -1, "", scene, transforms, nodeByIdentity, visitedTransforms, 0, Identity(rootTransform));

                if (scene.Nodes.Count == 0) throw new InvalidDataException("The selected hero skin bundle contained no usable Transform nodes.");
                AddMeshes(scene, transforms, nodeByIdentity, rootTransform, workspaceToOriginal, inputs, workspace);

                BuildClip(scene, clip, clipItem, transforms);
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
                var partsWithSkinWeights = scene.MeshParts.Count(x => x.IsSkinned && x.SkinningReliable);
                var partsWithRigidFallback = scene.MeshParts.Count(x => x.IsSkinned && !x.SkinningReliable);
                var skinningWarnings = scene.MeshParts.Where(x => x.IsSkinned && !x.SkinningReliable)
                    .Select(x => x.Name + ": " + x.SkinningStatus).Distinct().Take(3).ToArray();
                scene.PlaybackNotes =
                    "Supported Transform curve channels: " + scene.DirectCurveCount +
                    "; curve paths not found in selected hierarchy: " + scene.UnboundCurveCount +
                    "; skinned mesh parts: " + scene.SkinnedPartCount +
                    "; parts with calibrated skinning: " + partsWithSkinWeights +
                    "; parts using safe rigid fallback: " + partsWithRigidFallback +
                    "; decoded tracks: " + scene.Tracks.Count +
                    "; transform keys: " + scene.Tracks.Sum(track => track.Positions.Length + track.Rotations.Length + track.EulerRotations.Length + track.Scales.Length) +
                    "; clip events: " + scene.ClipEventCount + ". " +
                    (skinningWarnings.Length == 0 ? "" : "Skinning fallback: " + string.Join(" | ", skinningWarnings) + ". ") +
                    (scene.DirectCurveCount == 0
                        ? "No supported Transform channels were decoded. Check the selected root/clip and binding diagnostics."
                        : "Vector curves use linear interpolation and quaternion curves use slerp; Unity tangents, Avatar muscle reconstruction and controller transitions are not fully reproduced.") +
                    (string.IsNullOrWhiteSpace(scene.CurveExtractionNotes) ? "" : " Curve decoder note: " + scene.CurveExtractionNotes);
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
            List<AssetStudio.Transform> transforms, Dictionary<string, int> nodeByIdentity, HashSet<string> visited, int depth,
            string anchorIdentity)
        {
            if (transform == null) return;
            if (depth > 512) throw new InvalidDataException("Transform hierarchy depth exceeded safety limit.");
            var key = Identity(transform);
            if (!visited.Add(key)) return;
            string name = transform.m_GameObject != null && transform.m_GameObject.TryGet(out AssetStudio.GameObject go) && go != null
                ? (go.m_Name ?? "")
                : "Transform_" + transform.m_PathID;
            bool isAnchor = string.Equals(key, anchorIdentity, StringComparison.OrdinalIgnoreCase);
            string ownPath = isAnchor ? "" :
                (parentIndex < 0
                    ? (string.IsNullOrEmpty(path) ? "" : path + "/" + name)
                    : (string.IsNullOrEmpty(path) ? name : path + "/" + name));
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
                    BuildNodes(child, nodeIndex, ownPath, scene, transforms, nodeByIdentity, visited, depth + 1, anchorIdentity);
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
                    // A mesh can be instanced by multiple renderers; each renderer has its own
                    // transform/bone mapping, so deduplicate by component rather than Mesh alone.
                    var meshKey = Identity(component) + "|" + Identity(mesh);
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
                    if (part.IsSkinned)
                        ValidateSkinningBinding(part, scene.Nodes);
                    scene.MeshParts.Add(part);
                }
            }
        }


        /// <summary>
        /// Calibrates skinning against the rest pose. If serialized bind poses use a coordinate
        /// convention that does not match this isolated preview, reconstruct equivalent bind
        /// matrices from the renderer and bone rest transforms. This preserves the displayed
        /// rest pose while allowing animated bones to deform the mesh, instead of silently
        /// freezing the complete SkinnedMeshRenderer in rigid fallback.
        /// </summary>
        private static void ValidateSkinningBinding(PlaybackMeshPart part, List<PlaybackNode> nodes)
        {
            part.SkinningReliable = false;
            part.SkinningMatrixOrder = 0;
            part.SkinningStatus = "No valid skinning data; using rigid fallback";

            int vertexCount = part.Vertices?.Length / 3 ?? 0;
            if (vertexCount <= 0 ||
                part.Weights == null || part.Weights.Length != vertexCount * 4 ||
                part.BoneIndices == null || part.BoneIndices.Length != part.Weights.Length ||
                part.BoneNodeIndices == null || part.BoneNodeIndices.Length == 0 ||
                part.NodeIndex < 0 || part.NodeIndex >= nodes.Count)
            {
                part.SkinningStatus = "Missing or mismatched skin weights, bone references, or renderer transform";
                return;
            }

            bool hasInfluence = false;
            bool storedPosesUsable = part.BindPoses != null && part.BindPoses.Length > 0;
            for (int i = 0; i < part.Weights.Length; i++)
            {
                float weight = part.Weights[i];
                if (!IsFinite(weight) || weight <= 0.00001f) continue;
                hasInfluence = true;

                int boneIndex = part.BoneIndices[i];
                if (boneIndex < 0 || boneIndex >= part.BoneNodeIndices.Length ||
                    part.BoneNodeIndices[boneIndex] < 0 ||
                    part.BoneNodeIndices[boneIndex] >= nodes.Count)
                {
                    part.SkinningStatus = "A weighted bone does not resolve to a Transform; skinning skipped to prevent stretched vertices";
                    return;
                }

                if (part.BindPoses == null || boneIndex >= part.BindPoses.Length ||
                    !IsFiniteMatrix(part.BindPoses[boneIndex]))
                    storedPosesUsable = false;
            }

            if (!hasInfluence)
            {
                part.SkinningStatus = "Mesh has no positive bone weights; using rigid fallback";
                return;
            }

            var worlds = new NMatrix[nodes.Count];
            for (int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                var rotation = node.Rotation;
                if (!IsFinite(rotation.X) || !IsFinite(rotation.Y) || !IsFinite(rotation.Z) ||
                    !IsFinite(rotation.W) || rotation.LengthSquared() < 1e-10f)
                    rotation = NQuaternion.Identity;
                else
                    rotation = NQuaternion.Normalize(rotation);

                var local = NMatrix.CreateScale(node.Scale) *
                    NMatrix.CreateFromQuaternion(rotation) *
                    NMatrix.CreateTranslation(node.Position);
                worlds[i] = node.ParentIndex >= 0 && node.ParentIndex < i
                    ? local * worlds[node.ParentIndex]
                    : local;
            }

            var rendererWorld = worlds[part.NodeIndex];
            if (!storedPosesUsable)
            {
                if (TryReconstructRestPoseBindPoses(part, worlds, rendererWorld))
                    return;

                part.SkinningStatus = "Serialized bind poses are missing or invalid, and rest-pose reconstruction was not possible; using rigid fallback";
                return;
            }

            var min = new NVector3(float.PositiveInfinity);
            var max = new NVector3(float.NegativeInfinity);
            for (int i = 0; i < part.Vertices.Length; i += 3)
            {
                var v = new NVector3(part.Vertices[i], part.Vertices[i + 1], part.Vertices[i + 2]);
                min = NVector3.Min(min, v);
                max = NVector3.Max(max, v);
            }

            var diagonal = Math.Max((max - min).Length(), 0.0001f);
            var rendererWorld = worlds[part.NodeIndex];
            var stride = Math.Max(1, vertexCount / 96);
            double standardError = 0;
            double standardMaxError = 0;
            int samples = 0;

            // Validate only Unity's canonical matrix order. Comparing two guessed orders and
            // choosing whichever resembles the rigid rest pose slightly more can select the
            // inverse order for assets with nearly-identity transforms, then stretch the mesh
            // as soon as animated bones rotate. The canonical order is always bindPose * boneWorld.
            for (int vertex = 0; vertex < vertexCount; vertex += stride)
            {
                var source = new NVector3(part.Vertices[vertex * 3], part.Vertices[vertex * 3 + 1], part.Vertices[vertex * 3 + 2]);
                var expected = NVector3.Transform(source, rendererWorld);
                NVector3 standard = NVector3.Zero;
                float weightTotal = 0;

                for (int influence = 0; influence < 4; influence++)
                {
                    int slot = vertex * 4 + influence;
                    float weight = part.Weights[slot];
                    if (!IsFinite(weight) || weight <= 0) continue;

                    int boneIndex = part.BoneIndices[slot];
                    int boneNode = part.BoneNodeIndices[boneIndex];
                    var bindPose = part.BindPoses[boneIndex];
                    standard += NVector3.Transform(source, bindPose * worlds[boneNode]) * weight;
                    weightTotal += weight;
                }

                if (weightTotal <= 0.00001f) continue;
                standard /= weightTotal;
                var error = NVector3.Distance(standard, expected) / diagonal;
                standardError += error;
                standardMaxError = Math.Max(standardMaxError, error);
                samples++;
            }

            if (samples == 0)
            {
                if (TryReconstructRestPoseBindPoses(part, worlds, rendererWorld))
                    return;
                part.SkinningStatus = "No usable weighted vertices; using rigid fallback";
                return;
            }

            var meanError = standardError / samples;
            if (!double.IsFinite(meanError) || !double.IsFinite(standardMaxError) ||
                meanError > 0.12 || standardMaxError > 0.45)
            {
                // Never try an alternate multiplication order as a shortcut: reconstruct
                // bind poses against the current rest transforms, and keep the source safe
                // if the renderer/bone coordinate spaces still cannot be reconciled.
                if (TryReconstructRestPoseBindPoses(part, worlds, rendererWorld))
                    return;

                part.SkinningStatus = string.Format(
                    "Unity-order bind-pose mismatch (mean {0:P1}, worst sampled vertex {1:P1}); rigid fallback retained",
                    meanError, standardMaxError);
                return;
            }

            part.SkinningMatrixOrder = 0;
            part.SkinningReliable = true;
            bool approximate = meanError > 0.04 || standardMaxError > 0.20;
            part.SkinningStatus = (approximate ? "Approximate Unity-order CPU skinning enabled (" : "Unity-order bind-pose calibrated (") +
                "mean error " + meanError.ToString("P2", System.Globalization.CultureInfo.InvariantCulture) +
                ", max sampled error " + standardMaxError.ToString("P2", System.Globalization.CultureInfo.InvariantCulture) + ")";
        }

        private static bool TryReconstructRestPoseBindPoses(
            PlaybackMeshPart part, NMatrix[] worlds, NMatrix rendererWorld)
        {
            if (part == null || worlds == null ||
                part.BoneNodeIndices == null || part.BoneNodeIndices.Length == 0)
                return false;

            var reconstructed = new NMatrix[part.BoneNodeIndices.Length];
            for (int boneIndex = 0; boneIndex < part.BoneNodeIndices.Length; boneIndex++)
            {
                int boneNode = part.BoneNodeIndices[boneIndex];
                // Unweighted/unresolved entries are harmless identity placeholders. Every
                // positively weighted index was validated before this method is called.
                if (boneNode < 0 || boneNode >= worlds.Length)
                {
                    reconstructed[boneIndex] = NMatrix.Identity;
                    continue;
                }

                if (!NMatrix.Invert(worlds[boneNode], out var inverseBoneWorld))
                    return false;

                var effectiveBindPose = rendererWorld * inverseBoneWorld;
                if (!IsFiniteMatrix(effectiveBindPose))
                    return false;
                reconstructed[boneIndex] = effectiveBindPose;
            }

            // Row-vector convention used by System.Numerics:
            // vertex * (rendererRest * inverse(boneRest)) * boneCurrent.
            // At the rest pose this equals vertex * rendererRest, avoiding a pose jump.
            part.BindPoses = reconstructed;
            part.SkinningMatrixOrder = 0;
            part.SkinningReliable = true;
            part.SkinningStatus = "Rest-pose bind matrices reconstructed from resolved renderer/bone transforms; animated CPU skinning enabled";
            return true;
        }

        private static bool IsFiniteMatrix(NMatrix m) =>
            IsFinite(m.M11) && IsFinite(m.M12) && IsFinite(m.M13) && IsFinite(m.M14) &&
            IsFinite(m.M21) && IsFinite(m.M22) && IsFinite(m.M23) && IsFinite(m.M24) &&
            IsFinite(m.M31) && IsFinite(m.M32) && IsFinite(m.M33) && IsFinite(m.M34) &&
            IsFinite(m.M41) && IsFinite(m.M42) && IsFinite(m.M43) && IsFinite(m.M44);

        private static void BuildClip(VisualLabPlaybackScene scene, AssetStudio.AnimationClip clip, VisualLabUnityAssetItem item, List<AssetStudio.Transform> transforms)
        {
            scene.ClipName = string.IsNullOrWhiteSpace(clip.m_Name) ? item.Name : clip.m_Name;
            scene.SampleRate = clip.m_SampleRate > 0 && IsFinite(clip.m_SampleRate) ? clip.m_SampleRate : 30;

            var embeddedClip = clip.m_MuscleClip?.m_Clip;
            List<AssetStudio.StreamedClip.StreamedFrame> streamedFrames = null;
            string extractionNote = "";
            try
            {
                if (embeddedClip?.m_StreamedClip != null &&
                    embeddedClip.m_StreamedClip.data != null &&
                    embeddedClip.m_StreamedClip.data.Length > 0)
                    streamedFrames = embeddedClip.m_StreamedClip.ReadData();
            }
            catch (Exception ex)
            {
                extractionNote = "Streamed keys could not be decoded: " + ex.Message;
            }

            float duration = 0;
            foreach (var curve in clip.m_PositionCurves ?? Array.Empty<AssetStudio.Vector3Curve>())
                duration = Math.Max(duration, MaxTime(curve.curve?.m_Curve?.Select(k => k.time)));
            foreach (var curve in clip.m_RotationCurves ?? Array.Empty<AssetStudio.QuaternionCurve>())
                duration = Math.Max(duration, MaxTime(curve.curve?.m_Curve?.Select(k => k.time)));
            foreach (var curve in clip.m_EulerCurves ?? Array.Empty<AssetStudio.Vector3Curve>())
                duration = Math.Max(duration, MaxTime(curve.curve?.m_Curve?.Select(k => k.time)));
            foreach (var curve in clip.m_ScaleCurves ?? Array.Empty<AssetStudio.Vector3Curve>())
                duration = Math.Max(duration, MaxTime(curve.curve?.m_Curve?.Select(k => k.time)));
            foreach (var evt in clip.m_Events ?? Array.Empty<AssetStudio.AnimationEvent>())
                if (evt != null && IsFinite(evt.time)) duration = Math.Max(duration, evt.time);
            if (clip.m_MuscleClip != null && IsFinite(clip.m_MuscleClip.m_StopTime))
                duration = Math.Max(duration, clip.m_MuscleClip.m_StopTime);
            if (streamedFrames != null)
                foreach (var frame in streamedFrames)
                    if (frame != null && IsFinite(frame.time)) duration = Math.Max(duration, frame.time);
            var denseForDuration = embeddedClip?.m_DenseClip;
            if (denseForDuration != null && denseForDuration.m_FrameCount > 0 &&
                denseForDuration.m_SampleRate > 0 && IsFinite(denseForDuration.m_SampleRate) &&
                IsFinite(denseForDuration.m_BeginTime))
                duration = Math.Max(duration, denseForDuration.m_BeginTime + (denseForDuration.m_FrameCount - 1) / denseForDuration.m_SampleRate);

            scene.ClipEventCount = clip.m_Events?.Length ?? 0;
            scene.Duration = Math.Max(duration, scene.SampleRate > 0 ? 1f / scene.SampleRate : 0.0333f);

            var byPath = new Dictionary<string, PlaybackTransformTrack>(StringComparer.Ordinal);
            var positionsByPath = new Dictionary<string, List<PlaybackVectorKey>>(StringComparer.Ordinal);
            var scalesByPath = new Dictionary<string, List<PlaybackVectorKey>>(StringComparer.Ordinal);
            var eulersByPath = new Dictionary<string, List<PlaybackVectorKey>>(StringComparer.Ordinal);
            var rotationsByPath = new Dictionary<string, List<PlaybackQuaternionKey>>(StringComparer.Ordinal);
            var markedChannels = new HashSet<string>(StringComparer.Ordinal);

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

            string NormalizePath(string path) => NormalizeClipPath(scene, path);

            void MarkChannel(string path, string channel)
            {
                if (markedChannels.Add(path + "|" + channel))
                    scene.DirectCurveCount++;
            }

            void AddVectorKey(Dictionary<string, List<PlaybackVectorKey>> destination, string path, string channel,
                float time, NVector3 value, NVector3 inSlope = default(NVector3),
                NVector3 outSlope = default(NVector3), bool hasTangents = false)
            {
                if (!IsFinite(time) || !IsFinite(value.X) || !IsFinite(value.Y) || !IsFinite(value.Z)) return;
                path = NormalizePath(path);
                if (!destination.TryGetValue(path, out var keys))
                {
                    keys = new List<PlaybackVectorKey>();
                    destination[path] = keys;
                }
                keys.Add(new PlaybackVectorKey
                {
                    Time = time, Value = value,
                    InSlope = inSlope, OutSlope = outSlope, HasTangents = hasTangents
                });
                Track(path);
                MarkChannel(path, channel);
            }

            void AddRotationKey(string path, float time, NQuaternion value)
            {
                if (!IsFinite(time) || !IsFinite(value.X) || !IsFinite(value.Y) || !IsFinite(value.Z) || !IsFinite(value.W)) return;
                path = NormalizePath(path);
                if (!rotationsByPath.TryGetValue(path, out var keys))
                {
                    keys = new List<PlaybackQuaternionKey>();
                    rotationsByPath[path] = keys;
                }
                keys.Add(new PlaybackQuaternionKey { Time = time, Value = Normalize(value) });
                Track(path);
                MarkChannel(path, "rotation");
            }

            var pathHashMap = BuildPathHashMap(scene, transforms);
            foreach (var curve in clip.m_PositionCurves ?? Array.Empty<AssetStudio.Vector3Curve>())
                foreach (var key in curve.curve?.m_Curve ?? Array.Empty<AssetStudio.Keyframe<AssetStudio.Vector3>>())
                    if (key != null) AddVectorKey(positionsByPath, curve.path, "position", key.time,
                        new NVector3(key.value.X, key.value.Y, key.value.Z),
                        new NVector3(key.inSlope.X, key.inSlope.Y, key.inSlope.Z),
                        new NVector3(key.outSlope.X, key.outSlope.Y, key.outSlope.Z), key.weightedMode == 0);

            foreach (var curve in clip.m_ScaleCurves ?? Array.Empty<AssetStudio.Vector3Curve>())
                foreach (var key in curve.curve?.m_Curve ?? Array.Empty<AssetStudio.Keyframe<AssetStudio.Vector3>>())
                    if (key != null) AddVectorKey(scalesByPath, curve.path, "scale", key.time,
                        new NVector3(key.value.X, key.value.Y, key.value.Z),
                        new NVector3(key.inSlope.X, key.inSlope.Y, key.inSlope.Z),
                        new NVector3(key.outSlope.X, key.outSlope.Y, key.outSlope.Z), key.weightedMode == 0);

            foreach (var curve in clip.m_EulerCurves ?? Array.Empty<AssetStudio.Vector3Curve>())
                foreach (var key in curve.curve?.m_Curve ?? Array.Empty<AssetStudio.Keyframe<AssetStudio.Vector3>>())
                    if (key != null) AddVectorKey(eulersByPath, curve.path, "euler", key.time,
                        new NVector3(key.value.X, key.value.Y, key.value.Z),
                        new NVector3(key.inSlope.X, key.inSlope.Y, key.inSlope.Z),
                        new NVector3(key.outSlope.X, key.outSlope.Y, key.outSlope.Z), key.weightedMode == 0);

            foreach (var curve in clip.m_RotationCurves ?? Array.Empty<AssetStudio.QuaternionCurve>())
                foreach (var key in curve.curve?.m_Curve ?? Array.Empty<AssetStudio.Keyframe<AssetStudio.Quaternion>>())
                    if (key != null) AddRotationKey(curve.path, key.time, new NQuaternion(key.value.X, key.value.Y, key.value.Z, key.value.W));

            foreach (var curve in clip.m_CompressedRotationCurves ?? Array.Empty<AssetStudio.CompressedAnimationCurve>())
            {
                try
                {
                    var times = curve.m_Times?.UnpackInts() ?? Array.Empty<int>();
                    var quaternions = curve.m_Values?.UnpackQuats() ?? Array.Empty<AssetStudio.Quaternion>();
                    int count = Math.Min(times.Length, quaternions.Length);
                    if (count == 0) continue;
                    double maxPacked = Math.Pow(2, Math.Max(1, (int)curve.m_Times.m_BitSize)) - 1;
                    for (int i = 0; i < count; i++)
                    {
                        float time = Math.Max(0, Math.Min(scene.Duration, (float)(times[i] / maxPacked * scene.Duration)));
                        var q = quaternions[i];
                        AddRotationKey(curve.m_Path, time, new NQuaternion(q.X, q.Y, q.Z, q.W));
                    }
                }
                catch { /* Keep other readable channels when one compressed channel is malformed. */ }
            }

            var bindingConstant = clip.m_ClipBindingConstant;
            if ((bindingConstant == null || bindingConstant.genericBindings == null || bindingConstant.genericBindings.Length == 0) &&
                embeddedClip?.m_Binding != null)
            {
                try
                {
                    var reconstructed = embeddedClip.ConvertValueArrayToGenericBinding();
                    if (reconstructed?.genericBindings != null && reconstructed.genericBindings.Length > 0)
                        bindingConstant = reconstructed;
                }
                catch (Exception ex) { extractionNote = AppendNote(extractionNote, "Binding table could not be reconstructed: " + ex.Message); }
            }

            if (embeddedClip != null && bindingConstant?.genericBindings != null)
            {
                try
                {
                    int streamedCurveCount = embeddedClip.m_StreamedClip == null
                        ? 0 : (int)Math.Min((long)int.MaxValue, (long)embeddedClip.m_StreamedClip.curveCount);
                    var dense = embeddedClip.m_DenseClip;
                    int denseCurveCount = dense == null ? 0 : (int)Math.Min((long)int.MaxValue, (long)dense.m_CurveCount);
                    var constant = embeddedClip.m_ConstantClip;
                    int constantStart = checked(streamedCurveCount + denseCurveCount);
                    var streamedByCurve = new Dictionary<int, List<PlaybackScalarKey>>();

                    // Streamed keys are scalar curves. Their indices are sparse curve indices,
                    // not sequential offsets in frame.keyList. Treating the key list as a packed
                    // Vector3/Quaternion array misassigned X/Y/Z and quaternion channels, which
                    // can explode animated vertices even when the Mesh and bind poses are valid.
                    if (streamedFrames != null && streamedCurveCount > 0)
                    {
                        // ReadData returns the real endpoint frames as well as interior frames.
                        // Dropping frame 0 and the last frame can leave a short skill clip with
                        // only one sampled key, making Play advance the clock while the model stays
                        // frozen. Keep all finite frames; ReadData only derives incoming tangents
                        // for frame indices 2..Count-2, so endpoint interpolation remains linear.
                        for (int frameIndex = 0; frameIndex < streamedFrames.Count; frameIndex++)
                        {
                            var frame = streamedFrames[frameIndex];
                            if (frame == null || !IsFinite(frame.time)) continue;
                            foreach (var key in frame.keyList ?? Array.Empty<AssetStudio.StreamedClip.StreamedCurveKey>())
                            {
                                if (key == null || key.index < 0 || key.index >= streamedCurveCount || !IsFinite(key.value))
                                    continue;
                                if (!streamedByCurve.TryGetValue(key.index, out var scalarKeys))
                                {
                                    scalarKeys = new List<PlaybackScalarKey>();
                                    streamedByCurve[key.index] = scalarKeys;
                                }

                                var decodedKey = new PlaybackScalarKey
                                {
                                    Time = frame.time,
                                    Value = key.value,
                                    InSlope = key.inSlope,
                                    OutSlope = key.outSlope,
                                    HasTangents = frameIndex >= 2 && frameIndex < streamedFrames.Count - 1 &&
                                        IsFinite(key.inSlope) && IsFinite(key.outSlope)
                                };
                                int duplicateIndex = scalarKeys.FindIndex(existing =>
                                    Math.Abs(existing.Time - frame.time) < 0.000001f);
                                if (duplicateIndex >= 0)
                                    scalarKeys[duplicateIndex] = decodedKey;
                                else
                                    scalarKeys.Add(decodedKey);
                            }
                        }
                        foreach (var keys in streamedByCurve.Values)
                            keys.Sort((a, b) => a.Time.CompareTo(b.Time));
                    }

                    var defaultByPath = scene.Nodes
                        .GroupBy(node => node.Path ?? "", StringComparer.Ordinal)
                        .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                    int scalarOffset = 0;
                    int skippedBindingCount = 0;

                    foreach (var binding in bindingConstant.genericBindings)
                    {
                        if (binding == null) continue;
                        int attribute = (int)binding.attribute;
                        int width = BindingWidth(binding);
                        int scalarEnd = checked(scalarOffset + width);
                        bool isTransformChannel = binding.typeID == AssetStudio.ClassIDType.Transform &&
                            (attribute == 1 || attribute == 2 || attribute == 3 || attribute == 4);

                        if (isTransformChannel)
                        {
                            string path = pathHashMap.TryGetValue(binding.path, out var bindingPath)
                                ? NormalizePath(bindingPath)
                                : "#unresolved_" + binding.path.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);

                            var vectorDestination = attribute == 1 ? positionsByPath :
                                attribute == 3 ? scalesByPath : attribute == 4 ? eulersByPath : null;
                            bool alreadyHasDirectKeys = attribute == 2
                                ? rotationsByPath.ContainsKey(path)
                                : vectorDestination != null && vectorDestination.ContainsKey(path);

                            if (alreadyHasDirectKeys || path.StartsWith("#unresolved_", StringComparison.Ordinal))
                            {
                                if (path.StartsWith("#unresolved_", StringComparison.Ordinal))
                                    skippedBindingCount++;
                                scalarOffset = scalarEnd;
                                continue;
                            }

                            bool isStreamed = scalarOffset >= 0 && scalarEnd <= streamedCurveCount;
                            bool isDense = dense != null && dense.m_SampleArray != null &&
                                scalarOffset >= streamedCurveCount && scalarEnd <= constantStart &&
                                scalarEnd - streamedCurveCount <= denseCurveCount;
                            bool isConstant = constant?.data != null &&
                                scalarOffset >= constantStart && scalarEnd <= constantStart + constant.data.Length;

                            if (isStreamed)
                            {
                                var times = Enumerable.Range(scalarOffset, width)
                                    .Where(index => streamedByCurve.ContainsKey(index))
                                    .SelectMany(index => streamedByCurve[index].Select(key => key.Time))
                                    .Where(IsFinite)
                                    .Distinct()
                                    .OrderBy(time => time)
                                    .ToArray();

                                PlaybackNode baseNode = defaultByPath.TryGetValue(path, out var foundNode) ? foundNode : null;
                                var baselinePosition = baseNode?.Position ?? NVector3.Zero;
                                var baselineScale = baseNode?.Scale ?? NVector3.One;
                                var baselineEuler = NVector3.Zero;
                                var baselineRotation = baseNode?.Rotation ?? NQuaternion.Identity;

                                foreach (float time in times)
                                {
                                    if (attribute == 2)
                                    {
                                        var q = new NQuaternion(
                                            SampleScalar(streamedByCurve, scalarOffset, time, baselineRotation.X),
                                            SampleScalar(streamedByCurve, scalarOffset + 1, time, baselineRotation.Y),
                                            SampleScalar(streamedByCurve, scalarOffset + 2, time, baselineRotation.Z),
                                            SampleScalar(streamedByCurve, scalarOffset + 3, time, baselineRotation.W));
                                        AddRotationKey(path, time, q);
                                    }
                                    else
                                    {
                                        var baseline = attribute == 1 ? baselinePosition :
                                            attribute == 3 ? baselineScale : baselineEuler;
                                        var value = new NVector3(
                                            SampleScalar(streamedByCurve, scalarOffset, time, baseline.X),
                                            SampleScalar(streamedByCurve, scalarOffset + 1, time, baseline.Y),
                                            SampleScalar(streamedByCurve, scalarOffset + 2, time, baseline.Z));
                                        AddVectorKey(vectorDestination, path,
                                            attribute == 1 ? "position" : attribute == 3 ? "scale" : "euler",
                                            time, value);
                                    }
                                }
                            }
                            else if (isDense && denseCurveCount > 0 &&
                                dense.m_FrameCount > 0 && dense.m_SampleRate > 0 && IsFinite(dense.m_SampleRate) &&
                                IsFinite(dense.m_BeginTime))
                            {
                                int relativeOffset = scalarOffset - streamedCurveCount;
                                for (int frameIndex = 0; frameIndex < dense.m_FrameCount; frameIndex++)
                                {
                                    long baseOffsetLong = (long)frameIndex * denseCurveCount + relativeOffset;
                                    if (baseOffsetLong < 0 || baseOffsetLong + width > dense.m_SampleArray.Length) break;
                                    int baseOffset = (int)baseOffsetLong;
                                    float time = dense.m_BeginTime + frameIndex / dense.m_SampleRate;
                                    if (attribute == 2)
                                    {
                                        AddRotationKey(path, time, new NQuaternion(
                                            dense.m_SampleArray[baseOffset], dense.m_SampleArray[baseOffset + 1],
                                            dense.m_SampleArray[baseOffset + 2], dense.m_SampleArray[baseOffset + 3]));
                                    }
                                    else
                                    {
                                        AddVectorKey(vectorDestination, path,
                                            attribute == 1 ? "position" : attribute == 3 ? "scale" : "euler",
                                            time, new NVector3(
                                                dense.m_SampleArray[baseOffset],
                                                dense.m_SampleArray[baseOffset + 1],
                                                dense.m_SampleArray[baseOffset + 2]));
                                    }
                                }
                            }
                            else if (isConstant)
                            {
                                int relativeOffset = scalarOffset - constantStart;
                                if (attribute == 2)
                                {
                                    var q = new NQuaternion(
                                        constant.data[relativeOffset], constant.data[relativeOffset + 1],
                                        constant.data[relativeOffset + 2], constant.data[relativeOffset + 3]);
                                    AddRotationKey(path, 0, q);
                                    AddRotationKey(path, scene.Duration, q);
                                }
                                else
                                {
                                    var v = new NVector3(
                                        constant.data[relativeOffset], constant.data[relativeOffset + 1],
                                        constant.data[relativeOffset + 2]);
                                    AddVectorKey(vectorDestination, path,
                                        attribute == 1 ? "position" : attribute == 3 ? "scale" : "euler", 0, v);
                                    AddVectorKey(vectorDestination, path,
                                        attribute == 1 ? "position" : attribute == 3 ? "scale" : "euler", scene.Duration, v);
                                }
                            }
                            else if (scalarOffset < streamedCurveCount || scalarOffset < constantStart)
                            {
                                skippedBindingCount++;
                            }
                        }

                        scalarOffset = scalarEnd;
                    }

                    if (skippedBindingCount > 0)
                        extractionNote = AppendNote(extractionNote,
                            skippedBindingCount + " Transform bindings could not be mapped to a complete streamed/dense/constant channel group or hierarchy path.");
                }
                catch (Exception ex)
                {
                    extractionNote = AppendNote(extractionNote, "Some streamed/dense/constant channels were skipped: " + ex.Message);
                }
            }
            else if (clip.m_MuscleClip != null && scene.DirectCurveCount == 0)
            {
                extractionNote = AppendNote(extractionNote, "The clip has a muscle container but no compatible binding table was available.");
            }

            foreach (var pair in positionsByPath)
                Track(pair.Key).Positions = pair.Value.OrderBy(k => k.Time)
                    .GroupBy(k => Math.Round(k.Time, 6)).Select(group => group.First()).ToArray();
            foreach (var pair in scalesByPath)
                Track(pair.Key).Scales = pair.Value.OrderBy(k => k.Time)
                    .GroupBy(k => Math.Round(k.Time, 6)).Select(group => group.First()).ToArray();
            foreach (var pair in eulersByPath)
                Track(pair.Key).EulerRotations = pair.Value.OrderBy(k => k.Time)
                    .GroupBy(k => Math.Round(k.Time, 6)).Select(group => group.First()).ToArray();
            foreach (var pair in rotationsByPath)
                Track(pair.Key).Rotations = pair.Value.OrderBy(k => k.Time)
                    .GroupBy(k => Math.Round(k.Time, 6)).Select(group => group.First()).ToArray();

            var nodePaths = new HashSet<string>(scene.Nodes.Select(node => node.Path), StringComparer.Ordinal);
            foreach (var sourceTrack in byPath.Values.ToArray())
            {
                if (nodePaths.Contains(sourceTrack.Path) ||
                    string.IsNullOrWhiteSpace(sourceTrack.Path) ||
                    sourceTrack.Path.StartsWith("#unresolved_", StringComparison.Ordinal))
                    continue;

                var sourcePath = sourceTrack.Path.Replace('\\', '/').Trim('/');
                var lastSegment = sourcePath.Split('/').LastOrDefault();
                if (string.IsNullOrWhiteSpace(lastSegment)) continue;

                var matches = scene.Nodes
                    .Where(node =>
                        string.Equals(node.Name, lastSegment, StringComparison.Ordinal) ||
                        (!string.IsNullOrEmpty(node.Path) &&
                            (string.Equals(node.Path, sourcePath, StringComparison.Ordinal) ||
                             node.Path.EndsWith("/" + sourcePath, StringComparison.Ordinal) ||
                             sourcePath.EndsWith("/" + node.Path, StringComparison.Ordinal))))
                    .Select(node => node.Path)
                    .Distinct(StringComparer.Ordinal)
                    .Take(2)
                    .ToArray();
                if (matches.Length != 1) continue;

                string targetPath = matches[0];
                if (string.Equals(targetPath, sourceTrack.Path, StringComparison.Ordinal)) continue;
                if (!byPath.TryGetValue(targetPath, out var targetTrack))
                {
                    byPath.Remove(sourceTrack.Path);
                    sourceTrack.Path = targetPath;
                    byPath[targetPath] = sourceTrack;
                    continue;
                }

                targetTrack.Positions = targetTrack.Positions.Concat(sourceTrack.Positions)
                    .OrderBy(key => key.Time).GroupBy(key => Math.Round(key.Time, 6)).Select(group => group.Last()).ToArray();
                targetTrack.Rotations = targetTrack.Rotations.Concat(sourceTrack.Rotations)
                    .OrderBy(key => key.Time).GroupBy(key => Math.Round(key.Time, 6)).Select(group => group.Last()).ToArray();
                targetTrack.EulerRotations = targetTrack.EulerRotations.Concat(sourceTrack.EulerRotations)
                    .OrderBy(key => key.Time).GroupBy(key => Math.Round(key.Time, 6)).Select(group => group.Last()).ToArray();
                targetTrack.Scales = targetTrack.Scales.Concat(sourceTrack.Scales)
                    .OrderBy(key => key.Time).GroupBy(key => Math.Round(key.Time, 6)).Select(group => group.Last()).ToArray();
                byPath.Remove(sourceTrack.Path);
            }

            nodePaths = new HashSet<string>(scene.Nodes.Select(node => node.Path), StringComparer.Ordinal);
            foreach (var track in byPath.Values)
                if (!nodePaths.Contains(track.Path)) scene.UnboundCurveCount++;
            scene.Tracks = byPath.Values.OrderBy(track => track.Path, StringComparer.Ordinal).ToList();
            scene.CurveExtractionNotes = extractionNote;
        }

        private static string AppendNote(string current, string next) =>
            string.IsNullOrWhiteSpace(current) ? next : current + " " + next;

        private static int BindingWidth(AssetStudio.GenericBinding binding)
        {
            if (binding == null || binding.typeID != AssetStudio.ClassIDType.Transform)
                return 1;
            switch ((int)binding.attribute)
            {
                case 1: // Transform position x/y/z
                case 3: // Transform scale x/y/z
                case 4: // Transform Euler x/y/z
                    return 3;
                case 2: // Transform quaternion x/y/z/w
                    return 4;
                default:
                    return 1;
            }
        }

        private static float SampleScalar(
            Dictionary<int, List<PlaybackScalarKey>> curves, int curveIndex, float time, float fallback)
        {
            if (curves == null || !curves.TryGetValue(curveIndex, out var keys) || keys == null || keys.Count == 0)
                return fallback;
            if (time <= keys[0].Time) return keys[0].Value;
            if (time >= keys[keys.Count - 1].Time) return keys[keys.Count - 1].Value;

            int next = keys.FindIndex(key => key.Time >= time);
            if (next <= 0) return keys[0].Value;
            var a = keys[next - 1];
            var b = keys[next];
            float span = Math.Max(0.000001f, b.Time - a.Time);
            float u = Math.Max(0, Math.Min(1, (time - a.Time) / span));
            if (a.HasTangents && b.HasTangents && IsFinite(a.OutSlope) && IsFinite(b.InSlope))
            {
                float u2 = u * u;
                float u3 = u2 * u;
                return (2f * u3 - 3f * u2 + 1f) * a.Value +
                    (u3 - 2f * u2 + u) * span * a.OutSlope +
                    (-2f * u3 + 3f * u2) * b.Value +
                    (u3 - u2) * span * b.InSlope;
            }
            return a.Value + (b.Value - a.Value) * u;
        }

        private static Dictionary<uint, string> BuildPathHashMap(
            VisualLabPlaybackScene scene, List<AssetStudio.Transform> transforms)
        {
            var result = new Dictionary<uint, string>();
            // Unity/AssetStudio path hashes include the whole transform path and each suffix.
            // Mapping them here lets GenericBinding entries resolve even when the Avatar is not present.
            foreach (var node in scene.Nodes)
            {
                string candidate = string.IsNullOrWhiteSpace(node.Path)
                    ? scene.ModelName
                    : scene.ModelName + "/" + node.Path;
                while (!string.IsNullOrEmpty(candidate))
                {
                    result[ComputeUnityPathHash(candidate)] = NormalizeClipPath(scene, candidate);
                    int slash = candidate.IndexOf('/');
                    if (slash < 0) break;
                    candidate = candidate.Substring(slash + 1);
                }
            }

            foreach (var transform in transforms ?? new List<AssetStudio.Transform>())
            {
                if (transform?.m_GameObject == null || !transform.m_GameObject.TryGet(out AssetStudio.GameObject gameObject) || gameObject == null)
                    continue;
                foreach (var pointer in gameObject.m_Components ?? Array.Empty<PPtr<AssetStudio.Component>>())
                {
                    if (pointer == null || !pointer.TryGet(out AssetStudio.Component component) || !(component is AssetStudio.Animator animator) ||
                        animator.m_Avatar == null || !animator.m_Avatar.TryGet(out AssetStudio.Avatar avatar) || avatar == null)
                        continue;
                    foreach (var pair in avatar.m_TOS ?? Array.Empty<KeyValuePair<uint, string>>())
                    {
                        if (!result.ContainsKey(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                            result[pair.Key] = NormalizeClipPath(scene, pair.Value);
                    }
                }
            }
            return result;
        }

        private static uint ComputeUnityPathHash(string value)
        {
            uint crc = 0xffffffffu;
            var bytes = System.Text.Encoding.UTF8.GetBytes(value ?? "");
            foreach (byte b in bytes)
            {
                crc ^= b;
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc & 1u) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
            }
            return ~crc;
        }

        private static string NormalizeClipPath(VisualLabPlaybackScene scene, string path)
        {
            path = (path ?? "").Replace('\\', '/').Trim('/');
            if (path.Length == 0) return "";
            if (string.Equals(path, scene.ModelName, StringComparison.Ordinal)) return "";
            if (!string.IsNullOrWhiteSpace(scene.ModelName) &&
                path.StartsWith(scene.ModelName + "/", StringComparison.Ordinal))
                path = path.Substring(scene.ModelName.Length + 1);

            if (scene.Nodes.Any(node => string.Equals(node.Path, path, StringComparison.Ordinal)))
                return path;

            var suffixMatches = scene.Nodes
                .Where(node => string.Equals(node.Name, path, StringComparison.Ordinal) ||
                    (!string.IsNullOrEmpty(node.Path) && (path.EndsWith("/" + node.Path, StringComparison.Ordinal) ||
                    node.Path.EndsWith("/" + path, StringComparison.Ordinal))))
                .Take(2).ToArray();
            return suffixMatches.Length == 1 ? suffixMatches[0].Path : path;
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
            (obj?.assetsFile?.fullName ?? "") + "|" +
            (obj?.assetsFile?.originalPath ?? "") + "|" +
            (obj?.assetsFile?.fileName ?? "?") + "|" +
            (obj?.m_PathID.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0");

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
        private readonly string[] _heroSkinBundlePaths;
        private readonly List<Juancho.VisualLab.VisualLabUnityAssetItem> _assets;
        private readonly ComboBox _skinBundleCombo = new ComboBox();
        private readonly ComboBox _rootCombo = new ComboBox();
        private readonly ComboBox _clipCombo = new ComboBox();
        private bool _loadingChoices;
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

        public static bool IsHeroSkinBundlePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string name;
            try { name = Path.GetFileName(path); } catch { return false; }
            return !string.IsNullOrWhiteSpace(name) &&
                name.StartsWith("hero_", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith("_skin.unity3d", StringComparison.OrdinalIgnoreCase) &&
                name.Length > "hero__skin.unity3d".Length;
        }

        public static string[] FindHeroSkinBundlePaths(IEnumerable<string> inputFiles)
        {
            return (inputFiles ?? Enumerable.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path) && IsHeroSkinBundlePath(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public static bool BundleHasModelAndClip(IEnumerable<Juancho.VisualLab.VisualLabUnityAssetItem> assets, string bundlePath)
        {
            if (string.IsNullOrWhiteSpace(bundlePath)) return false;
            var inBundle = (assets ?? Enumerable.Empty<Juancho.VisualLab.VisualLabUnityAssetItem>())
                .Where(item => SamePath(item.SourceBundlePath, bundlePath)).ToArray();
            return inBundle.Any(item => string.Equals(item.ClassName, "GameObject", StringComparison.OrdinalIgnoreCase)) &&
                inBundle.Any(item => string.Equals(item.ClassName, "Mesh", StringComparison.OrdinalIgnoreCase)) &&
                inBundle.Any(item => string.Equals(item.ClassName, "AnimationClip", StringComparison.OrdinalIgnoreCase));
        }

        private static bool SamePath(string left, string right)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
                    string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public JuanchoVisualLabAnimationPlaybackForm(IEnumerable<string> inputFiles, IEnumerable<Juancho.VisualLab.VisualLabUnityAssetItem> assets)
        {
            _inputFiles = (inputFiles ?? Enumerable.Empty<string>()).Where(File.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            _assets = (assets ?? Enumerable.Empty<Juancho.VisualLab.VisualLabUnityAssetItem>()).ToList();
            _heroSkinBundlePaths = FindHeroSkinBundlePaths(_inputFiles);
            if (_inputFiles.Length == 0) throw new ArgumentException("Scan a package folder before opening the animation player.", nameof(inputFiles));
            if (_heroSkinBundlePaths.Length == 0)
                throw new InvalidDataException("The scanned package does not contain a file named hero_<heroname>_skin.unity3d. The interactive player will not substitute an unrelated bundle.");

            Text = "Juancho MLBB Visual Lab — Interactive Model & Animation Player";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1480, 940);
            MinimumSize = new Size(1000, 680);
            Font = new System.Drawing.Font("Segoe UI", 9F);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var top = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };

            top.Controls.Add(new Label { Text = "Hero skin bundle:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            _skinBundleCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _skinBundleCombo.Width = 315;
            foreach (var bundle in _heroSkinBundlePaths)
                _skinBundleCombo.Items.Add(new SkinBundleChoice(bundle));
            _skinBundleCombo.SelectedIndexChanged += (s, e) => PopulateModelAndClipChoices();
            if (_skinBundleCombo.Items.Count > 0) _skinBundleCombo.SelectedIndex = 0;
            top.Controls.Add(_skinBundleCombo);

            top.Controls.Add(new Label { Text = "Model root (GameObject):", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
            _rootCombo.DropDownStyle = ComboBoxStyle.DropDownList; _rootCombo.Width = 370;
            _rootCombo.SelectedIndexChanged += async (s, e) =>
            {
                UpdateLoadEnabled();
                if (!_loadingChoices && _scene != null && _rootCombo.SelectedItem is AssetChoice && _clipCombo.SelectedItem is AssetChoice)
                    await LoadPreviewAsync();
            };
            top.Controls.Add(_rootCombo);

            top.Controls.Add(new Label { Text = "AnimationClip list (select to play):", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
            _clipCombo.DropDownStyle = ComboBoxStyle.DropDownList; _clipCombo.Width = 420;
            _clipCombo.SelectedIndexChanged += async (s, e) =>
            {
                UpdateLoadEnabled();
                if (!_loadingChoices && _scene != null && _rootCombo.SelectedItem is AssetChoice && _clipCombo.SelectedItem is AssetChoice)
                    await LoadPreviewAsync();
            };
            top.Controls.Add(_clipCombo);

            _loadButton.Text = "Load Model + Clip"; _loadButton.AutoSize = true;
            _loadButton.Click += async (s, e) => await LoadPreviewAsync();
            top.Controls.Add(_loadButton);
            PopulateModelAndClipChoices();

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
            _stopButton.Click += (s, e) => { _playing = false; _timer.Stop(); _clock.Stop(); _playButton.Text = "Play"; _time = 0; UpdateFrame(); };
            _loopCheck.CheckedChanged += (s, e) => UpdateFrame();
            _speedCombo.DropDownStyle = ComboBoxStyle.DropDownList; _speedCombo.Width = 86;
            foreach (var speed in new[] { "0.25x", "0.5x", "1x", "1.5x", "2x" }) _speedCombo.Items.Add(speed);
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
            _timer.Tick += Timer_Tick;
            Shown += async (s, e) =>
            {
                if (_rootCombo.SelectedItem != null && _clipCombo.SelectedItem != null)
                    await LoadPreviewAsync();
                else
                    _status.Text = "Choose a hero skin bundle that contains a Mesh, GameObject hierarchy, and AnimationClip.";
            };
            FormClosed += (s, e) => { _timer.Stop(); _clock.Stop(); _gl.Dispose(); };
        }

        private sealed class SkinBundleChoice
        {
            public string Path { get; }
            public SkinBundleChoice(string path) { Path = path; }
            public override string ToString() => System.IO.Path.GetFileName(Path) + "  [" + Path + "]";
        }

        private void PopulateModelAndClipChoices()
        {
            var previousRoot = (_rootCombo.SelectedItem as AssetChoice)?.Item.Key;
            var previousClip = (_clipCombo.SelectedItem as AssetChoice)?.Item.Key;
            _loadingChoices = true;
            _rootCombo.BeginUpdate(); _clipCombo.BeginUpdate();
            try
            {
                _rootCombo.Items.Clear(); _clipCombo.Items.Clear();
                var bundle = (_skinBundleCombo.SelectedItem as SkinBundleChoice)?.Path;
                if (string.IsNullOrWhiteSpace(bundle))
                {
                    _loadButton.Enabled = false;
                    _scene = null;
                    _status.Text = "No hero_<heroname>_skin.unity3d selected.";
                    return;
                }

                var bundleAssets = _assets.Where(item => SamePath(item.SourceBundlePath, bundle)).ToArray();
                var roots = bundleAssets
                    .Where(item => string.Equals(item.ClassName, "GameObject", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(item =>
                    {
                        var name = (item.Name ?? "").ToLowerInvariant();
                        var file = System.IO.Path.GetFileNameWithoutExtension(bundle).ToLowerInvariant();
                        return name.Contains("skin") || name.Contains("hero_") || name == file ? 1 : 0;
                    })
                    .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var clips = bundleAssets
                    .Where(item => string.Equals(item.ClassName, "AnimationClip", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(item =>
                    {
                        var hint = ((item.Name ?? "") + " " + bundle).ToLowerInvariant();
                        return hint.Contains("skill") || hint.Contains("ultimate") || hint.Contains("attack") || hint.Contains("spell") ? 1 : 0;
                    })
                    .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                foreach (var item in roots) _rootCombo.Items.Add(new AssetChoice(item));
                foreach (var item in clips) _clipCombo.Items.Add(new AssetChoice(item));

                int rootIndex = roots.ToList().FindIndex(item => string.Equals(item.Key, previousRoot, StringComparison.OrdinalIgnoreCase));
                int clipIndex = clips.ToList().FindIndex(item => string.Equals(item.Key, previousClip, StringComparison.OrdinalIgnoreCase));
                if (rootIndex < 0 && roots.Length > 0) rootIndex = 0;
                if (clipIndex < 0 && clips.Length > 0) clipIndex = 0;
                if (rootIndex >= 0) _rootCombo.SelectedIndex = rootIndex;
                if (clipIndex >= 0) _clipCombo.SelectedIndex = clipIndex;

                int meshCount = bundleAssets.Count(item => string.Equals(item.ClassName, "Mesh", StringComparison.OrdinalIgnoreCase));
                _status.Text = System.IO.Path.GetFileName(bundle) + " · " + roots.Length + " GameObjects · " +
                    meshCount + " Meshes · " + clips.Length + " AnimationClips" +
                    (meshCount > 0 && roots.Length > 0 && clips.Length > 0 ? " — select a clip to preview it" : " — missing one or more required object types in this hero skin bundle");
                _scene = null;
                UpdateLoadEnabled();
            }
            finally
            {
                _rootCombo.EndUpdate(); _clipCombo.EndUpdate();
                _loadingChoices = false;
            }
        }

        private void UpdateLoadEnabled()
        {
            var bundle = (_skinBundleCombo.SelectedItem as SkinBundleChoice)?.Path;
            _loadButton.Enabled = !string.IsNullOrWhiteSpace(bundle) &&
                _rootCombo.SelectedItem is AssetChoice root &&
                _clipCombo.SelectedItem is AssetChoice clip &&
                SamePath(root.Item.SourceBundlePath, bundle) &&
                SamePath(clip.Item.SourceBundlePath, bundle);
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
                _time = 0; _playing = false; _timer.Stop(); _clock.Stop(); _playButton.Text = "Play";
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
                else { _time = _scene.Duration; _playing = false; _timer.Stop(); _clock.Stop(); _playButton.Text = "Play"; }
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
