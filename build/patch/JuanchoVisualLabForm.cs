using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Juancho.VisualLab;

namespace AssetStudioGUI
{
    /// <summary>
    /// Phase-one Visual Lab workspace: read-only package inventory and a manually authored skill timeline.
    /// Rendering/animation playback is deliberately not advertised until a runtime adapter is connected.
    /// </summary>
    public sealed class JuanchoVisualLabForm : Form
    {
        private readonly DataGridView _assetGrid = new DataGridView();
        private readonly DataGridView _timelineGrid = new DataGridView();
        private readonly DataGridView _unityObjectGrid = new DataGridView();
        private readonly DataGridView _dependencyGrid = new DataGridView();
        private readonly BindingList<VisualLabAssetFile> _assetItems = new BindingList<VisualLabAssetFile>();
        private readonly BindingList<VisualLabTimelineEvent> _timelineItems = new BindingList<VisualLabTimelineEvent>();
        private readonly BindingList<VisualLabUnityAssetItem> _unityObjectItems = new BindingList<VisualLabUnityAssetItem>();
        private readonly BindingList<VisualLabUnityDependency> _dependencyItems = new BindingList<VisualLabUnityDependency>();
        private readonly Label _scanStatus = new Label();
        private readonly Label _objectStatus = new Label();
        private readonly Label _objectCountStatus = new Label();
        private readonly ComboBox _roleFilter = new ComboBox();
        private readonly Label _runtimeStatus = new Label();
        private readonly TextBox _sequenceTitle = new TextBox();
        private readonly ToolStripProgressBar _progressBar = new ToolStripProgressBar();
        private readonly ToolStripStatusLabel _statusLabel = new ToolStripStatusLabel();
        private readonly ToolStripStatusLabel _detailLabel = new ToolStripStatusLabel();
        private readonly StatusStrip _statusStrip = new StatusStrip();
        private readonly Button _scanFolderButton = new Button();
        private readonly Button _scanFilesButton = new Button();
        private readonly Button _exportReportButton = new Button();
        private readonly Button _cancelButton = new Button();
        private readonly Button _analyzeObjectsButton = new Button();
        private readonly Button _previewMeshButton = new Button();
        private CancellationTokenSource _scanCancellation;
        private VisualLabScanResult _lastScan;
        private VisualLabUnityIndexResult _lastUnityIndex;
        private string _selectedAssetPath = "";
        private readonly List<string> _protectedSourceRoots = new List<string>();

        public JuanchoVisualLabForm()
        {
            Text = "Juancho MLBB Visual Lab — Package & Timeline Foundation";
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(1000, 640);
            Size = new Size(1320, 820);
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            var tabs = new TabControl { Dock = DockStyle.Fill };
            var packageTab = new TabPage("Package Explorer");
            var objectsTab = new TabPage("Serialized Objects");
            var timelineTab = new TabPage("Skill Timeline");
            var runtimeTab = new TabPage("Runtime Status");
            tabs.TabPages.Add(packageTab);
            tabs.TabPages.Add(objectsTab);
            tabs.TabPages.Add(timelineTab);
            tabs.TabPages.Add(runtimeTab);

            packageTab.Controls.Add(BuildPackageTab());
            objectsTab.Controls.Add(BuildSerializedObjectsTab());
            timelineTab.Controls.Add(BuildTimelineTab());
            runtimeTab.Controls.Add(BuildRuntimeTab());

            _statusStrip.Items.Add(_statusLabel);
            _statusStrip.Items.Add(new ToolStripStatusLabel { Spring = true });
            _statusStrip.Items.Add(_progressBar);
            _statusStrip.Items.Add(_detailLabel);
            _statusLabel.Text = "Ready. Source files are read-only.";
            _progressBar.Visible = false;
            _progressBar.Minimum = 0;
            _progressBar.Maximum = 100;
            _detailLabel.Text = "Visual Lab foundation";
            Controls.Add(tabs);
            Controls.Add(_statusStrip);
        }

        private Control BuildPackageTab()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(0, 0, 0, 6) };
            _scanFolderButton.Text = "Scan Package Folder";
            _scanFolderButton.AutoSize = true;
            _scanFolderButton.Click += async (s, e) => await ChooseFolderAndScanAsync();
            _scanFilesButton.Text = "Import Multiple Bundles";
            _scanFilesButton.AutoSize = true;
            _scanFilesButton.Click += async (s, e) => await ChooseFilesAndScanAsync();
            _exportReportButton.Text = "Export Scan Report";
            _exportReportButton.AutoSize = true;
            _exportReportButton.Enabled = false;
            _exportReportButton.Click += async (s, e) => await ExportReportAsync();
            _analyzeObjectsButton.Text = "Analyze Unity Bundle Contents";
            _analyzeObjectsButton.AutoSize = true;
            _analyzeObjectsButton.Enabled = false;
            _analyzeObjectsButton.Click += async (s, e) => await AnalyzeUnityBundlesAsync();
            _cancelButton.Text = "Cancel Scan";
            _cancelButton.AutoSize = true;
            _cancelButton.Enabled = false;
            _cancelButton.Click += (s, e) => _scanCancellation?.Cancel();
            var useSelectedButton = new Button { Text = "Use Selected Asset in Timeline", AutoSize = true };
            useSelectedButton.Click += (s, e) =>
            {
                var selected = GetSelectedAsset();
                if (selected == null)
                {
                    MessageBox.Show(this, "Select an asset in the package table first.", "Juancho MLBB Visual Lab", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                _selectedAssetPath = selected.FullPath;
                _statusLabel.Text = "Selected timeline source: " + selected.RelativePath;
            };
            buttons.Controls.Add(_scanFolderButton);
            buttons.Controls.Add(_scanFilesButton);
            buttons.Controls.Add(_exportReportButton);
            buttons.Controls.Add(_analyzeObjectsButton);
            buttons.Controls.Add(_cancelButton);
            buttons.Controls.Add(useSelectedButton);

            ConfigureAssetGrid();
            _assetGrid.DataSource = _assetItems;
            _assetGrid.SelectionChanged += (s, e) =>
            {
                var selected = GetSelectedAsset();
                if (selected != null)
                {
                    _selectedAssetPath = selected.FullPath;
                    _detailLabel.Text = selected.Category + " · " + FormatBytes(selected.LengthBytes);
                }
            };

            _scanStatus.Text = "No package scanned yet. Import a folder or select multiple bundle files. SHA-256 inventory and duplicate detection run without changing the source files.";
            _scanStatus.AutoSize = true;
            _scanStatus.Padding = new Padding(0, 8, 0, 0);
            root.Controls.Add(buttons, 0, 0);
            root.Controls.Add(_assetGrid, 0, 1);
            root.Controls.Add(_scanStatus, 0, 2);
            return root;
        }

        private void ConfigureAssetGrid()
        {
            _assetGrid.Dock = DockStyle.Fill;
            _assetGrid.ReadOnly = true;
            _assetGrid.AllowUserToAddRows = false;
            _assetGrid.AllowUserToDeleteRows = false;
            _assetGrid.AllowUserToOrderColumns = true;
            _assetGrid.MultiSelect = false;
            _assetGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _assetGrid.AutoGenerateColumns = false;
            _assetGrid.RowHeadersVisible = false;
            _assetGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells;
            _assetGrid.Columns.Clear();
            _assetGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "RelativePath", HeaderText = "Source path", Width = 280, MinimumWidth = 180 });
            _assetGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Category", HeaderText = "Detected category", Width = 180, MinimumWidth = 130 });
            _assetGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "SuggestedFolderGroup", HeaderText = "Folder group (heuristic)", Width = 160, MinimumWidth = 120 });
            _assetGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "LengthBytes", HeaderText = "Bytes", Width = 95 });
            _assetGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Sha256", HeaderText = "SHA-256", Width = 260, MinimumWidth = 200 });
            _assetGrid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = "IsDuplicateContent", HeaderText = "Duplicate", Width = 75 });
        }

        private Control BuildSerializedObjectsTab()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _objectStatus.AutoSize = true;
            _objectStatus.MaximumSize = new Size(1240, 0);
            _objectStatus.Padding = new Padding(0, 0, 0, 8);
            _objectStatus.Text = "Scan a package and choose Analyze Unity Bundle Contents to enumerate serialized objects and cross-bundle references.";

            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(0, 0, 0, 6) };
            toolbar.Controls.Add(new Label { Text = "Asset role:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            _roleFilter.DropDownStyle = ComboBoxStyle.DropDownList;
            _roleFilter.Width = 250;
            _roleFilter.Items.Add("All roles");
            _roleFilter.SelectedIndex = 0;
            _roleFilter.SelectedIndexChanged += RoleFilter_SelectedIndexChanged;
            toolbar.Controls.Add(_roleFilter);
            _previewMeshButton.Text = "Preview Selected Model / Mesh (static)";
            _previewMeshButton.AutoSize = true;
            _previewMeshButton.Enabled = false;
            _previewMeshButton.Click += async (s, e) => await PreviewSelectedMeshAsync();
            toolbar.Controls.Add(_previewMeshButton);
            _objectCountStatus.AutoSize = true;
            _objectCountStatus.Padding = new Padding(10, 6, 0, 0);
            _objectCountStatus.Text = "No parsed objects";
            toolbar.Controls.Add(_objectCountStatus);

            ConfigureUnityObjectGrid();
            ConfigureDependencyGrid();
            _unityObjectGrid.DataSource = _unityObjectItems;
            _dependencyGrid.DataSource = _dependencyItems;
            _unityObjectGrid.SelectionChanged += (s, e) => { RefreshDependenciesForSelectedObject(); UpdateMeshPreviewButton(); };

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 330,
                FixedPanel = FixedPanel.None
            };
            split.Panel1.Controls.Add(_unityObjectGrid);
            split.Panel2.Controls.Add(_dependencyGrid);
            split.Panel1MinSize = 140;
            split.Panel2MinSize = 120;

            root.Controls.Add(_objectStatus, 0, 0);
            root.Controls.Add(toolbar, 0, 1);
            root.Controls.Add(split, 0, 2);
            return root;
        }

        private void ConfigureUnityObjectGrid()
        {
            _unityObjectGrid.Dock = DockStyle.Fill;
            _unityObjectGrid.ReadOnly = true;
            _unityObjectGrid.AllowUserToAddRows = false;
            _unityObjectGrid.AllowUserToDeleteRows = false;
            _unityObjectGrid.AllowUserToOrderColumns = true;
            _unityObjectGrid.MultiSelect = false;
            _unityObjectGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _unityObjectGrid.AutoGenerateColumns = false;
            _unityObjectGrid.RowHeadersVisible = false;
            _unityObjectGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells;
            _unityObjectGrid.Columns.Clear();
            _unityObjectGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Name", HeaderText = "Object name", Width = 220, MinimumWidth = 140 });
            _unityObjectGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "ClassName", HeaderText = "Unity class", Width = 150, MinimumWidth = 110 });
            _unityObjectGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Role", HeaderText = "Suggested role", Width = 190, MinimumWidth = 150 });
            _unityObjectGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "PreviewReadiness", HeaderText = "Preview status", Width = 300, MinimumWidth = 220 });
            _unityObjectGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "PathId", HeaderText = "Path ID", Width = 100 });
            _unityObjectGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "SerializedFileName", HeaderText = "Serialized file", Width = 230, MinimumWidth = 150 });
            _unityObjectGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "UnityVersion", HeaderText = "Unity version", Width = 120 });
            _unityObjectGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Platform", HeaderText = "Platform", Width = 100 });
            _unityObjectGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "DependencyCount", HeaderText = "References", Width = 90 });
            _unityObjectGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "UnresolvedDependencyCount", HeaderText = "Unresolved", Width = 90 });
            _unityObjectGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Status", HeaderText = "Status", Width = 190 });
        }

        private void ConfigureDependencyGrid()
        {
            _dependencyGrid.Dock = DockStyle.Fill;
            _dependencyGrid.ReadOnly = true;
            _dependencyGrid.AllowUserToAddRows = false;
            _dependencyGrid.AllowUserToDeleteRows = false;
            _dependencyGrid.AllowUserToOrderColumns = true;
            _dependencyGrid.MultiSelect = false;
            _dependencyGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _dependencyGrid.AutoGenerateColumns = false;
            _dependencyGrid.RowHeadersVisible = false;
            _dependencyGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells;
            _dependencyGrid.Columns.Clear();
            _dependencyGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "MemberPath", HeaderText = "Serialized field", Width = 260, MinimumWidth = 160 });
            _dependencyGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "TargetFileName", HeaderText = "Target file", Width = 230, MinimumWidth = 140 });
            _dependencyGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "TargetPathId", HeaderText = "Target Path ID", Width = 100 });
            _dependencyGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "TargetName", HeaderText = "Target object", Width = 220, MinimumWidth = 130 });
            _dependencyGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "TargetClassName", HeaderText = "Target class", Width = 150 });
            _dependencyGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Status", HeaderText = "Resolution", Width = 160 });
        }

        private void RefreshRoleFilterOptions()
        {
            _roleFilter.SelectedIndexChanged -= RoleFilter_SelectedIndexChanged;
            _roleFilter.BeginUpdate();
            try
            {
                _roleFilter.Items.Clear();
                _roleFilter.Items.Add("All roles");
                if (_lastUnityIndex != null)
                {
                    foreach (var role in _lastUnityIndex.Assets
                        .Select(x => x.Role)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                        _roleFilter.Items.Add(role);
                }
                _roleFilter.SelectedIndex = 0;
            }
            finally
            {
                _roleFilter.EndUpdate();
                _roleFilter.SelectedIndexChanged += RoleFilter_SelectedIndexChanged;
            }
        }

        private void RoleFilter_SelectedIndexChanged(object sender, EventArgs e)
        {
            ApplyRoleFilter();
        }

        private void ApplyRoleFilter()
        {
            var previous = _unityObjectGrid.CurrentRow?.DataBoundItem as VisualLabUnityAssetItem;
            var previousKey = previous?.Key;
            _unityObjectItems.Clear();

            var all = _lastUnityIndex?.Assets ?? new List<VisualLabUnityAssetItem>();
            var selectedRole = _roleFilter.SelectedItem as string;
            var visible = string.IsNullOrWhiteSpace(selectedRole) || selectedRole == "All roles"
                ? all
                : all.Where(x => string.Equals(x.Role, selectedRole, StringComparison.OrdinalIgnoreCase)).ToList();

            foreach (var item in visible)
                _unityObjectItems.Add(item);

            _objectCountStatus.Text = _lastUnityIndex == null
                ? "No parsed objects"
                : string.Format("{0:N0} shown / {1:N0} objects", _unityObjectItems.Count, _lastUnityIndex.Assets.Count);

            if (_unityObjectItems.Count == 0)
            {
                _dependencyItems.Clear();
                return;
            }

            var rowIndex = previousKey == null
                ? -1
                : _unityObjectItems.ToList().FindIndex(x => string.Equals(x.Key, previousKey, StringComparison.OrdinalIgnoreCase));
            if (rowIndex < 0) rowIndex = 0;

            if (_unityObjectGrid.Rows.Count > rowIndex)
            {
                _unityObjectGrid.ClearSelection();
                _unityObjectGrid.Rows[rowIndex].Selected = true;
                _unityObjectGrid.CurrentCell = _unityObjectGrid.Rows[rowIndex].Cells[0];
            }
        }

        private void UpdateMeshPreviewButton()
        {
            var selected = _unityObjectGrid.CurrentRow?.DataBoundItem as VisualLabUnityAssetItem;
            _previewMeshButton.Enabled = selected != null &&
                (string.Equals(selected.ClassName, "Mesh", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(selected.ClassName, "GameObject", StringComparison.OrdinalIgnoreCase)) &&
                _lastUnityIndex != null;
        }

        private async Task PreviewSelectedMeshAsync()
        {
            var selected = _unityObjectGrid.CurrentRow?.DataBoundItem as VisualLabUnityAssetItem;
            var isMesh = selected != null && string.Equals(selected.ClassName, "Mesh", StringComparison.OrdinalIgnoreCase);
            var isGameObject = selected != null && string.Equals(selected.ClassName, "GameObject", StringComparison.OrdinalIgnoreCase);
            if (!isMesh && !isGameObject)
            {
                MessageBox.Show(this, "Select a Unity Mesh or GameObject. GameObject preview assembles available child MeshFilter and SkinnedMeshRenderer geometry in static form; animation and materials are not active.",
                    "Juancho MLBB Visual Lab", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (_lastUnityIndex == null || _lastUnityIndex.InputFiles.Count == 0)
            {
                MessageBox.Show(this, "Analyze the Unity package first, then select a Mesh object.", "Juancho MLBB Visual Lab",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SetScanning(true);
            _progressBar.Style = ProgressBarStyle.Marquee;
            _progressBar.MarqueeAnimationSpeed = 24;
            _statusLabel.Text = "Preparing static mesh preview…";
            _detailLabel.Text = selected.Name;
            try
            {
                var preview = isMesh
                    ? await VisualLabUnityIndexer.ExtractMeshPreviewAsync(_lastUnityIndex.InputFiles, selected)
                    : await VisualLabUnityIndexer.ExtractGameObjectPreviewAsync(_lastUnityIndex.InputFiles, selected);
                using (var previewForm = new JuanchoVisualLabMeshPreviewForm(preview))
                    previewForm.ShowDialog(this);
                _statusLabel.Text = "Static mesh preview closed";
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "Mesh preview failed";
                MessageBox.Show(this,
                    "The selected mesh could not be previewed. Original bundle files were not modified.\r\n\r\n" + ex.Message,
                    "Juancho MLBB Visual Lab", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _progressBar.Style = ProgressBarStyle.Blocks;
                _progressBar.MarqueeAnimationSpeed = 0;
                SetScanning(false);
            }
        }

        private void RefreshDependenciesForSelectedObject()
        {
            var selected = _unityObjectGrid.CurrentRow?.DataBoundItem as VisualLabUnityAssetItem;
            _dependencyItems.Clear();
            if (selected == null || _lastUnityIndex == null) return;
            foreach (var dependency in _lastUnityIndex.Dependencies.Where(x => string.Equals(x.SourceAssetKey, selected.Key, StringComparison.OrdinalIgnoreCase)))
                _dependencyItems.Add(dependency);
            _selectedAssetPath = selected.SourceBundlePath;
            _detailLabel.Text = selected.ClassName + " · " + _dependencyItems.Count + " references";
        }

        private Control BuildTimelineTab()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var header = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            header.Controls.Add(new Label { Text = "Sequence title:", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
            _sequenceTitle.Width = 260;
            _sequenceTitle.Text = "Untitled MLBB skill sequence";
            header.Controls.Add(_sequenceTitle);

            AddTimelineButton(header, "Add Animation", VisualLabTimelineEventKind.Animation);
            AddTimelineButton(header, "Add Effect", VisualLabTimelineEventKind.Effect);
            AddTimelineButton(header, "Add Projectile", VisualLabTimelineEventKind.Projectile);
            AddTimelineButton(header, "Add Activation", VisualLabTimelineEventKind.Activation);
            AddTimelineButton(header, "Add Marker", VisualLabTimelineEventKind.Marker);

            var useSourceButton = new Button { Text = "Use Selected Package Asset", AutoSize = true };
            useSourceButton.Click += (s, e) =>
            {
                if (_timelineGrid.CurrentRow?.DataBoundItem is VisualLabTimelineEvent item && !string.IsNullOrWhiteSpace(_selectedAssetPath))
                {
                    item.SourceAssetPath = _selectedAssetPath;
                    _timelineGrid.Refresh();
                    _statusLabel.Text = "Assigned source path to " + item.Name;
                }
                else
                {
                    MessageBox.Show(this, "Select a timeline row and an asset from Package Explorer first.", "Juancho MLBB Visual Lab", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };
            header.Controls.Add(useSourceButton);

            var removeButton = new Button { Text = "Remove Event", AutoSize = true };
            removeButton.Click += (s, e) =>
            {
                if (_timelineGrid.CurrentRow?.DataBoundItem is VisualLabTimelineEvent item) _timelineItems.Remove(item);
            };
            header.Controls.Add(removeButton);

            var saveButton = new Button { Text = "Save Sequence As…", AutoSize = true };
            saveButton.Click += async (s, e) => await SaveSequenceAsync();
            header.Controls.Add(saveButton);

            var loadButton = new Button { Text = "Open Sequence…", AutoSize = true };
            loadButton.Click += async (s, e) => await LoadSequenceAsync();
            header.Controls.Add(loadButton);

            ConfigureTimelineGrid();
            _timelineGrid.DataSource = _timelineItems;
            var note = new Label
            {
                AutoSize = true,
                Padding = new Padding(0, 8, 0, 0),
                Text = "Manual sequence authoring only in this foundation build. Start/duration are seconds. Animation, Effect, and Projectile events require a source path. Playback is added when the reconstruction runtime is connected."
            };

            root.Controls.Add(header, 0, 0);
            root.Controls.Add(_timelineGrid, 0, 1);
            root.Controls.Add(note, 0, 2);
            return root;
        }

        private void ConfigureTimelineGrid()
        {
            _timelineGrid.Dock = DockStyle.Fill;
            _timelineGrid.AllowUserToAddRows = false;
            _timelineGrid.AllowUserToDeleteRows = false;
            _timelineGrid.AutoGenerateColumns = false;
            _timelineGrid.RowHeadersVisible = false;
            _timelineGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _timelineGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells;
            _timelineGrid.Columns.Clear();
            _timelineGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Name", HeaderText = "Event", Width = 170, MinimumWidth = 120 });
            var kindColumn = new DataGridViewComboBoxColumn
            {
                DataPropertyName = "Kind",
                HeaderText = "Track type",
                DataSource = Enum.GetValues(typeof(VisualLabTimelineEventKind)),
                Width = 115
            };
            _timelineGrid.Columns.Add(kindColumn);
            _timelineGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "StartSeconds", HeaderText = "Start (s)", Width = 90 });
            _timelineGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "DurationSeconds", HeaderText = "Duration (s)", Width = 100 });
            _timelineGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "SourceAssetPath", HeaderText = "Source asset path", Width = 420, MinimumWidth = 220 });
            _timelineGrid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = "Enabled", HeaderText = "Enabled", Width = 75 });
        }

        private Control BuildRuntimeTab()
        {
            var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22) };
            var title = new Label
            {
                Text = "Runtime adapter not connected yet",
                Font = new Font(Font, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(22, 22)
            };
            _runtimeStatus.Text =
                "Implemented in this foundation: read-only package inventory, SHA-256 duplicate detection, scan reports, AssetStudio-backed serialized-object indexing, PPtr dependency inspection, and a saved manual skill timeline. The dependency table reports actual references found in the selected parsed bundle set.\r\n\r\n" +
                "Not implemented yet: a live skinned-mesh scene renderer, reconstructed AnimationClip playback, full MLBB ParticleSystem/shader reproduction, and executable timeline playback. Those remain gated on a real runtime proof-of-concept using a complete hero package.\r\n\r\n" +
                "Source bundles are never edited by Visual Lab scanning or object indexing. Scan reports and sequence projects are blocked from being saved inside imported source folders. Import paths and serialized asset identifiers are retained for later runtime binding.";
            _runtimeStatus.AutoSize = false;
            _runtimeStatus.Location = new Point(22, 58);
            _runtimeStatus.Width = 850;
            _runtimeStatus.Height = 190;
            _runtimeStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            panel.Controls.Add(title);
            panel.Controls.Add(_runtimeStatus);
            return panel;
        }

        private void AddTimelineButton(FlowLayoutPanel panel, string label, VisualLabTimelineEventKind kind)
        {
            var button = new Button { Text = label, AutoSize = true };
            button.Click += (s, e) =>
            {
                _timelineItems.Add(new VisualLabTimelineEvent
                {
                    Name = kind.ToString() + " " + (_timelineItems.Count + 1),
                    Kind = kind,
                    SourceAssetPath = kind == VisualLabTimelineEventKind.Marker || kind == VisualLabTimelineEventKind.Activation ? "" : _selectedAssetPath,
                    StartSeconds = _timelineItems.Count == 0 ? 0 : _timelineItems.Max(x => x.StartSeconds + x.DurationSeconds),
                    DurationSeconds = kind == VisualLabTimelineEventKind.Marker ? 0 : 1
                });
            };
            panel.Controls.Add(button);
        }

        private async Task ChooseFolderAndScanAsync()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select a complete MLBB hero or skin package folder.";
                dialog.ShowNewFolderButton = false;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    await ScanAsync(new[] { dialog.SelectedPath });
            }
        }

        private async Task ChooseFilesAndScanAsync()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "Select one or more Unity bundles or companion assets";
                dialog.Filter = "Unity and companion assets|*.unity3d;*.bundle;*.assets;*.asset;*.ab;*.ress;*.resource;*.bytes;*.png;*.jpg;*.jpeg;*.tga;*.dds;*.ktx;*.astc;*.pvr;*.split*|All files|*.*";
                dialog.Multiselect = true;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    await ScanAsync(dialog.FileNames);
            }
        }

        private async Task ScanAsync(IEnumerable<string> paths)
        {
            _scanCancellation?.Cancel();
            _scanCancellation?.Dispose();
            _scanCancellation = new CancellationTokenSource();
            SetScanning(true);
            _assetItems.Clear();
            _lastScan = null;
            _lastUnityIndex = null;
            _unityObjectItems.Clear();
            _dependencyItems.Clear();
            _scanStatus.Text = "Discovering package files…";
            var progress = new Progress<VisualLabScanProgress>(p =>
            {
                _progressBar.Visible = true;
                _progressBar.Value = p.Total <= 0 ? 0 : Math.Min(100, (int)(100.0 * p.Completed / p.Total));
                _statusLabel.Text = "Scanning " + p.Completed + " / " + p.Total;
            });
            try
            {
                var result = await VisualLabPackageScanner.ScanAsync(paths, progress, _scanCancellation.Token);
                _lastScan = result;
                RememberProtectedRoots(result.ProtectedSourceRoots);
                foreach (var asset in result.Assets) _assetItems.Add(asset);
                _exportReportButton.Enabled = result.Assets.Count > 0 || result.Diagnostics.Count > 0;
                _analyzeObjectsButton.Enabled = result.Assets.Any(IsUnityInput);
                _scanStatus.Text = string.Format(
                    "Indexed {0} files · {1} duplicate-content entries · {2} warnings · {3} · Source files were not modified.",
                    result.Assets.Count, result.DuplicateFileCount, result.Diagnostics.Count(x => x.Severity == "Warning"), FormatBytes(result.TotalBytes));
                _statusLabel.Text = "Package scan completed";
                _detailLabel.Text = result.DurationMilliseconds + " ms";
                if (result.Assets.Count == 0)
                    MessageBox.Show(this, "No supported bundle or companion assets were found. Check the selected folder and the Diagnostics list in the scan report.", "Juancho MLBB Visual Lab", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (OperationCanceledException)
            {
                _statusLabel.Text = "Scan cancelled";
                _scanStatus.Text = "Scan cancelled. Source files were not modified.";
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "Scan failed";
                _scanStatus.Text = ex.Message;
                MessageBox.Show(this, "Package scan failed:\r\n\r\n" + ex.Message, "Juancho MLBB Visual Lab", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetScanning(false);
            }
        }

        private async Task AnalyzeUnityBundlesAsync()
        {
            if (_lastScan == null)
            {
                MessageBox.Show(this, "Scan a package folder or select bundle files first.", "Juancho MLBB Visual Lab", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var files = _lastScan.Assets
                .Where(x => IsUnityInput(x) && File.Exists(x.FullPath))
                .Select(x => x.FullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (files.Length == 0)
            {
                MessageBox.Show(this, "No Unity bundle or serialized asset files were found in this scan.", "Juancho MLBB Visual Lab", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SetScanning(true);
            _progressBar.Style = ProgressBarStyle.Marquee;
            _progressBar.MarqueeAnimationSpeed = 24;
            _statusLabel.Text = "Parsing Unity serialized objects…";
            _detailLabel.Text = files.Length + " candidate files";
            _unityObjectItems.Clear();
            _dependencyItems.Clear();
            _lastUnityIndex = null;

            try
            {
                var result = await VisualLabUnityIndexer.AnalyzeFilesAsync(files);
                _lastUnityIndex = result;
                RefreshRoleFilterOptions();
                ApplyRoleFilter();
                _objectStatus.Text = string.Format(
                    "Loaded {0} serialized files · Indexed {1} objects · {2} references resolved · {3} unresolved references · {4} ms. Source bundles were opened read-only.",
                    result.SerializedFileCount, result.Assets.Count, result.ResolvedDependencyCount,
                    result.UnresolvedDependencyCount, result.DurationMilliseconds);

                _statusLabel.Text = "Serialized object analysis complete";
                _detailLabel.Text = result.UnresolvedDependencyCount + " unresolved references";
                if (result.Diagnostics.Any(x => x.Severity == "Warning" || x.Severity == "Error"))
                {
                    var detail = string.Join(Environment.NewLine, result.Diagnostics
                        .Where(x => x.Severity == "Warning" || x.Severity == "Error")
                        .Select(x => x.Severity + ": " + x.Message)
                        .Take(8));
                    MessageBox.Show(this, detail, "Visual Lab package diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                if (_unityObjectItems.Count > 0)
                {
                    _unityObjectGrid.ClearSelection();
                    _unityObjectGrid.Rows[0].Selected = true;
                    _unityObjectGrid.CurrentCell = _unityObjectGrid.Rows[0].Cells[0];
                    RefreshDependenciesForSelectedObject();
                } else {
                    _dependencyItems.Clear();
                }
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "Serialized object analysis failed";
                _objectStatus.Text = "Parsing did not complete. Source files were not modified. " + ex.Message;
                MessageBox.Show(this, "Unity package analysis failed. The source bundles were not modified.\r\n\r\n" + ex.Message,
                    "Juancho MLBB Visual Lab", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _progressBar.Style = ProgressBarStyle.Blocks;
                _progressBar.MarqueeAnimationSpeed = 0;
                SetScanning(false);
            }
        }

        private static bool IsUnityInput(VisualLabAssetFile file)
        {
            if (file == null) return false;
            switch (file.Extension.ToLowerInvariant())
            {
                case ".unity3d":
                case ".bundle":
                case ".assets":
                case ".asset":
                case ".ab":
                case ".ress":
                case ".resource":
                    return true;
                default:
                    return file.Extension.StartsWith(".split", StringComparison.OrdinalIgnoreCase) &&
                           file.Extension.Length > 6 &&
                           file.Extension.Substring(6).All(char.IsDigit);

            }
        }

        private async Task ExportReportAsync()
        {
            if (_lastScan == null) return;
            using (var dialog = new SaveFileDialog())
            {
                dialog.Title = "Export read-only package inventory";
                dialog.FileName = "juancho-visual-lab-scan-report.json";
                dialog.Filter = "JSON report|*.json";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    await VisualLabProjectStore.SaveScanReportAsync(dialog.FileName, _lastScan);
                    _statusLabel.Text = "Scan report saved";
                    MessageBox.Show(this, "Scan report exported. The report was saved outside the imported source folders.", "Juancho MLBB Visual Lab", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Report export failed:\r\n\r\n" + ex.Message, "Juancho MLBB Visual Lab", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private async Task SaveSequenceAsync()
        {
            try
            {
                _timelineGrid.EndEdit();
                var sequence = new VisualLabSequence
                {
                    Title = string.IsNullOrWhiteSpace(_sequenceTitle.Text) ? "Untitled skill sequence" : _sequenceTitle.Text.Trim(),
                    ProtectedSourceRoots = _protectedSourceRoots.ToList(),
                    Events = _timelineItems.ToList()
                };
                using (var dialog = new SaveFileDialog())
                {
                    dialog.Title = "Save manual skill sequence";
                    dialog.FileName = "skill-sequence.juancho-visual-lab.json";
                    dialog.Filter = "Visual Lab sequence|*.juancho-visual-lab.json;*.json|JSON files|*.json";
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    await VisualLabProjectStore.SaveSequenceAsync(dialog.FileName, sequence);
                    _statusLabel.Text = "Sequence saved";
                    MessageBox.Show(this, "The timeline sequence was saved. It contains event timing and asset references; this foundation build does not execute the sequence yet.", "Juancho MLBB Visual Lab", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Sequence could not be saved:\r\n\r\n" + ex.Message, "Juancho MLBB Visual Lab", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private async Task LoadSequenceAsync()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "Open a Visual Lab timeline sequence";
                dialog.Filter = "Visual Lab sequence|*.juancho-visual-lab.json;*.json|JSON files|*.json";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var sequence = await VisualLabProjectStore.LoadSequenceAsync(dialog.FileName);
                    RememberProtectedRoots(sequence.ProtectedSourceRoots);
                    _timelineItems.Clear();
                    foreach (var item in sequence.Events) _timelineItems.Add(item);
                    _sequenceTitle.Text = sequence.Title;
                    _statusLabel.Text = "Sequence loaded: " + Path.GetFileName(dialog.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Sequence could not be opened:\r\n\r\n" + ex.Message, "Juancho MLBB Visual Lab", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void RememberProtectedRoots(IEnumerable<string> roots)
        {
            if (roots == null) return;
            foreach (var root in roots)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                var full = Path.GetFullPath(root);
                if (!_protectedSourceRoots.Any(existing => string.Equals(Path.GetFullPath(existing), full, StringComparison.OrdinalIgnoreCase)))
                    _protectedSourceRoots.Add(full);
            }
        }

        private VisualLabAssetFile GetSelectedAsset()
        {
            return _assetGrid.CurrentRow?.DataBoundItem as VisualLabAssetFile;
        }

        private void SetScanning(bool scanning)
        {
            _scanFolderButton.Enabled = !scanning;
            _scanFilesButton.Enabled = !scanning;
            _exportReportButton.Enabled = !scanning && _lastScan != null;
            _cancelButton.Enabled = scanning;
            _analyzeObjectsButton.Enabled = !scanning && _lastScan != null && _lastScan.Assets.Any(IsUnityInput);
            UpdateMeshPreviewButton();
            if (scanning) _previewMeshButton.Enabled = false;
            _progressBar.Visible = scanning;
            if (!scanning) _progressBar.Value = 0;
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " " + units[unit];
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _scanCancellation?.Cancel();
            base.OnFormClosing(e);
        }
    }
}
