using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Juancho.VisualLab;

internal static class Program
{
    private static async Task<int> Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "JuanchoVisualLabTests-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var output = Path.Combine(root, "workspace");
        Directory.CreateDirectory(Path.Combine(source, "hero", "effects"));
        Directory.CreateDirectory(output);
        try
        {
            var bundleA = Path.Combine(source, "hero", "hero_skin.unity3d");
            var bundleB = Path.Combine(source, "hero", "effects", "skill_effect.unity3d");
            var texture = Path.Combine(source, "hero", "effects", "glow.png");
            var bytesA = Encoding.UTF8.GetBytes("sample unity bundle A");
            var bytesB = Encoding.UTF8.GetBytes("sample unity bundle B");
            var bytesTexture = Encoding.UTF8.GetBytes("sample texture bytes");
            await File.WriteAllBytesAsync(bundleA, bytesA);
            await File.WriteAllBytesAsync(bundleB, bytesB);
            await File.WriteAllBytesAsync(texture, bytesTexture);

            var scan = await VisualLabPackageScanner.ScanAsync(new[] { source });
            Assert(scan.Assets.Count == 3, "Scanner should find both Unity bundle candidates and the texture.");
            Assert(scan.Assets.Any(x => x.Category == "Unity bundle candidate"), "Bundle candidates should be categorized.");
            Assert(scan.Assets.Any(x => x.Category == "Texture candidate"), "Texture candidates should be categorized.");
            Assert(scan.Assets.Any(x => x.Category.Contains("skill/effect path hint", StringComparison.OrdinalIgnoreCase)),
                "Skill/effect bundles should be highlighted in the physical package scan.");
            Assert(VisualLabSkillAssetClassifier.ClassifySlot("hero_zeo_skill_01_effect.unity3d") == "Skill 1",
                "Explicit Skill 1 naming should map to the Skill 1 filter.");
            Assert(VisualLabSkillAssetClassifier.ClassifySlot("hero_zeo_skill_2_trail") == "Skill 2",
                "Explicit Skill 2 naming should map to the Skill 2 filter.");
            Assert(VisualLabSkillAssetClassifier.ClassifySlot("hero_zeo_skill3_projectile") == "Skill 3",
                "Compact Skill 3 naming should map to the Skill 3 filter.");
            Assert(VisualLabSkillAssetClassifier.ClassifySlot("hero_zeo_passive_fx") == "Passive",
                "Passive naming should be recognized when the hero package includes passive assets.");
            Assert(VisualLabSkillAssetClassifier.IsTextureExtension(".tga") &&
                   VisualLabSkillAssetClassifier.IsTextureExtension(".png"),
                "Unity/raw texture input extensions should be supported.");
            Assert(scan.ProtectedSourceRoots.Contains(Path.GetFullPath(source)), "The selected source directory must be marked protected.");
            Assert(scan.Assets.All(x => x.Sha256.Length == 64), "Every indexed file should have a SHA-256 hash.");
            Assert(scan.Assets.Any(x => x.SuggestedFolderGroup.Contains("effects")), "Folder grouping metadata should be retained.");

            var originalA = await File.ReadAllBytesAsync(bundleA);
            var originalWriteTime = File.GetLastWriteTimeUtc(bundleA);
            await VisualLabProjectStore.SaveScanReportAsync(Path.Combine(output, "scan-report.json"), scan);
            Assert(originalA.SequenceEqual(await File.ReadAllBytesAsync(bundleA)), "Scanning/reporting must not alter source bytes.");
            Assert(originalWriteTime == File.GetLastWriteTimeUtc(bundleA), "Scanning/reporting must not alter source timestamps.");

            var blocked = false;
            try
            {
                await VisualLabProjectStore.SaveScanReportAsync(Path.Combine(source, "scan-report.json"), scan);
            }
            catch (InvalidOperationException) { blocked = true; }
            Assert(blocked, "Saving a report inside the imported source folder must be blocked.");

            var sequence = new VisualLabSequence
            {
                Title = "Skill 1 smoke test",
                ProtectedSourceRoots = scan.ProtectedSourceRoots,
                Events =
                {
                    new VisualLabTimelineEvent
                    {
                        Name = "Cast animation",
                        Kind = VisualLabTimelineEventKind.Animation,
                        SourceAssetPath = bundleA,
                        StartSeconds = 0,
                        DurationSeconds = 0.8
                    },
                    new VisualLabTimelineEvent
                    {
                        Name = "Impact",
                        Kind = VisualLabTimelineEventKind.Effect,
                        SourceAssetPath = bundleB,
                        StartSeconds = 0.6,
                        DurationSeconds = 1.1
                    }
                }
            };
            var sequencePath = Path.Combine(output, "skill-one.juancho-visual-lab.json");
            await VisualLabProjectStore.SaveSequenceAsync(sequencePath, sequence);
            var reopened = await VisualLabProjectStore.LoadSequenceAsync(sequencePath);
            Assert(reopened.Events.Count == 2, "Saved timeline should reopen with both events.");
            Assert(reopened.Events[1].Kind == VisualLabTimelineEventKind.Effect, "Event kinds must round-trip.");
            Assert(Math.Abs(reopened.Events[1].StartSeconds - 0.6) < 0.00001, "Timeline timing must round-trip.");

            var invalidRejected = false;
            try
            {
                VisualLabProjectStore.ValidateSequence(new VisualLabSequence
                {
                    Title = "Invalid",
                    Events = { new VisualLabTimelineEvent { Name = "Bad", Kind = VisualLabTimelineEventKind.Marker, StartSeconds = -1 } }
                });
            }
            catch (ArgumentOutOfRangeException) { invalidRejected = true; }
            Assert(invalidRejected, "Negative timeline times must be rejected.");

            Console.WriteLine("PASS: package scan, skill/effect categorization, skill-slot classifier, texture extension recognition, SHA-256 inventory, source preservation, export guard, sequence save/reload, and timing validation.");
            return 0;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAILED: " + message);
    }
}
