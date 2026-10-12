using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Juancho.VisualLab;

namespace AssetStudioGUI
{
    /// <summary>
    /// Read-only package-wide skill/effect browser. Slot assignment is evidence-based:
    /// explicit skill/passive tokens are used when present, otherwise the UI shows
    /// unassigned candidates rather than pretending to know undocumented game logic.
    /// </summary>
    public sealed class JuanchoVisualLabSkillExplorerForm : Form
    {
        private sealed class ExplorerRow
        {
            public string Name { get; set; } = "";
            public string UnityClass { get; set; } = "";
            public string Role { get; set; } = "";
            public string SkillGroup { get; set; } = "";
            public string SourcePath { get; set; } = "";
            public string SerializedFileName { get; set; } = "";
            public string Evidence { get; set; } = "";
            public int ReferenceCount { get; set; }
            public VisualLabUnityAssetItem UnityAsset { get; set; }
            public VisualLabAssetFile FileAsset { get; set; }
            public string Key { get { return UnityAsset?.Key ?? ("FILE|" + (FileAsset?.FullPath ?? "")); } }
        }

        private readonly VisualLabUnityIndexResult _index;
        private readonly VisualLabScanResult _scan;
        private readonly string[] _inputs;
        private readonly List<ExplorerRow> _allRows = new List<ExplorerRow>();
        private readonly List<ExplorerRow> _presetRows = new List<ExplorerRow>();
        private readonly BindingList<ExplorerRow> _visibleRows = new BindingList<ExplorerRow>();
        private readonly DataGridView _grid = new DataGridView();
        private readonly ComboBox _typeFilter = new ComboBox();
        private readonly TextBox _search = new TextBox();
        private readonly Label _summary = new Label();
        private readonly Label _detail = new Label();
        private readonly PictureBox _preview = new PictureBox();
        private readonly Label _previewInfo = new Label();
        private readonly ListBox _references = new ListBox();
        private readonly Button _inspectButton = new Button();
        private readonly Button _passiveButton = new Button();
        private readonly ToolStripStatusLabel _status = new ToolStripStatusLabel();
        private readonly StatusStrip _statusStrip = new StatusStrip();
        private string _activePreset = "All Effects";
        private bool _populating;

        public JuanchoVisualLabSkillExplorerForm(VisualLabUnityIndexResult index, VisualLabScanResult scan)
        {
            _index = index ?? throw new ArgumentNullException(nameof(index));
            _scan = scan;
            _inputs = (_index.InputFiles ?? new List<string>())
                .Where(File.Exists).Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            Text = "Juancho MLBB Visual Lab — Skills, Effects & Texture2D Explorer";
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(1120, 700);
            Size = new Size(1500, 900);
            Font = new Font("Segoe UI", 9F);

            BuildRows();
            BuildUi();
            SetPresetAvailability();
            ApplyPreset("All Effects");

            FormClosed += (s, e) =>
            {
                if (_preview.Image != null)
                {
                    var old = _preview.Image;
                    _preview.Image = null;
                    old.Dispose();
                }
            };
        }

        private void BuildUi()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var header = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(0, 0, 0, 7) };
            header.Controls.Add(new Label
            {
                Text = "Explore parsed assets and package files. Skill grouping uses names, source paths and resolved references; it does not invent runtime mappings.",
                AutoSize = true,
                MaximumSize = new Size(1320, 0),
                Padding = new Padding(0, 5, 10, 4)
            });
            AddPresetButton(header, "All Effects", "All Effects");
            AddPresetButton(header, "Skill 1", "Skill 1");
            AddPresetButton(header, "Skill 2", "Skill 2");
            AddPresetButton(header, "Skill 3", "Skill 3");
            _passiveButton.Text = "Passive";
            _passiveButton.AutoSize = true;
            _passiveButton.Click += (s, e) => ApplyPreset("Passive");
            header.Controls.Add(_passiveButton);
            AddPresetButton(header, "Texture2D / Images", "Textures");
            AddPresetButton(header, "All Package Assets", "All");
            header.Controls.Add(new Label { Text = "Type:", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
            _typeFilter.DropDownStyle = ComboBoxStyle.DropDownList;
            _typeFilter.Width = 175;
            _typeFilter.Items.AddRange(new object[]
            {
                "All asset types", "Effects & particles", "Texture2D / images",
                "AnimationClip", "Materials / shaders", "Model / rig",
                "Loose package files"
            });
            _typeFilter.SelectedIndex = 0;
            _typeFilter.SelectedIndexChanged += (s, e) => RefreshRows();
            header.Controls.Add(_typeFilter);
            header.Controls.Add(new Label { Text = "Search:", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
            _search.Width = 210;
            _search.TextChanged += (s, e) => RefreshRows();
            header.Controls.Add(_search);

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 820, FixedPanel = FixedPanel.Panel2 };
            split.Panel1MinSize = 420;
            split.Panel2MinSize = 300;

            _grid.Dock = DockStyle.Fill;
            _grid.ReadOnly = true;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AutoGenerateColumns = false;
            _grid.MultiSelect = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.RowHeadersVisible = false;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells;
            _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Name", HeaderText = "Asset / file", Width = 220, MinimumWidth = 140 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "UnityClass", HeaderText = "Type", Width = 135, MinimumWidth = 95 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "SkillGroup", HeaderText = "Slot hint", Width = 95, MinimumWidth = 75 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Role", HeaderText = "Role", Width = 145, MinimumWidth = 100 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "ReferenceCount", HeaderText = "References", Width = 85 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "SourcePath", HeaderText = "Source", Width = 300, MinimumWidth = 180 });
            _grid.DataSource = _visibleRows;
            _grid.SelectionChanged += (s, e) => ShowSelectedRow();
            _grid.CellDoubleClick += (s, e) => InspectSelected();
            split.Panel1.Controls.Add(_grid);

            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(6, 0, 0, 0) };
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _detail.AutoSize = false;
            _detail.Dock = DockStyle.Fill;
            _detail.BorderStyle = BorderStyle.FixedSingle;
            _detail.Padding = new Padding(6);
            _detail.Text = "Select an asset. Use Inspect Selected Asset to open its Unity bundle and inspect Texture2D, material, renderer and reference data.";
            right.Controls.Add(_detail, 0, 0);
            _preview.Dock = DockStyle.Fill;
            _preview.SizeMode = PictureBoxSizeMode.Zoom;
            _preview.BackColor = Color.FromArgb(36, 38, 44);
            right.Controls.Add(_preview, 0, 1);
            _previewInfo.AutoSize = true;
            _previewInfo.Dock = DockStyle.Fill;
            _previewInfo.Padding = new Padding(2, 4, 2, 4);
            _previewInfo.Text = "Texture2D previews and raw image input appear here when selected.";
            right.Controls.Add(_previewInfo, 0, 2);
            _inspectButton.Text = "Inspect Selected Asset / Texture2D…";
            _inspectButton.AutoSize = true;
            _inspectButton.Dock = DockStyle.Fill;
            _inspectButton.Click += (s, e) => InspectSelected();
            right.Controls.Add(_inspectButton, 0, 3);
            right.Controls.Add(new Label { Text = "Resolved package references", AutoSize = true, Padding = new Padding(0, 6, 0, 2) }, 0, 4);
            _references.Dock = DockStyle.Fill;
            _references.HorizontalScrollbar = true;
            right.Controls.Add(_references, 0, 5);
            right.Controls.Add(new Label
            {
                Text = "Note: effects may require Unity runtime scripts, shaders, particle simulation, Animator state, and timing events. This view inventories evidence; it cannot guarantee exact in-game behavior from serialized assets alone.",
                AutoSize = true,
                MaximumSize = new Size(420, 0),
                Padding = new Padding(0, 6, 0, 0)
            }, 0, 6);
            split.Panel2.Controls.Add(right);

            _summary.AutoSize = true;
            _summary.Padding = new Padding(0, 6, 0, 0);
            _status.Text = "Read-only asset inspection; source files are not modified.";
            _statusStrip.Items.Add(_status);
            root.Controls.Add(header, 0, 0);
            root.Controls.Add(split, 0, 1);
            root.Controls.Add(_summary, 0, 2);
            Controls.Add(root);
            Controls.Add(_statusStrip);
        }

        private void AddPresetButton(FlowLayoutPanel panel, string label, string preset)
        {
            var button = new Button { Text = label, AutoSize = true };
            button.Click += (s, e) => ApplyPreset(preset);
            panel.Controls.Add(button);
        }

        private void BuildRows()
        {
            var assets = _index.Assets ?? new List<VisualLabUnityAssetItem>();
            var dependencies = _index.Dependencies ?? new List<VisualLabUnityDependency>();
            foreach (var asset in assets)
            {
                if (asset == null) continue;
                var refs = dependencies.Count(dep =>
                    string.Equals(dep.SourceAssetKey, asset.Key, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(dep.TargetAssetKey, asset.Key, StringComparison.OrdinalIgnoreCase));
                var evidence = VisualLabSkillAssetClassifier.ClassifySlot(
                    (asset.Name ?? "") + " " + (asset.SerializedFileName ?? "") + " " + (asset.SourceBundlePath ?? ""));
                _allRows.Add(new ExplorerRow
                {
                    Name = string.IsNullOrWhiteSpace(asset.Name) ? asset.ClassName + "_" + asset.PathId : asset.Name,
                    UnityClass = asset.ClassName ?? "",
                    Role = asset.Role ?? "Other",
                    SkillGroup = evidence.Length == 0 ? "Unassigned" : evidence,
                    SourcePath = asset.SourceBundlePath ?? "",
                    SerializedFileName = asset.SerializedFileName ?? "",
                    Evidence = asset.PreviewReadiness ?? "",
                    ReferenceCount = refs,
                    UnityAsset = asset
                });
            }

            foreach (var file in _scan?.Assets ?? new List<VisualLabAssetFile>())
            {
                if (file == null) continue;
                bool isTexture = VisualLabSkillAssetClassifier.IsTextureExtension(file.Extension);
                bool isSkillEffect = VisualLabSkillAssetClassifier.IsEffectOrSkillPath(file.RelativePath + " " + file.FullPath);
                if (!isTexture && !isSkillEffect) continue;
                _allRows.Add(new ExplorerRow
                {
                    Name = Path.GetFileName(file.FullPath),
                    UnityClass = file.Category ?? "Package file",
                    Role = isTexture ? "Texture file" : "Skill / effect file hint",
                    SkillGroup = FileSkillGroup(file),
                    SourcePath = file.RelativePath ?? file.FullPath,
                    SerializedFileName = Path.GetFileName(file.FullPath),
                    Evidence = file.Category ?? "",
                    FileAsset = file
                });
            }
        }

        private void SetPresetAvailability()
        {
            bool hasPassive = _allRows.Any(row => SlotOf(row) == "Passive");
            _passiveButton.Visible = hasPassive;
            var slotCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var slot in new[] { "Skill 1", "Skill 2", "Skill 3", "Passive" })
                slotCounts[slot] = _allRows.Count(row => SlotOf(row) == slot);
            _passiveButton.Text = "Passive (" + slotCounts["Passive"] + ")";
        }

        private static string SlotOf(ExplorerRow row)
        {
            if (row == null) return "";
            string direct = VisualLabSkillAssetClassifier.ClassifySlot(
                (row.Name ?? "") + " " + (row.SerializedFileName ?? "") + " " + (row.SourcePath ?? ""));
            return direct;
        }

        private void ApplyPreset(string preset)
        {
            _activePreset = preset ?? "All";
            _presetRows.Clear();
            var dependencyEdges = _index.Dependencies ?? new List<VisualLabUnityDependency>();
            if (_activePreset == "All")
            {
                _presetRows.AddRange(_allRows);
                _summary.Text = "All package entries: " + _presetRows.Count + ". Physical scan files and parsed Unity objects are shown separately.";
            }
            else if (_activePreset == "Textures")
            {
                _presetRows.AddRange(_allRows.Where(row =>
                    (row.UnityAsset != null && (string.Equals(row.UnityClass, "Texture2D", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(row.UnityClass, "Sprite", StringComparison.OrdinalIgnoreCase))) ||
                    (row.FileAsset != null && VisualLabSkillAssetClassifier.IsTextureExtension(row.FileAsset.Extension))));
                _summary.Text = "Textures: " + _presetRows.Count + ". Unity Texture2D entries can be decoded in the selected bundle inspector; loose image files are previewed here.";
            }
            else if (_activePreset == "All Effects")
            {
                _presetRows.AddRange(_allRows.Where(IsEffectCandidate));
                _summary.Text = "Effect candidates: " + _presetRows.Count + ". The role is confirmed only where actual serialized component/reference data supports it; names and paths are hints.";
            }
            else
            {
                var seeds = _allRows.Where(row => SlotOf(row) == _activePreset).ToArray();
                if (seeds.Length == 0)
                {
                    _presetRows.AddRange(_allRows.Where(IsEffectCandidate));
                    _summary.Text = _activePreset + ": no explicit slot match was found in object/file names or paths. Showing unassigned skill/effect candidates (" + _presetRows.Count + "); inspect bundle references to determine which skill uses them.";
                }
                else
                {
                    var includedKeys = new HashSet<string>(seeds.Where(x => x.UnityAsset != null).Select(x => x.UnityAsset.Key), StringComparer.OrdinalIgnoreCase);
                    var matchingPaths = new HashSet<string>(seeds.Where(x => x.UnityAsset != null || x.FileAsset != null).Select(x =>
                        SafeFullPath(x.UnityAsset?.SourceBundlePath ?? x.FileAsset?.FullPath ?? x.SourcePath)), StringComparer.OrdinalIgnoreCase);
                    var frontier = new HashSet<string>(includedKeys, StringComparer.OrdinalIgnoreCase);
                    for (int depth = 0; depth < 2; depth++)
                    {
                        var next = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var edge in dependencyEdges)
                        {
                            if (!string.IsNullOrWhiteSpace(edge.SourceAssetKey) && frontier.Contains(edge.SourceAssetKey) &&
                                !string.IsNullOrWhiteSpace(edge.TargetAssetKey) && includedKeys.Add(edge.TargetAssetKey))
                                next.Add(edge.TargetAssetKey);
                            if (!string.IsNullOrWhiteSpace(edge.TargetAssetKey) && frontier.Contains(edge.TargetAssetKey) &&
                                !string.IsNullOrWhiteSpace(edge.SourceAssetKey) && includedKeys.Add(edge.SourceAssetKey))
                                next.Add(edge.SourceAssetKey);
                        }
                        frontier = next;
                        if (frontier.Count == 0) break;
                    }

                    _presetRows.AddRange(_allRows.Where(row =>
                        (row.UnityAsset != null && (includedKeys.Contains(row.UnityAsset.Key) ||
                         (matchingPaths.Contains(SafeFullPath(row.UnityAsset.SourceBundlePath)) && IsRelevantSupportingAsset(row)))) ||
                        (row.FileAsset != null && (matchingPaths.Contains(SafeFullPath(row.FileAsset.FullPath)) ||
                         SlotOf(row) == _activePreset))));
                    _summary.Text = _activePreset + ": " + seeds.Length + " explicit seed(s), " +
                        _presetRows.Count + " related rows. Related assets come from source-bundle grouping and resolved references; missing external dependencies may not be included.";
                }
            }
            RefreshRows();
        }

        private static bool IsRelevantSupportingAsset(ExplorerRow row)
        {
            var type = row?.UnityClass ?? "";
            return string.Equals(type, "Texture2D", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "Sprite", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "Material", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "Shader", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "AnimationClip", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "Mesh", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "GameObject", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "Transform", StringComparison.OrdinalIgnoreCase) ||
                   type.IndexOf("Renderer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   type.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   type.IndexOf("Audio", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   type.IndexOf("Animator", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   string.Equals(row?.Role, "Skill / effect data (heuristic)", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsEffectCandidate(ExplorerRow row)
        {
            if (row == null) return false;
            var type = row.UnityClass ?? "";
            return string.Equals(row.Role, "Visual effects", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(row.Role, "Skill / effect data (heuristic)", StringComparison.OrdinalIgnoreCase) ||
                   VisualLabSkillAssetClassifier.IsEffectOrSkillPath(row.Name + " " + row.SourcePath + " " + row.SerializedFileName) ||
                   type.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   type.IndexOf("TrailRenderer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   type.IndexOf("LineRenderer", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void RefreshRows()
        {
            if (_populating) return;
            _populating = true;
            try
            {
                var query = (_search.Text ?? "").Trim();
                var typeFilter = _typeFilter.SelectedItem as string ?? "All asset types";
                IEnumerable<ExplorerRow> rows = _presetRows;
                if (!string.IsNullOrWhiteSpace(query))
                    rows = rows.Where(row => (row.Name + " " + row.UnityClass + " " + row.Role + " " + row.SkillGroup + " " + row.SourcePath + " " + row.Evidence)
                        .IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
                switch (typeFilter)
                {
                    case "Effects & particles": rows = rows.Where(IsEffectCandidate); break;
                    case "Texture2D / images": rows = rows.Where(row =>
                        string.Equals(row.UnityClass, "Texture2D", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(row.UnityClass, "Sprite", StringComparison.OrdinalIgnoreCase) ||
                        (row.FileAsset != null && VisualLabSkillAssetClassifier.IsTextureExtension(row.FileAsset.Extension))); break;
                    case "AnimationClip": rows = rows.Where(row => string.Equals(row.UnityClass, "AnimationClip", StringComparison.OrdinalIgnoreCase)); break;
                    case "Materials / shaders": rows = rows.Where(row => string.Equals(row.UnityClass, "Material", StringComparison.OrdinalIgnoreCase) || string.Equals(row.UnityClass, "Shader", StringComparison.OrdinalIgnoreCase)); break;
                    case "Model / rig": rows = rows.Where(row =>
                        new[] { "GameObject", "Transform", "Mesh", "SkinnedMeshRenderer", "MeshRenderer", "MeshFilter", "Animator" }
                            .Contains(row.UnityClass, StringComparer.OrdinalIgnoreCase)); break;
                    case "Loose package files": rows = rows.Where(row => row.FileAsset != null); break;
                }
                _visibleRows.Clear();
                foreach (var row in rows.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Take(10000))
                    _visibleRows.Add(row);
                if (_visibleRows.Count > 0 && _grid.Rows.Count > 0)
                {
                    _grid.ClearSelection();
                    _grid.Rows[0].Selected = true;
                    _grid.CurrentCell = _grid.Rows[0].Cells[0];
                    ShowSelectedRow();
                }
                else
                {
                    _detail.Text = "No assets matched the current filters.";
                    _references.Items.Clear();
                    SetPreview(null);
                }
                _status.Text = _visibleRows.Count + " displayed · " + _presetRows.Count + " before filters · 10,000 row display cap";
            }
            finally { _populating = false; }
        }

        private void ShowSelectedRow()
        {
            var row = _grid.CurrentRow?.DataBoundItem as ExplorerRow;
            _references.BeginUpdate();
            try
            {
                _references.Items.Clear();
                if (row?.UnityAsset != null)
                {
                    _detail.Text =
                        "Name: " + row.Name + Environment.NewLine +
                        "Unity type: " + row.UnityClass + Environment.NewLine +
                        "Suggested role: " + row.Role + Environment.NewLine +
                        "Slot hint: " + row.SkillGroup + Environment.NewLine +
                        "Source bundle: " + row.UnityAsset.SourceBundlePath + Environment.NewLine +
                        "Serialized file: " + row.SerializedFileName + Environment.NewLine +
                        "Path ID: " + row.UnityAsset.PathId + Environment.NewLine +
                        "References: " + row.ReferenceCount + Environment.NewLine +
                        "Readiness: " + row.Evidence;
                    foreach (var edge in (_index.Dependencies ?? new List<VisualLabUnityDependency>())
                        .Where(dep => string.Equals(dep.SourceAssetKey, row.UnityAsset.Key, StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(dep.TargetAssetKey, row.UnityAsset.Key, StringComparison.OrdinalIgnoreCase))
                        .Take(250))
                    {
                        string relationship = string.Equals(edge.SourceAssetKey, row.UnityAsset.Key, StringComparison.OrdinalIgnoreCase)
                            ? "→ " + edge.TargetClassName + " " + edge.TargetName + " [" + edge.TargetFileName + ":" + edge.TargetPathId + "]"
                            : "← " + edge.SourceAssetName + " (" + edge.MemberPath + ")";
                        _references.Items.Add(relationship);
                    }
                    if (!string.Equals(row.UnityClass, "Texture2D", StringComparison.OrdinalIgnoreCase))
                        _previewInfoText = "Select Inspect Selected Asset to open this object in its Unity bundle.";
                    else
                        _previewInfoText = "Texture2D is serialized in this Unity asset file. Click Inspect Selected Asset / Texture2D to decode and preview it.";
                    SetPreview(null);
                }
                else if (row?.FileAsset != null)
                {
                    _detail.Text =
                        "Package file: " + row.FileAsset.FullPath + Environment.NewLine +
                        "Category: " + row.FileAsset.Category + Environment.NewLine +
                        "Extension: " + row.FileAsset.Extension + Environment.NewLine +
                        "Bytes: " + row.FileAsset.LengthBytes.ToString("N0") + Environment.NewLine +
                        "SHA-256: " + row.FileAsset.Sha256 + Environment.NewLine +
                        "Slot hint: " + row.SkillGroup + Environment.NewLine +
                        "Evidence: " + row.Evidence;
                    _references.Items.Add("Physical file inventory only; contained Unity object references are shown after bundle analysis.");
                    if (VisualLabSkillAssetClassifier.IsTextureExtension(row.FileAsset.Extension))
                        PreviewLooseImage(row.FileAsset);
                    else
                        SetPreview(null);
                }
                else
                {
                    _detail.Text = "Select an asset.";
                    SetPreview(null);
                }
            }
            finally { _references.EndUpdate(); }
        }

        private string _previewInfoText = "";

        private void PreviewLooseImage(VisualLabAssetFile file)
        {
            SetPreview(null);
            try
            {
                using (var image = Image.FromFile(file.FullPath))
                    SetPreview(new Bitmap(image));
                _previewInfoText = "Loose image: " + Path.GetFileName(file.FullPath) + " · " + _preview.Image.Width + "×" + _preview.Image.Height;
                _previewInfo.Text = _previewInfoText;
            }
            catch (Exception ex)
            {
                _previewInfoText = "This file is indexed as a texture candidate, but this Windows image decoder cannot display its format directly: " + ex.Message;
                _previewInfo.Text = _previewInfoText;
            }
        }

        private void SetPreview(Image image)
        {
            if (_preview.Image != null)
            {
                var old = _preview.Image;
                _preview.Image = null;
                old.Dispose();
            }
            if (image != null) _preview.Image = image;
            _preview.Tag = _previewInfoText;
            _preview.AccessibleDescription = _previewInfoText;
            _previewInfo.Text = _previewInfoText;
        }

        private void InspectSelected()
        {
            var row = _grid.CurrentRow?.DataBoundItem as ExplorerRow;
            if (row?.UnityAsset != null)
            {
                var bundlePaths = _inputs.Where(IsUnityBundlePath).ToArray();
                using (var inspector = new JuanchoVisualLabBundleInspectorForm(
                    _inputs, bundlePaths, row.UnityAsset.SourceBundlePath, row.UnityAsset.PathId, row.UnityAsset.ClassName))
                    inspector.ShowDialog(this);
                return;
            }
            if (row?.FileAsset != null && IsUnityBundlePath(row.FileAsset.FullPath))
            {
                var bundlePaths = _inputs.Where(IsUnityBundlePath).ToArray();
                using (var inspector = new JuanchoVisualLabBundleInspectorForm(
                    _inputs, bundlePaths, row.FileAsset.FullPath))
                    inspector.ShowDialog(this);
                return;
            }
            if (row?.FileAsset != null && VisualLabSkillAssetClassifier.IsTextureExtension(row.FileAsset.Extension))
            {
                PreviewLooseImage(row.FileAsset);
                MessageBox.Show(this, _previewInfoText, "Texture preview", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            MessageBox.Show(this, "Select a parsed Unity object or a Unity bundle/texture file first.", "Skill & Texture2D Explorer", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static string FileSkillGroup(VisualLabAssetFile file)
        {
            var slot = VisualLabSkillAssetClassifier.ClassifySlot((file?.RelativePath ?? "") + " " + (file?.FullPath ?? ""));
            return string.IsNullOrWhiteSpace(slot) ? "Unassigned" : slot;
        }

        private static bool IsUnityBundlePath(string path)
        {
            var extension = Path.GetExtension(path ?? "");
            if (new[] { ".unity3d", ".bundle", ".assets", ".asset", ".ab", ".ress", ".resource" }
                .Contains(extension, StringComparer.OrdinalIgnoreCase))
                return true;
            if (!extension.StartsWith(".split", StringComparison.OrdinalIgnoreCase)) return false;
            var suffix = extension.Substring(6);
            return suffix.Length > 0 && suffix.All(char.IsDigit);
        }

        private static string SafeFullPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            try { return Path.GetFullPath(value); } catch { return value; }
        }
    }
}
