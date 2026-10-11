using AssetStudio;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityObject = AssetStudio.Object;

namespace Juancho.VisualLab
{
    public sealed class VisualLabUnityAssetItem
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public string ClassName { get; set; } = "";
        public long PathId { get; set; }
        public string SerializedFileName { get; set; } = "";
        public string SourceBundlePath { get; set; } = "";
        public string UnityVersion { get; set; } = "";
        public string Platform { get; set; } = "";
        public long SerializedBytes { get; set; }
        public int DependencyCount { get; set; }
        public int UnresolvedDependencyCount { get; set; }
        public string Status { get; set; } = "Indexed";
    }

    public sealed class VisualLabUnityDependency
    {
        public string SourceAssetKey { get; set; } = "";
        public string SourceAssetName { get; set; } = "";
        public string MemberPath { get; set; } = "";
        public int FileId { get; set; }
        public long TargetPathId { get; set; }
        public string TargetFileName { get; set; } = "";
        public string TargetName { get; set; } = "";
        public string TargetClassName { get; set; } = "";
        public bool IsResolved { get; set; }
        public string Status { get { return IsResolved ? "Resolved" : "Unresolved / not loaded"; } }
    }

    public sealed class VisualLabUnityIndexDiagnostic
    {
        public string Severity { get; set; } = "Info";
        public string Source { get; set; } = "";
        public string Message { get; set; } = "";
    }

    public sealed class VisualLabUnityIndexResult
    {
        public DateTime StartedAtUtc { get; set; }
        public DateTime CompletedAtUtc { get; set; }
        public long DurationMilliseconds { get; set; }
        public List<string> InputFiles { get; set; } = new List<string>();
        public List<VisualLabUnityAssetItem> Assets { get; set; } = new List<VisualLabUnityAssetItem>();
        public List<VisualLabUnityDependency> Dependencies { get; set; } = new List<VisualLabUnityDependency>();
        public List<VisualLabUnityIndexDiagnostic> Diagnostics { get; set; } = new List<VisualLabUnityIndexDiagnostic>();
        public int ResolvedDependencyCount { get { return Dependencies.Count(x => x.IsResolved); } }
        public int UnresolvedDependencyCount { get { return Dependencies.Count(x => !x.IsResolved); } }
        public int SerializedFileCount { get; set; }
    }

    /// <summary>
    /// Uses AssetStudio's loaded serialized objects to build a read-only package-wide object index.
    /// Does not write to the source bundle or serialized file.
    /// </summary>
    public static class VisualLabUnityIndexer
    {
        private static readonly BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo PPtrFileIdField = typeof(PPtr<UnityObject>).GetField("m_FileID", InstanceFields);
        private static readonly FieldInfo PPtrPathIdField = typeof(PPtr<UnityObject>).GetField("m_PathID", InstanceFields);
        private static readonly FieldInfo PPtrAssetsFileField = typeof(PPtr<UnityObject>).GetField("assetsFile", InstanceFields);
        private static readonly ConcurrentDictionary<Type, bool> PointerContainmentCache = new ConcurrentDictionary<Type, bool>();

        public static Task<VisualLabUnityIndexResult> AnalyzeFilesAsync(IEnumerable<string> paths)
        {
            var files = (paths ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x) && File.Exists(x))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (files.Length == 0)
                throw new ArgumentException("Select at least one existing Unity bundle or serialized asset file.", nameof(paths));

            return Task.Run(() => AnalyzeFiles(files));
        }

        private static VisualLabUnityIndexResult AnalyzeFiles(string[] files)
        {
            var result = new VisualLabUnityIndexResult { StartedAtUtc = DateTime.UtcNow, InputFiles = files.ToList() };
            var manager = new AssetsManager();
            var workspace = Path.Combine(Path.GetTempPath(), "JuanchoMLBBVisualLab", Guid.NewGuid().ToString("N"));
            var workspaceToOriginalPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                Directory.CreateDirectory(workspace);

                // Critical safety boundary: AssetStudio.LoadFiles calls MergeSplitAssets, which may
                // create merged files next to .split0 inputs. Only pass isolated copies to that API.
                var isolatedFiles = CopyInputsToWorkspace(files, workspace, workspaceToOriginalPath);
                MergeSplitPartsInWorkspace(workspace);
                manager.LoadFiles(isolatedFiles);
                result.SerializedFileCount = manager.assetsFileList.Count;

                var items = new List<VisualLabUnityAssetItem>();
                foreach (var serializedFile in manager.assetsFileList)
                {
                    var identity = GetFileIdentity(serializedFile);
                    foreach (var obj in serializedFile.Objects ?? new List<UnityObject>())
                    {
                        if (obj == null) continue;
                        var key = MakeAssetKey(identity, obj.m_PathID);
                        var item = new VisualLabUnityAssetItem
                        {
                            Key = key,
                            Name = GetObjectName(obj),
                            ClassName = obj.type.ToString(),
                            PathId = obj.m_PathID,
                            SerializedFileName = serializedFile.fileName ?? Path.GetFileName(serializedFile.fullName ?? ""),
                            SourceBundlePath = ResolveOriginalSourcePath(serializedFile, workspaceToOriginalPath, files, workspace),
                            UnityVersion = serializedFile.unityVersion ?? "",
                            Platform = serializedFile.m_TargetPlatform.ToString(),
                            SerializedBytes = obj.byteSize
                        };
                        items.Add(item);
                    }
                }

                var fileLookup = BuildFileLookup(manager.assetsFileList);
                var assetLookup = items.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);

                foreach (var serializedFile in manager.assetsFileList)
                {
                    var sourceFileIdentity = GetFileIdentity(serializedFile);
                    foreach (var obj in serializedFile.Objects ?? new List<UnityObject>())
                    {
                        if (obj == null) continue;
                        var sourceKey = MakeAssetKey(sourceFileIdentity, obj.m_PathID);
                        if (!assetLookup.TryGetValue(sourceKey, out var sourceItem)) continue;

                        var references = new List<VisualLabUnityDependency>();
                        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
                        CollectPointers(
                            obj,
                            obj.GetType(),
                            sourceItem,
                            serializedFile,
                            fileLookup,
                            assetLookup,
                            references,
                            visited,
                            "",
                            0);

                        foreach (var dependency in references)
                            result.Dependencies.Add(dependency);

                        sourceItem.DependencyCount = references.Count;
                        sourceItem.UnresolvedDependencyCount = references.Count(x => !x.IsResolved);
                        if (sourceItem.UnresolvedDependencyCount > 0)
                            sourceItem.Status = "Has unresolved references";
                    }
                }

                result.Assets = items
                    .OrderBy(x => x.SerializedFileName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.ClassName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.PathId)
                    .ToList();
                result.Dependencies = result.Dependencies
                    .OrderBy(x => x.SourceAssetName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.MemberPath, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (result.Assets.Count == 0)
                    result.Diagnostics.Add(new VisualLabUnityIndexDiagnostic
                    {
                        Severity = "Warning",
                        Message = "AssetStudio loaded no serialized objects from the selected files. Some files may be unsupported or require additional split/resource files."
                    });

                if (result.UnresolvedDependencyCount > 0)
                    result.Diagnostics.Add(new VisualLabUnityIndexDiagnostic
                    {
                        Severity = "Warning",
                        Message = result.UnresolvedDependencyCount + " object references could not be resolved against this loaded package. Some targets may be outside the selected bundle set or may use duplicate basenames."
                    });

                var duplicateNames = files
                    .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key)
                    .Take(10)
                    .ToArray();
                if (duplicateNames.Length > 0)
                    result.Diagnostics.Add(new VisualLabUnityIndexDiagnostic
                    {
                        Severity = "Warning",
                        Message = "Selected inputs contain duplicate file basenames (" + string.Join(", ", duplicateNames) + "). AssetStudio external references may be ambiguous for those names."
                    });

                result.Diagnostics.Add(new VisualLabUnityIndexDiagnostic
                {
                    Severity = "Info",
                    Message = "Objects and PPtr references were parsed from isolated copies in a temporary workspace. Original source files were not passed to AssetStudio's split-file merge path and are never modified by this index operation."
                });
            }
            catch (Exception ex)
            {
                result.Diagnostics.Add(new VisualLabUnityIndexDiagnostic
                {
                    Severity = "Error",
                    Source = string.Join("; ", files),
                    Message = "AssetStudio could not finish parsing the selected package: " + ex.Message
                });
                throw new InvalidDataException(
                    "Unity package parsing failed. Original source files were not modified; Visual Lab parsed temporary copies. Details: " + ex.Message,
                    ex);
            }
            finally
            {
                try { manager.Clear(); } catch { }
                try { if (Directory.Exists(workspace)) Directory.Delete(workspace, true); } catch { }
            }

            result.CompletedAtUtc = DateTime.UtcNow;
            result.DurationMilliseconds = Math.Max(0, (long)(result.CompletedAtUtc - result.StartedAtUtc).TotalMilliseconds);
            return result;
        }

        private static string[] CopyInputsToWorkspace(
            string[] sourceFiles,
            string workspace,
            Dictionary<string, string> workspaceToOriginalPath)
        {
            var commonRoot = GetCommonDirectory(sourceFiles);
            var copied = new List<string>(sourceFiles.Length);

            for (var i = 0; i < sourceFiles.Length; i++)
            {
                var source = Path.GetFullPath(sourceFiles[i]);
                var relative = Path.GetRelativePath(commonRoot, source);
                if (Path.IsPathRooted(relative) || relative == ".." ||
                    relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                    relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
                {
                    relative = Path.Combine("_external_inputs", i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture), Path.GetFileName(source));
                }

                var destination = Path.GetFullPath(Path.Combine(workspace, relative));
                if (!VisualLabPackageScanner.IsWithinDirectory(destination, workspace))
                    throw new InvalidOperationException("A selected input resolved outside the temporary parse workspace.");

                var destinationDirectory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrWhiteSpace(destinationDirectory))
                    Directory.CreateDirectory(destinationDirectory);
                File.Copy(source, destination, true);
                workspaceToOriginalPath[destination] = source;
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
                    if (parent == null)
                    {
                        common = Path.GetPathRoot(common);
                        break;
                    }
                    common = parent.FullName;
                }
            }

            return string.IsNullOrWhiteSpace(common) ? Path.GetPathRoot(Path.GetFullPath(files[0])) : common;
        }

        private static void MergeSplitPartsInWorkspace(string workspace)
        {
            // This intentionally mirrors the split-part behavior only inside the disposable workspace.
            var splitZeroFiles = Directory.GetFiles(workspace, "*.split0", SearchOption.AllDirectories);
            foreach (var splitZero in splitZeroFiles)
            {
                var destinationName = Path.GetFileNameWithoutExtension(splitZero);
                var directory = Path.GetDirectoryName(splitZero);
                if (string.IsNullOrWhiteSpace(directory)) continue;
                var destination = Path.Combine(directory, destinationName);
                if (File.Exists(destination)) continue;

                var parts = Directory.GetFiles(directory, destinationName + ".split*")
                    .OrderBy(path =>
                    {
                        var extension = Path.GetExtension(path);
                        var suffix = extension.Length > 6 ? extension.Substring(6) : "";
                        return int.TryParse(suffix, out var number) ? number : int.MaxValue;
                    })
                    .ToArray();

                if (parts.Length == 0) continue;
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    foreach (var part in parts)
                    {
                        using (var input = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read))
                            input.CopyTo(output);
                    }
                }
            }
        }

        private static string ResolveOriginalSourcePath(
            SerializedFile file,
            Dictionary<string, string> workspaceToOriginalPath,
            string[] originalInputs,
            string workspace)
        {
            var candidates = new[] { file.originalPath, file.fullName };
            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                try
                {
                    var full = Path.GetFullPath(candidate);
                    if (workspaceToOriginalPath.TryGetValue(full, out var original))
                        return original;
                }
                catch { }
            }

            var name = Path.GetFileName(file.originalPath ?? "");
            if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileName(file.fullName ?? "");
            var matches = originalInputs
                .Where(path => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (matches.Length == 1) return matches[0];

            return originalInputs.FirstOrDefault() ?? "";
        }

        private static Dictionary<string, SerializedFile> BuildFileLookup(IEnumerable<SerializedFile> files)
        {
            var lookup = new Dictionary<string, SerializedFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                AddLookup(lookup, file.fileName, file);
                AddLookup(lookup, Path.GetFileName(file.fullName ?? ""), file);
                AddLookup(lookup, Path.GetFileName(file.originalPath ?? ""), file);
            }
            return lookup;
        }

        private static void AddLookup(Dictionary<string, SerializedFile> lookup, string name, SerializedFile file)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (!lookup.ContainsKey(name)) lookup.Add(name, file);
        }

        private static void CollectPointers(
            object value,
            Type valueType,
            VisualLabUnityAssetItem sourceItem,
            SerializedFile owningFile,
            Dictionary<string, SerializedFile> fileLookup,
            Dictionary<string, VisualLabUnityAssetItem> assetLookup,
            List<VisualLabUnityDependency> dependencies,
            HashSet<object> visited,
            string memberPath,
            int depth)
        {
            if (value == null || depth > 24) return;
            var type = valueType ?? value.GetType();
            if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime))
                return;

            if (IsPPtr(type))
            {
                AddPointerDependency(value, memberPath, sourceItem, owningFile, fileLookup, assetLookup, dependencies);
                return;
            }

            // Avoid walking enormous vertex, color, and keyframe arrays when their declared
            // types cannot contain any PPtr<T> fields. This keeps large hero bundles tractable.
            if (!CanContainPointers(type)) return;
            if (!type.IsValueType && !visited.Add(value)) return;

            if (value is IEnumerable enumerable && !(value is string))
            {
                var index = 0;
                foreach (var child in enumerable)
                {
                    CollectPointers(child, child?.GetType(), sourceItem, owningFile, fileLookup, assetLookup, dependencies, visited, memberPath + "[" + index + "]", depth + 1);
                    index++;
                    if (index > 50000) break;
                }
                return;
            }

            var current = type;
            while (current != null && current != typeof(UnityObject) && current != typeof(object))
            {
                if (current.Namespace == "System" || current.Namespace == "System.Collections" || current.Namespace == "System.Collections.Generic")
                    break;

                foreach (var field in current.GetFields(InstanceFields | BindingFlags.DeclaredOnly))
                {
                    if (field.IsStatic) continue;
                    if (field.Name == "reader" || field.Name == "assetsFile" || field.Name == "assetsManager" ||
                        field.Name == "serializedType" || field.Name == "version" || field.Name == "buildType" ||
                        field.Name == "platform" || field.Name == "m_PathID" || field.Name == "byteSize" ||
                        field.Name == "m_CachedPtr")
                    {
                        continue;
                    }

                    object child;
                    try { child = field.GetValue(value); }
                    catch { continue; }
                    if (child == null) continue;

                    var childType = child.GetType();
                    if (childType.IsPrimitive || childType.IsEnum || childType == typeof(string) ||
                        childType == typeof(decimal) || childType == typeof(DateTime))
                    {
                        continue;
                    }

                    var path = string.IsNullOrEmpty(memberPath) ? field.Name : memberPath + "." + field.Name;
                    CollectPointers(child, childType, sourceItem, owningFile, fileLookup, assetLookup, dependencies, visited, path, depth + 1);
                }
                current = current.BaseType;
            }
        }

        private static bool CanContainPointers(Type type)
        {
            if (type == null) return false;
            return PointerContainmentCache.GetOrAdd(type, candidate => CanContainPointersCore(candidate, new HashSet<Type>()));
        }

        private static bool CanContainPointersCore(Type type, HashSet<Type> visiting)
        {
            if (type == null || type.IsPrimitive || type.IsEnum || type == typeof(string) ||
                type == typeof(decimal) || type == typeof(DateTime))
                return false;
            if (IsPPtr(type)) return true;

            if (type.IsArray)
                return CanContainPointersCore(type.GetElementType(), visiting);

            if (typeof(IEnumerable).IsAssignableFrom(type) && type.IsGenericType)
                return type.GetGenericArguments().Any(argument => CanContainPointersCore(argument, visiting));

            if (!type.IsValueType && type.Namespace != "AssetStudio")
                return false;
            if (!visiting.Add(type)) return false;

            try
            {
                var current = type;
                while (current != null && current != typeof(UnityObject) && current != typeof(object))
                {
                    if (current.Namespace == "System" || current.Namespace == "System.Collections" ||
                        current.Namespace == "System.Collections.Generic")
                        break;

                    foreach (var field in current.GetFields(InstanceFields | BindingFlags.DeclaredOnly))
                    {
                        if (field.IsStatic || field.Name == "reader" || field.Name == "assetsFile" ||
                            field.Name == "assetsManager" || field.Name == "serializedType" ||
                            field.Name == "version" || field.Name == "buildType" || field.Name == "platform" ||
                            field.Name == "m_PathID" || field.Name == "byteSize" || field.Name == "m_CachedPtr")
                            continue;

                        if (CanContainPointersCore(field.FieldType, visiting))
                            return true;
                    }
                    current = current.BaseType;
                }
                return false;
            }
            finally
            {
                visiting.Remove(type);
            }
        }

        private static bool IsPPtr(Type type)
        {
            return type != null && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(PPtr<>);
        }

        private static void AddPointerDependency(
            object pointer,
            string memberPath,
            VisualLabUnityAssetItem sourceItem,
            SerializedFile owningFile,
            Dictionary<string, SerializedFile> fileLookup,
            Dictionary<string, VisualLabUnityAssetItem> assetLookup,
            List<VisualLabUnityDependency> dependencies)
        {
            // PPtr<T> uses different runtime types for each T; reflect from the actual
            // closed generic type rather than a PPtr<Object> FieldInfo.
            var pointerType = pointer.GetType();
            var fileIdField = pointerType.GetField("m_FileID", InstanceFields);
            var pathIdField = pointerType.GetField("m_PathID", InstanceFields);
            var owningFileField = pointerType.GetField("assetsFile", InstanceFields);
            var fileId = fileIdField == null ? -1 : Convert.ToInt32(fileIdField.GetValue(pointer));
            var pathId = pathIdField == null ? 0 : Convert.ToInt64(pathIdField.GetValue(pointer));
            if (pathId == 0) return;

            var pointerOwner = owningFileField == null ? owningFile : owningFileField.GetValue(pointer) as SerializedFile;
            pointerOwner = pointerOwner ?? owningFile;
            SerializedFile targetFile = null;
            string targetFileName = pointerOwner.fileName ?? Path.GetFileName(pointerOwner.fullName ?? "");

            if (fileId == 0)
            {
                targetFile = pointerOwner;
            }
            else if (fileId > 0 && pointerOwner.m_Externals != null && fileId - 1 < pointerOwner.m_Externals.Count)
            {
                var externalName = pointerOwner.m_Externals[fileId - 1]?.fileName;
                if (!string.IsNullOrWhiteSpace(externalName))
                {
                    targetFileName = externalName;
                    fileLookup.TryGetValue(externalName, out targetFile);
                    if (targetFile == null) fileLookup.TryGetValue(Path.GetFileName(externalName), out targetFile);
                }
            }

            var targetIdentity = targetFile == null ? targetFileName : GetFileIdentity(targetFile);
            var targetKey = MakeAssetKey(targetIdentity, pathId);
            assetLookup.TryGetValue(targetKey, out var targetItem);

            // Some serialized references use the external file's basename while another loaded entry
            // reports a normalized relative path. Try the normalized file identity before marking missing.
            if (targetItem == null && targetFile != null)
            {
                targetIdentity = targetFile.fileName ?? Path.GetFileName(targetFile.fullName ?? "");
                assetLookup.TryGetValue(MakeAssetKey(targetIdentity, pathId), out targetItem);
            }

            dependencies.Add(new VisualLabUnityDependency
            {
                SourceAssetKey = sourceItem.Key,
                SourceAssetName = sourceItem.Name,
                MemberPath = string.IsNullOrWhiteSpace(memberPath) ? "(reference)" : memberPath,
                FileId = fileId,
                TargetPathId = pathId,
                TargetFileName = targetFileName,
                TargetName = targetItem?.Name ?? "",
                TargetClassName = targetItem?.ClassName ?? "",
                IsResolved = targetItem != null
            });
        }

        private static string GetFileIdentity(SerializedFile file)
        {
            if (file == null) return "";
            var name = file.fileName;
            if (!string.IsNullOrWhiteSpace(name)) return name;
            return Path.GetFileName(file.fullName ?? file.originalPath ?? "");
        }

        private static string MakeAssetKey(string fileIdentity, long pathId)
        {
            return (fileIdentity ?? "") + "|" + pathId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string GetObjectName(UnityObject obj)
        {
            var current = obj?.GetType();
            while (current != null && current != typeof(UnityObject) && current != typeof(object))
            {
                var field = current.GetField("m_Name", InstanceFields | BindingFlags.DeclaredOnly)
                    ?? current.GetField("name", InstanceFields | BindingFlags.DeclaredOnly);
                if (field != null && field.FieldType == typeof(string))
                {
                    try
                    {
                        var name = field.GetValue(obj) as string;
                        if (!string.IsNullOrWhiteSpace(name)) return name;
                    }
                    catch { }
                }
                current = current.BaseType;
            }
            return "(unnamed)";
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object x, object y) { return ReferenceEquals(x, y); }
            public int GetHashCode(object obj) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj); }
        }
    }
}
