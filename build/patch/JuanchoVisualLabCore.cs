using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Juancho.VisualLab
{
    public sealed class VisualLabAssetFile
    {
        public string FullPath { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public string Extension { get; set; } = "";
        public string Category { get; set; } = "";
        public string SuggestedFolderGroup { get; set; } = "";
        public long LengthBytes { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }
        public string Sha256 { get; set; } = "";
        public bool IsDuplicateContent { get; set; }
    }

    public sealed class VisualLabScanDiagnostic
    {
        public string Severity { get; set; } = "Info";
        public string Path { get; set; } = "";
        public string Message { get; set; } = "";
    }

    public sealed class VisualLabScanProgress
    {
        public int Completed { get; set; }
        public int Total { get; set; }
        public string CurrentPath { get; set; } = "";
    }

    public sealed class VisualLabScanResult
    {
        public int SchemaVersion { get; set; } = 1;
        public DateTime StartedAtUtc { get; set; }
        public DateTime CompletedAtUtc { get; set; }
        public long DurationMilliseconds { get; set; }
        public List<string> InputPaths { get; set; } = new List<string>();
        public List<string> ProtectedSourceRoots { get; set; } = new List<string>();
        public List<VisualLabAssetFile> Assets { get; set; } = new List<VisualLabAssetFile>();
        public List<VisualLabScanDiagnostic> Diagnostics { get; set; } = new List<VisualLabScanDiagnostic>();
        public int DuplicateFileCount { get { return Assets.Count(x => x.IsDuplicateContent); } }
        public long TotalBytes { get { return Assets.Sum(x => x.LengthBytes); } }
    }

    public enum VisualLabTimelineEventKind
    {
        Animation,
        Effect,
        Projectile,
        Activation,
        Marker
    }

    public sealed class VisualLabTimelineEvent
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "New event";
        public VisualLabTimelineEventKind Kind { get; set; } = VisualLabTimelineEventKind.Marker;
        public string SourceAssetPath { get; set; } = "";
        public double StartSeconds { get; set; }
        public double DurationSeconds { get; set; } = 1.0;
        public bool Enabled { get; set; } = true;
    }

    public sealed class VisualLabSequence
    {
        public int SchemaVersion { get; set; } = 1;
        public string Title { get; set; } = "Untitled skill sequence";
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
        public List<string> ProtectedSourceRoots { get; set; } = new List<string>();
        public List<VisualLabTimelineEvent> Events { get; set; } = new List<VisualLabTimelineEvent>();
    }

    public static class VisualLabSkillAssetClassifier
    {
        private static readonly Regex PassivePattern = new Regex(
            @"(?i)(?:passive|innate|passiv)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex[] SkillPatterns =
        {
            new Regex(@"(?i)(?:^|[^a-z0-9])(?:skill|ability|spell|cast|sp|sk|s)[ _.\-]*0?1(?:$|[^0-9])", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            new Regex(@"(?i)(?:^|[^a-z0-9])(?:skill|ability|spell|cast|sp|sk|s)[ _.\-]*0?2(?:$|[^0-9])", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            new Regex(@"(?i)(?:^|[^a-z0-9])(?:skill|ability|spell|cast|sp|sk|s)[ _.\-]*0?3(?:$|[^0-9])", RegexOptions.Compiled | RegexOptions.CultureInvariant)
        };

        public static string ClassifySlot(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            if (PassivePattern.IsMatch(value)) return "Passive";
            for (int i = 0; i < SkillPatterns.Length; i++)
                if (SkillPatterns[i].IsMatch(value)) return "Skill " + (i + 1);
            return "";
        }

        public static bool IsEffectOrSkillPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var lower = value.ToLowerInvariant();
            return lower.Contains("skill") || lower.Contains("ability") || lower.Contains("spell") ||
                   lower.Contains("effect") || lower.Contains("eff_") || lower.Contains("particle") ||
                   lower.Contains("projectile") || lower.Contains("trail") || lower.Contains("impact") ||
                   lower.Contains("passive") || lower.Contains("summon") || lower.Contains("ult_") ||
                   lower.Contains("_ult") || lower.Contains("bullet") || lower.Contains("explosion");
        }

        public static bool IsTextureExtension(string extension)
        {
            switch ((extension ?? "").ToLowerInvariant())
            {
                case ".png": case ".jpg": case ".jpeg": case ".bmp": case ".gif":
                case ".tif": case ".tiff": case ".tga": case ".dds": case ".ktx":
                case ".astc": case ".pvr": case ".webp": case ".exr": case ".hdr":
                    return true;
                default:
                    return false;
            }
        }
    }

    public static class VisualLabPackageScanner
    {
        private static readonly HashSet<string> KnownExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".unity3d", ".bundle", ".assets", ".asset", ".ab", ".ress", ".ress", ".resource",
            ".bytes", ".manifest", ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp",
            ".tga", ".dds", ".ktx", ".astc", ".pvr", ".exr", ".hdr", ".wav", ".ogg", ".mp3",
            ".prefab", ".mat", ".anim", ".controller", ".shader", ".json", ".lua"
        };

        private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
        private static JsonSerializerOptions CreateJsonOptions()
        {
            var options = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }

        public static async Task<VisualLabScanResult> ScanAsync(
            IEnumerable<string> inputPaths,
            IProgress<VisualLabScanProgress> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (inputPaths == null) throw new ArgumentNullException(nameof(inputPaths));
            var inputs = inputPaths.Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(Path.GetFullPath).Distinct(PathComparer()).ToList();
            if (inputs.Count == 0) throw new ArgumentException("Select at least one file or folder.", nameof(inputPaths));

            var result = new VisualLabScanResult { StartedAtUtc = DateTime.UtcNow, InputPaths = inputs };
            var roots = new List<string>();
            var candidates = new HashSet<string>(PathComparer());

            foreach (var input in inputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(input))
                {
                    roots.Add(input);
                    try
                    {
                        var enumeration = new EnumerationOptions
                        {
                            RecurseSubdirectories = true,
                            IgnoreInaccessible = true,
                            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
                        };
                        foreach (var file in Directory.EnumerateFiles(input, "*", enumeration))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (await IsCandidateAsync(file, cancellationToken).ConfigureAwait(false))
                                candidates.Add(Path.GetFullPath(file));
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Diagnostics.Add(new VisualLabScanDiagnostic
                        {
                            Severity = "Warning", Path = input, Message = "Folder enumeration was incomplete: " + ex.Message
                        });
                    }
                }
                else if (File.Exists(input))
                {
                    var parent = Path.GetDirectoryName(input);
                    if (!string.IsNullOrWhiteSpace(parent)) roots.Add(parent);
                    candidates.Add(input);
                }
                else
                {
                    result.Diagnostics.Add(new VisualLabScanDiagnostic
                    {
                        Severity = "Warning", Path = input, Message = "Input path does not exist and was skipped."
                    });
                }
            }

            result.ProtectedSourceRoots = roots.Where(Directory.Exists).Select(Path.GetFullPath).Distinct(PathComparer()).ToList();
            if (candidates.Count == 0)
            {
                result.Diagnostics.Add(new VisualLabScanDiagnostic
                {
                    Severity = "Warning", Message = "No recognized Unity bundles or common companion assets were found."
                });
            }

            var files = candidates.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            var bag = new ConcurrentBag<VisualLabAssetFile>();
            var errors = new ConcurrentBag<VisualLabScanDiagnostic>();
            var completed = 0;
            using (var gate = new SemaphoreSlim(4, 4))
            {
                var tasks = files.Select(async file =>
                {
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var info = new FileInfo(file);
                        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                        var root = result.ProtectedSourceRoots
                            .Where(r => IsWithinDirectory(file, r))
                            .OrderByDescending(r => r.Length).FirstOrDefault();
                        var relative = root == null ? Path.GetFileName(file) : Path.GetRelativePath(root, file);
                        var parentRelative = Path.GetDirectoryName(relative) ?? "";
                        bag.Add(new VisualLabAssetFile
                        {
                            FullPath = file,
                            RelativePath = relative,
                            Extension = info.Extension,
                            Category = Categorize(file),
                            SuggestedFolderGroup = parentRelative == "." ? "(root)" : parentRelative,
                            LengthBytes = info.Length,
                            LastWriteTimeUtc = info.LastWriteTimeUtc,
                            Sha256 = Convert.ToHexString(hash)
                        });
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        errors.Add(new VisualLabScanDiagnostic
                        {
                            Severity = "Warning", Path = file, Message = "Could not read or hash file: " + ex.Message
                        });
                    }
                    finally
                    {
                        var done = Interlocked.Increment(ref completed);
                        progress?.Report(new VisualLabScanProgress { Completed = done, Total = files.Length, CurrentPath = file });
                        gate.Release();
                    }
                });
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            result.Assets = bag.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var group in result.Assets.Where(x => !string.IsNullOrEmpty(x.Sha256)).GroupBy(x => x.Sha256, StringComparer.OrdinalIgnoreCase))
            {
                if (group.Count() > 1)
                    foreach (var item in group) item.IsDuplicateContent = true;
            }
            result.Diagnostics.AddRange(errors.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase));
            result.CompletedAtUtc = DateTime.UtcNow;
            result.DurationMilliseconds = Math.Max(0, (long)(result.CompletedAtUtc - result.StartedAtUtc).TotalMilliseconds);
            return result;
        }

        private static async Task<bool> IsCandidateAsync(string path, CancellationToken token)
        {
            var extension = Path.GetExtension(path);
            if (IsUnitySplitPartExtension(extension)) return true;
            if (KnownExtensions.Contains(extension)) return true;
            if (!string.IsNullOrEmpty(extension)) return false;

            var header = new byte[16];
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var read = await stream.ReadAsync(header.AsMemory(0, header.Length), token).ConfigureAwait(false);
                if (read < 7) return false;
                var signature = System.Text.Encoding.ASCII.GetString(header, 0, Math.Min(read, 11));
                return signature.StartsWith("UnityFS", StringComparison.Ordinal) ||
                       signature.StartsWith("UnityRaw", StringComparison.Ordinal) ||
                       signature.StartsWith("UnityWeb", StringComparison.Ordinal) ||
                       signature.StartsWith("UnityArchive", StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private static string Categorize(string path)
        {
            var ext = Path.GetExtension(path);
            var name = Path.GetFileNameWithoutExtension(path);
            if (IsUnitySplitPartExtension(ext)) return "Unity split part candidate";
            if (ext.Equals(".unity3d", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".bundle", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".ab", StringComparison.OrdinalIgnoreCase))
                return VisualLabSkillAssetClassifier.IsEffectOrSkillPath(path)
                    ? "Unity bundle candidate — skill/effect path hint"
                    : "Unity bundle candidate";
            if (ext.Equals(".assets", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".asset", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".ress", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".resource", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".bytes", StringComparison.OrdinalIgnoreCase))
                return "Unity asset / companion candidate";
            if (VisualLabSkillAssetClassifier.IsTextureExtension(ext))
                return "Texture candidate";
            if (ext.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
                return "Audio candidate";
            if (ext.Equals(".manifest", StringComparison.OrdinalIgnoreCase)) return "Manifest candidate";
            if (VisualLabSkillAssetClassifier.IsEffectOrSkillPath(path))
                return "Potential effect/skill asset (name/path heuristic)";
            if (new[] { ".prefab", ".mat", ".anim", ".controller", ".shader" }.Contains(ext, StringComparer.OrdinalIgnoreCase))
                return "Unity source asset candidate";
            if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase) || ext.Equals(".lua", StringComparison.OrdinalIgnoreCase))
                return "Script / metadata candidate";
            return "Other recognized companion";
        }

        private static bool IsUnitySplitPartExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension) || !extension.StartsWith(".split", StringComparison.OrdinalIgnoreCase)) return false;
            var suffix = extension.Substring(6);
            return suffix.Length > 0 && suffix.All(char.IsDigit);
        }

        internal static bool IsWithinDirectory(string path, string directory)
        {
            var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(fullPath, fullDirectory, PathComparison())) return true;
            return fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, PathComparison()) ||
                   fullPath.StartsWith(fullDirectory + Path.AltDirectorySeparatorChar, PathComparison());
        }

        internal static StringComparer PathComparer()
        {
            return Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        }

        private static StringComparison PathComparison()
        {
            return Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        }
    }

    public static class VisualLabProjectStore
    {
        private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

        private static JsonSerializerOptions CreateJsonOptions()
        {
            var options = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }

        public static async Task SaveScanReportAsync(string destinationPath, VisualLabScanResult report, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            EnsureOutsideProtectedRoots(destinationPath, report.ProtectedSourceRoots);
            await WriteJsonAtomicallyAsync(destinationPath, report, cancellationToken).ConfigureAwait(false);
        }

        public static async Task SaveSequenceAsync(string destinationPath, VisualLabSequence sequence, CancellationToken cancellationToken = default(CancellationToken))
        {
            ValidateSequence(sequence);
            EnsureOutsideProtectedRoots(destinationPath, sequence.ProtectedSourceRoots);
            sequence.UpdatedAtUtc = DateTime.UtcNow;
            await WriteJsonAtomicallyAsync(destinationPath, sequence, cancellationToken).ConfigureAwait(false);
        }

        public static async Task<VisualLabSequence> LoadSequenceAsync(string path, CancellationToken cancellationToken = default(CancellationToken))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var sequence = await JsonSerializer.DeserializeAsync<VisualLabSequence>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (sequence == null) throw new InvalidDataException("The sequence file is empty or invalid.");
            ValidateSequence(sequence);
            return sequence;
        }

        public static void ValidateSequence(VisualLabSequence sequence)
        {
            if (sequence == null) throw new ArgumentNullException(nameof(sequence));
            if (sequence.SchemaVersion != 1) throw new InvalidDataException("Unsupported Visual Lab sequence schema version: " + sequence.SchemaVersion.ToString(CultureInfo.InvariantCulture));
            if (string.IsNullOrWhiteSpace(sequence.Title)) throw new ArgumentException("A sequence title is required.", nameof(sequence));
            if (sequence.Events == null) throw new ArgumentException("The sequence event list is missing.", nameof(sequence));

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in sequence.Events)
            {
                if (item == null) throw new ArgumentException("The sequence contains a null event.", nameof(sequence));
                if (string.IsNullOrWhiteSpace(item.Id) || !ids.Add(item.Id)) throw new ArgumentException("Every timeline event needs a unique ID.", nameof(sequence));
                if (string.IsNullOrWhiteSpace(item.Name)) throw new ArgumentException("Every timeline event needs a name.", nameof(sequence));
                if (!Enum.IsDefined(typeof(VisualLabTimelineEventKind), item.Kind)) throw new ArgumentException("A timeline event has an unknown kind.", nameof(sequence));
                if (double.IsNaN(item.StartSeconds) || double.IsInfinity(item.StartSeconds) || item.StartSeconds < 0)
                    throw new ArgumentOutOfRangeException(nameof(sequence), "Event start times must be finite and zero or greater.");
                if (double.IsNaN(item.DurationSeconds) || double.IsInfinity(item.DurationSeconds) || item.DurationSeconds < 0)
                    throw new ArgumentOutOfRangeException(nameof(sequence), "Event durations must be finite and zero or greater.");
                if ((item.Kind == VisualLabTimelineEventKind.Animation ||
                     item.Kind == VisualLabTimelineEventKind.Effect ||
                     item.Kind == VisualLabTimelineEventKind.Projectile) &&
                    string.IsNullOrWhiteSpace(item.SourceAssetPath))
                    throw new ArgumentException("Animation, effect, and projectile events must reference a source asset path.", nameof(sequence));
            }
        }

        public static void EnsureOutsideProtectedRoots(string destinationPath, IEnumerable<string> protectedRoots)
        {
            if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("A destination path is required.", nameof(destinationPath));
            var destination = Path.GetFullPath(destinationPath);
            foreach (var root in protectedRoots ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(root) && VisualLabPackageScanner.IsWithinDirectory(destination, root))
                    throw new InvalidOperationException("For source preservation, save reports and sequence projects outside the imported source folders. Destination: " + destination);
            }
        }

        private static async Task WriteJsonAtomicallyAsync<T>(string destinationPath, T value, CancellationToken cancellationToken)
        {
            var destination = Path.GetFullPath(destinationPath);
            var folder = Path.GetDirectoryName(destination);
            if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("The destination must have a parent folder.", nameof(destinationPath));
            Directory.CreateDirectory(folder);
            var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                File.Move(temporary, destination, true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }
    }
}
