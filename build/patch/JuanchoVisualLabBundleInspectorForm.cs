using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using AssetStudio;
using OpenTK.Graphics.OpenGL;
using GlMatrix4 = OpenTK.Mathematics.Matrix4;
using GlVector3 = OpenTK.Mathematics.Vector3;
using GlMathHelper = OpenTK.Mathematics.MathHelper;
using NMatrix = System.Numerics.Matrix4x4;
using NQuaternion = System.Numerics.Quaternion;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using UnityObject = AssetStudio.Object;

namespace AssetStudioGUI
{
    /// <summary>
    /// Read-only Unity-style inspection of a single MLBB hero skin bundle.
    /// The object tree is constructed from parsed GameObject, Transform, Component,
    /// Renderer, Material and Texture2D references; names and paths are not used to
    /// fabricate relationships.
    /// </summary>
    public sealed class JuanchoVisualLabBundleInspectorForm : Form
    {
        private readonly string[] _inputs;
        private readonly string[] _heroBundles;
        private readonly string _initialBundle;
        private readonly ComboBox _bundlePicker = new ComboBox();
        private readonly TextBox _search = new TextBox();
        private readonly TreeView _hierarchyTree = new TreeView();
        private readonly TreeView _objectsTree = new TreeView();
        private readonly RichTextBox _details = new RichTextBox();
        private readonly PictureBox _texturePreview = new PictureBox();
        private readonly Label _status = new Label();
        private readonly Label _textureInfo = new Label();
        private readonly FlowLayoutPanel _materialTextureCards = new FlowLayoutPanel();
        private readonly TabControl _leftTabs = new TabControl();
        private BundleParseState _state;
        private string _currentBundle;
        private List<UnityObject> _bundleObjects = new List<UnityObject>();
        private HashSet<UnityObject> _bundleObjectSet = new HashSet<UnityObject>();
        private bool _loading;

        public JuanchoVisualLabBundleInspectorForm(IEnumerable<string> packageInputs, IEnumerable<string> heroSkinBundles, string preferredBundle = null)
        {
            _inputs = (packageInputs ?? Enumerable.Empty<string>())
                .Where(File.Exists).Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            _heroBundles = (heroSkinBundles ?? Enumerable.Empty<string>())
                .Where(File.Exists).Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(IsHeroSkinBundle).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray();
            _initialBundle = _heroBundles.FirstOrDefault(x => string.Equals(x, preferredBundle, StringComparison.OrdinalIgnoreCase))
                ?? _heroBundles.FirstOrDefault();

            Text = "Juancho MLBB Visual Lab — Hero Bundle / Prefab Inspector";
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(1120, 680);
            Size = new Size(1580, 940);
            Font = new System.Drawing.Font("Segoe UI", 9F);
            BuildUi();

            foreach (var path in _heroBundles)
                _bundlePicker.Items.Add(new BundleChoice(path));
            if (_bundlePicker.Items.Count > 0)
            {
                var initial = _heroBundles.ToList().FindIndex(x => string.Equals(x, _initialBundle, StringComparison.OrdinalIgnoreCase));
                _bundlePicker.SelectedIndex = initial < 0 ? 0 : initial;
            }

            Shown += async (s, e) => await EnsureParsedAsync();
            FormClosed += (s, e) => Cleanup();
            _bundlePicker.SelectedIndexChanged += async (s, e) =>
            {
                if (_loading) return;
                if (_bundlePicker.SelectedItem is BundleChoice choice)
                {
                    _currentBundle = choice.Path;
                    if (_state != null) PopulateCurrentBundle();
                }
            };
            _search.TextChanged += (s, e) => PopulateAllObjectsTree();
            _hierarchyTree.AfterSelect += (s, e) => SelectObject(e.Node?.Tag as UnityObject);
            _objectsTree.AfterSelect += (s, e) => SelectObject(e.Node?.Tag as UnityObject);
        }

        private sealed class BundleChoice
        {
            public string Path { get; }
            public BundleChoice(string path) { Path = path; }
            public override string ToString() => System.IO.Path.GetFileName(Path) + "   (" + Path + ")";
        }

        private sealed class BundleParseState
        {
            public AssetsManager Manager;
            public string Workspace;
            public Dictionary<string, string> WorkspaceToOriginal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string[] OriginalInputs = Array.Empty<string>();
            public Dictionary<SerializedFile, string> SourceByFile = new Dictionary<SerializedFile, string>();
            public List<SerializedFile> Files = new List<SerializedFile>();
            public int ParsedObjectCount;
            public int SerializedObjectCount;
            public int UnparsedObjectCount;
        }

        private void BuildUi()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var header = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(0, 0, 0, 6) };
            header.Controls.Add(new Label { Text = "Hero skin bundle:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            _bundlePicker.DropDownStyle = ComboBoxStyle.DropDownList;
            _bundlePicker.Width = 520;
            header.Controls.Add(_bundlePicker);
            header.Controls.Add(new Label { Text = "Search assets:", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
            _search.Width = 230;
            header.Controls.Add(_search);
            var expandButton = new Button { Text = "Expand Hierarchy", AutoSize = true };
            expandButton.Click += (s, e) => _hierarchyTree.ExpandAll();
            header.Controls.Add(expandButton);
            var collapseButton = new Button { Text = "Collapse Hierarchy", AutoSize = true };
            collapseButton.Click += (s, e) => _hierarchyTree.CollapseAll();
            header.Controls.Add(collapseButton);
            var previewButton = new Button { Text = "3D Preview with Texture Maps", AutoSize = true };
            previewButton.Click += (s, e) => OpenTexturedPreview();
            header.Controls.Add(previewButton);

            _leftTabs.Dock = DockStyle.Fill;
            var hierarchyTab = new TabPage("Prefab Hierarchy");
            var allObjectsTab = new TabPage("All Bundle Objects");
            _hierarchyTree.Dock = DockStyle.Fill;
            _hierarchyTree.HideSelection = false;
            _hierarchyTree.ShowNodeToolTips = true;
            _objectsTree.Dock = DockStyle.Fill;
            _objectsTree.HideSelection = false;
            _objectsTree.ShowNodeToolTips = true;
            hierarchyTab.Controls.Add(_hierarchyTree);
            allObjectsTab.Controls.Add(_objectsTree);
            _leftTabs.TabPages.Add(hierarchyTab);
            _leftTabs.TabPages.Add(allObjectsTab);

            _details.Dock = DockStyle.Fill;
            _details.ReadOnly = true;
            _details.WordWrap = false;
            _details.Font = new System.Drawing.Font("Consolas", 9F);
            _texturePreview.Dock = DockStyle.Fill;
            _texturePreview.SizeMode = PictureBoxSizeMode.Zoom;
            _texturePreview.BackColor = System.Drawing.Color.FromArgb(38, 38, 42);
            _textureInfo.AutoSize = true;
            _textureInfo.MaximumSize = new Size(520, 0);
            _textureInfo.Text = "Select a Texture2D or a material/renderer to inspect its texture slots.";

            var detailTabs = new TabControl { Dock = DockStyle.Fill };
            var detailTab = new TabPage("Object Details");
            var appearanceTab = new TabPage("Materials & Textures");
            var detailLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1, Padding = new Padding(4) };
            detailLayout.Controls.Add(_details, 0, 0);
            detailTab.Controls.Add(detailLayout);

            var appearanceLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(4) };
            appearanceLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 280));
            appearanceLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            appearanceLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            appearanceLayout.Controls.Add(_texturePreview, 0, 0);
            appearanceLayout.Controls.Add(_textureInfo, 0, 1);
            _materialTextureCards.Dock = DockStyle.Fill;
            _materialTextureCards.AutoScroll = true;
            _materialTextureCards.WrapContents = true;
            _materialTextureCards.FlowDirection = FlowDirection.LeftToRight;
            _materialTextureCards.Padding = new Padding(4);
            appearanceLayout.Controls.Add(_materialTextureCards, 0, 2);
            appearanceTab.Controls.Add(appearanceLayout);
            detailTabs.TabPages.Add(detailTab);
            detailTabs.TabPages.Add(appearanceTab);

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 600, FixedPanel = FixedPanel.Panel1 };
            split.Panel1.Controls.Add(_leftTabs);
            split.Panel2.Controls.Add(detailTabs);
            split.Panel1MinSize = 300;
            split.Panel2MinSize = 400;

            _status.AutoSize = true;
            _status.Text = "Parsing Unity objects from isolated copies. Original bundle files remain unchanged.";
            _status.Padding = new Padding(0, 6, 0, 0);
            root.Controls.Add(header, 0, 0);
            root.Controls.Add(split, 0, 1);
            root.Controls.Add(_status, 0, 2);
            Controls.Add(root);
        }

        private void OpenTexturedPreview()
        {
            if (_state == null || _bundleObjects.Count == 0)
            {
                MessageBox.Show(this, "Wait for the selected hero skin bundle to finish parsing first.", "Juancho Visual Lab", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            UnityObject selected = _hierarchyTree.SelectedNode?.Tag as UnityObject
                ?? _objectsTree.SelectedNode?.Tag as UnityObject;
            var focus = ResolveFocusGameObject(selected);
            try
            {
                using (var preview = new JuanchoVisualLabTexturedMeshPreviewForm(_bundleObjects, focus))
                    preview.ShowDialog(this);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not prepare a textured preview. Original bundle files were not modified.\r\n\r\n" + ex.Message,
                    "Juancho Visual Lab", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private GameObject ResolveFocusGameObject(UnityObject obj)
        {
            if (obj is GameObject go) return go;
            if (obj is Transform transform) return GetGameObject(transform);
            if (obj is Component component)
            {
                try { if (component.m_GameObject != null && component.m_GameObject.TryGet(out GameObject componentGo)) return componentGo; } catch { }
            }
            if (obj is Mesh mesh)
            {
                foreach (var candidate in _bundleObjects.OfType<GameObject>())
                {
                    foreach (var candidateComponent in GetComponents(candidate))
                    {
                        if (candidateComponent is MeshFilter filter && ReferenceEquals(TryGet(filter.m_Mesh), mesh)) return candidate;
                        if (candidateComponent is SkinnedMeshRenderer skin && ReferenceEquals(TryGet(skin.m_Mesh), mesh)) return candidate;
                    }
                }
            }
            if (obj is Material material)
            {
                foreach (var candidate in _bundleObjects.OfType<GameObject>())
                    if (GetComponents(candidate).OfType<Renderer>().Any(renderer => ResolveMaterials(renderer).Any(m => ReferenceEquals(m, material))))
                        return candidate;
            }
            if (obj is Texture2D texture)
            {
                foreach (var candidate in _bundleObjects.OfType<GameObject>())
                    if (GetComponents(candidate).OfType<Renderer>().Any(renderer => ResolveMaterials(renderer).Any(m =>
                        (m.m_SavedProperties?.m_TexEnvs ?? Array.Empty<KeyValuePair<string, UnityTexEnv>>())
                        .Any(slot => ReferenceEquals(TryGet(slot.Value?.m_Texture), texture)))))
                        return candidate;
            }
            return null;
        }

        private async Task EnsureParsedAsync()
        {
            if (_state != null || _loading) return;
            if (_inputs.Length == 0 || _heroBundles.Length == 0)
            {
                _status.Text = "No hero skin bundle was found. Scan the package folder and include hero_<heroname>_skin.unity3d.";
                return;
            }

            _loading = true;
            _bundlePicker.Enabled = false;
            _search.Enabled = false;
            _status.Text = "Loading complete Unity object data and resolving references…";
            try
            {
                _state = await Task.Run(() => ParseInputs(_inputs));
                if (_bundlePicker.SelectedItem is BundleChoice choice)
                    _currentBundle = choice.Path;
                else
                    _currentBundle = _initialBundle;
                PopulateCurrentBundle();
            }
            catch (Exception ex)
            {
                _status.Text = "Bundle parse failed: " + ex.Message;
                MessageBox.Show(this,
                    "Could not parse the hero skin bundle and its selected package dependencies. Original files were not modified.\r\n\r\n" + ex.Message,
                    "Juancho Bundle Inspector", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _loading = false;
                _bundlePicker.Enabled = true;
                _search.Enabled = true;
            }
        }

        private static BundleParseState ParseInputs(string[] inputs)
        {
            var state = new BundleParseState
            {
                Manager = new AssetsManager(),
                Workspace = Path.Combine(Path.GetTempPath(), "JuanchoMLBBVisualLab", "BundleInspector_" + Guid.NewGuid().ToString("N")),
                OriginalInputs = inputs
            };
            try
            {
                Directory.CreateDirectory(state.Workspace);
                var isolated = CopyInputs(inputs, state.Workspace, state.WorkspaceToOriginal);
                MergeSplitParts(state.Workspace);
                state.Manager.LoadFiles(isolated);
                state.Files = state.Manager.assetsFileList.ToList();
                foreach (var file in state.Files)
                {
                    var original = ResolveOriginalPath(file, state.WorkspaceToOriginal, inputs, state.Workspace);
                    state.SourceByFile[file] = original;
                    var serializedCount = file.m_Objects?.Count ?? 0;
                    var parsedCount = file.Objects?.Count ?? 0;
                    state.SerializedObjectCount += serializedCount;
                    state.ParsedObjectCount += parsedCount;
                    state.UnparsedObjectCount += Math.Max(0, serializedCount - parsedCount);
                }
                return state;
            }
            catch
            {
                try { state.Manager.Clear(); } catch { }
                try { if (Directory.Exists(state.Workspace)) Directory.Delete(state.Workspace, true); } catch { }
                throw;
            }
        }

        private void PopulateCurrentBundle()
        {
            if (_state == null || string.IsNullOrWhiteSpace(_currentBundle)) return;
            _bundleObjects = _state.Files
                .Where(file => _state.SourceByFile.TryGetValue(file, out var source) &&
                    string.Equals(SafeFullPath(source), SafeFullPath(_currentBundle), StringComparison.OrdinalIgnoreCase))
                .SelectMany(file => file.Objects ?? new List<UnityObject>())
                .Where(obj => obj != null)
                .GroupBy(ObjectIdentity, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(obj => obj.type.ToString(), StringComparer.OrdinalIgnoreCase)
                .ThenBy(GetObjectName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(obj => obj.m_PathID).ToList();
            _bundleObjectSet = new HashSet<UnityObject>(_bundleObjects);

            _hierarchyTree.BeginUpdate();
            try
            {
                _hierarchyTree.Nodes.Clear();
                var root = new TreeNode("Prefab / Scene Hierarchy");
                _hierarchyTree.Nodes.Add(root);
                var transforms = _bundleObjects.OfType<Transform>().ToArray();
                var transformSet = new HashSet<Transform>(transforms);
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var roots = transforms.Where(transform =>
                {
                    return transform.m_Father == null ||
                        !transform.m_Father.TryGet(out Transform parent) ||
                        parent == null || !transformSet.Contains(parent);
                }).ToArray();

                foreach (var transform in roots)
                    AddTransformNode(transform, root, transformSet, visited, 0);

                foreach (var transform in transforms)
                    if (!visited.Contains(ObjectIdentity(transform)))
                        AddTransformNode(transform, root, transformSet, visited, 0);

                if (transforms.Length == 0)
                {
                    var fallbackGameObjects = _bundleObjects.OfType<GameObject>().ToArray();
                    foreach (var go in fallbackGameObjects)
                    {
                        var node = new TreeNode("GameObject: " + go.m_Name) { Tag = go, ToolTipText = ObjectTooltip(go) };
                        root.Nodes.Add(node);
                        AddGameObjectComponents(go, node);
                    }
                }

                if (root.Nodes.Count == 0)
                    root.Nodes.Add(new TreeNode("No parsed GameObject/Transform hierarchy found in this bundle."));
                root.Expand();
            }
            finally { _hierarchyTree.EndUpdate(); }

            PopulateAllObjectsTree();
            var meshes = _bundleObjects.OfType<Mesh>().Count();
            var renderers = _bundleObjects.OfType<Renderer>().Count();
            var gameObjects = _bundleObjects.OfType<GameObject>().Count();
            var transformsCount = _bundleObjects.OfType<Transform>().Count();
            var materials = _bundleObjects.OfType<Material>().Count();
            var textures = _bundleObjects.OfType<Texture2D>().Count();
            var clips = _bundleObjects.OfType<AnimationClip>().Count();
            var targetSerializedFiles = _state.Files.Where(f => _state.SourceByFile.TryGetValue(f, out var src) &&
                string.Equals(SafeFullPath(src), SafeFullPath(_currentBundle), StringComparison.OrdinalIgnoreCase)).ToArray();
            var selectedFileCount = targetSerializedFiles.Length;
            var unparsedForBundle = targetSerializedFiles.Sum(f =>
                Math.Max(0, (f.m_Objects?.Count ?? 0) - (f.Objects?.Count ?? 0)));
            _status.Text = string.Format(
                "{0} · serialized files: {1} · parsed objects: {2:N0} · GameObjects: {3:N0} · Transforms/bones: {4:N0} · Renderers: {5:N0} · Meshes: {6:N0} · Materials: {7:N0} · Texture2D: {8:N0} · AnimationClips: {9:N0} · unparsed entries in this bundle: {10:N0}",
                Path.GetFileName(_currentBundle), selectedFileCount, _bundleObjects.Count, gameObjects, transformsCount,
                renderers, meshes, materials, textures, clips, unparsedForBundle);
            _details.Text = "Select a hierarchy node or an object in All Bundle Objects.\r\n\r\nThis inspector reads actual serialized object references. The bundle tree is not a filename-based guess.";
            ClearAppearance();
        }

        private void AddTransformNode(Transform transform, TreeNode parent, HashSet<Transform> transformSet, HashSet<string> visited, int depth)
        {
            if (transform == null || depth > 512) return;
            var key = ObjectIdentity(transform);
            if (!visited.Add(key)) return;
            var go = GetGameObject(transform);
            var node = new TreeNode(go == null ? "Transform: " + GetObjectName(transform) : go.m_Name)
            {
                Tag = (UnityObject)(go ?? (UnityObject)transform),
                ToolTipText = ObjectTooltip(go ?? (UnityObject)transform)
            };
            parent.Nodes.Add(node);

            if (go != null) AddGameObjectComponents(go, node);
            else node.Nodes.Add(new TreeNode("Transform component") { Tag = transform, ToolTipText = ObjectTooltip(transform) });

            foreach (var childPtr in transform.m_Children ?? Array.Empty<PPtr<Transform>>())
            {
                if (childPtr != null && childPtr.TryGet(out Transform child) && child != null && transformSet.Contains(child))
                    AddTransformNode(child, node, transformSet, visited, depth + 1);
            }
        }

        private void AddGameObjectComponents(GameObject go, TreeNode parent)
        {
            var transform = GetTransform(go);
            if (transform != null)
            {
                parent.Nodes.Add(new TreeNode("Transform  ·  local position / rotation / scale")
                {
                    Tag = transform,
                    ToolTipText = ObjectTooltip(transform)
                });
            }

            foreach (var pointer in go.m_Components ?? Array.Empty<PPtr<Component>>())
            {
                if (pointer == null || !pointer.TryGet(out Component component) || component == null || component is Transform)
                    continue;
                var componentNode = new TreeNode(component.GetType().Name + "  ·  " + GetObjectName(component))
                {
                    Tag = component,
                    ToolTipText = ObjectTooltip(component)
                };
                parent.Nodes.Add(componentNode);
                AddComponentReferences(component, componentNode);
            }
        }

        private void AddComponentReferences(Component component, TreeNode parent)
        {
            if (component is MeshFilter filter)
            {
                AddReference(parent, "Mesh", TryGet(filter.m_Mesh));
            }

            if (component is SkinnedMeshRenderer skinned)
            {
                AddReference(parent, "Skinned Mesh", TryGet(skinned.m_Mesh));
                var bonesNode = new TreeNode("Bones / skin rig (" + (skinned.m_Bones?.Length ?? 0) + ")");
                parent.Nodes.Add(bonesNode);
                int index = 0;
                foreach (var pointer in skinned.m_Bones ?? Array.Empty<PPtr<Transform>>())
                {
                    Transform bone = null;
                    try { pointer?.TryGet(out bone); } catch { }
                    var boneName = bone == null ? "(unresolved bone)" : GetObjectName(bone);
                    if (bone != null)
                    {
                        var boneGo = GetGameObject(bone);
                        if (boneGo != null && !string.IsNullOrWhiteSpace(boneGo.m_Name)) boneName = boneGo.m_Name;
                    }
                    var boneNode = new TreeNode(string.Format("[{0:D3}] {1}", index, boneName))
                    {
                        Tag = (UnityObject)bone ?? (UnityObject)component,
                        ToolTipText = bone == null ? "Bone reference could not be resolved from the loaded package." : ObjectTooltip(bone)
                    };
                    bonesNode.Nodes.Add(boneNode);
                    index++;
                }
            }

            if (component is Renderer renderer)
            {
                var materialsNode = new TreeNode("Materials / renderer slots (" + (renderer.m_Materials?.Length ?? 0) + ")");
                parent.Nodes.Add(materialsNode);
                int slot = 0;
                foreach (var pointer in renderer.m_Materials ?? Array.Empty<PPtr<Material>>())
                {
                    Material material = null;
                    try { pointer?.TryGet(out material); } catch { }
                    var materialNode = new TreeNode("Slot " + slot + ": " + (material == null ? "(unresolved material)" : material.m_Name))
                    {
                        Tag = (UnityObject)material ?? (UnityObject)component,
                        ToolTipText = material == null ? "Material reference could not be resolved." : ObjectTooltip(material)
                    };
                    materialsNode.Nodes.Add(materialNode);
                    if (material != null) AddMaterialTextureNodes(material, materialNode);
                    slot++;
                }
            }

            if (component is Animator animator)
            {
                AddReference(parent, "Avatar / rig definition", TryGet(animator.m_Avatar));
                AddReference(parent, "Runtime Animator Controller", TryGet(animator.m_Controller));
            }

            if (component is MonoBehaviour mono && mono.m_Script != null && mono.m_Script.TryGet(out MonoScript script) && script != null)
                AddReference(parent, "MonoBehaviour Script", script);
        }

        private void AddMaterialTextureNodes(Material material, TreeNode parent)
        {
            var texEnvs = material.m_SavedProperties?.m_TexEnvs;
            if (texEnvs == null) return;
            foreach (var slot in texEnvs)
            {
                Texture texture = null;
                try { slot.Value?.m_Texture?.TryGet(out texture); } catch { }
                var node = new TreeNode("Texture slot " + slot.Key + ": " + (texture == null ? "(empty / unresolved)" : GetObjectName(texture)))
                {
                    Tag = (UnityObject)texture ?? (UnityObject)material,
                    ToolTipText = texture == null ? "No Texture2D resolved for this saved material property." : ObjectTooltip(texture)
                };
                parent.Nodes.Add(node);
            }
        }

        private void AddReference(TreeNode parent, string label, UnityObject target)
        {
            parent.Nodes.Add(new TreeNode(label + ": " + (target == null ? "(unresolved)" : GetObjectName(target)))
            {
                Tag = target,
                ToolTipText = target == null ? "The reference is absent or could not be resolved." : ObjectTooltip(target)
            });
        }

        private void PopulateAllObjectsTree()
        {
            if (_state == null || _bundleObjects == null) return;
            var filter = (_search.Text ?? "").Trim();
            var visible = string.IsNullOrEmpty(filter) ? _bundleObjects : _bundleObjects.Where(obj =>
                GetObjectName(obj).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                obj.type.ToString().IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                obj.m_PathID.ToString().IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            _objectsTree.BeginUpdate();
            try
            {
                _objectsTree.Nodes.Clear();
                var root = new TreeNode("All Bundle Objects (" + visible.Count.ToString("N0") + " / " + _bundleObjects.Count.ToString("N0") + ")");
                _objectsTree.Nodes.Add(root);
                foreach (var group in visible.GroupBy(obj => obj.type.ToString()).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var groupNode = new TreeNode(group.Key + " (" + group.Count().ToString("N0") + ")");
                    root.Nodes.Add(groupNode);
                    foreach (var obj in group.OrderBy(GetObjectName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.m_PathID))
                        groupNode.Nodes.Add(new TreeNode(GetObjectName(obj) + "   [Path ID " + obj.m_PathID + "]")
                        {
                            Tag = obj,
                            ToolTipText = ObjectTooltip(obj)
                        });
                }
                root.Expand();
            }
            finally { _objectsTree.EndUpdate(); }
        }

        private void SelectObject(UnityObject obj)
        {
            if (obj == null) return;
            _details.Text = DescribeObject(obj);
            ClearAppearance();
            if (obj is Texture2D texture)
            {
                var bitmap = TryConvertTexture(texture, out var diagnostic);
                if (bitmap != null)
                {
                    _texturePreview.Image = bitmap;
                    _textureInfo.Text = TextureMetadata(texture);
                }
                else
                {
                    _textureInfo.Text = TextureMetadata(texture) + "\r\nPreview decode failed: " + diagnostic;
                }
            }

            var materials = GetRelatedMaterials(obj).Distinct().ToArray();
            var cardsAdded = 0;
            foreach (var material in materials)
            {
                cardsAdded += AddMaterialCard(material);
                if (cardsAdded >= 32) break;
            }

            if (cardsAdded == 0 && !(obj is Texture2D))
                _textureInfo.Text = "No material texture slots were resolved for this selection. A renderer may have no material, the shader may store textures outside the parsed saved properties, or the texture may be external/not included in the selected package.";
        }

        private List<Material> GetRelatedMaterials(UnityObject obj)
        {
            var result = new List<Material>();
            if (obj is Material material) result.Add(material);
            if (obj is Renderer renderer) result.AddRange(ResolveMaterials(renderer));
            if (obj is Component component)
            {
                GameObject go = null;
                try { if (component.m_GameObject != null) component.m_GameObject.TryGet(out go); } catch { }
                if (go != null)
                    foreach (var linked in GetComponents(go).OfType<Renderer>())
                        result.AddRange(ResolveMaterials(linked));
            }
            if (obj is GameObject gameObject)
            {
                foreach (var linked in EnumerateRenderers(gameObject)) result.AddRange(ResolveMaterials(linked));
            }
            if (obj is Transform transform)
            {
                var go = GetGameObject(transform);
                if (go != null)
                    foreach (var linked in EnumerateRenderers(go)) result.AddRange(ResolveMaterials(linked));
            }
            if (obj is Mesh mesh) result.AddRange(FindMaterialsForMesh(mesh));
            return result.Where(x => x != null).GroupBy(ObjectIdentity, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToList();
        }

        private IEnumerable<Renderer> EnumerateRenderers(GameObject root)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var start = GetTransform(root);
            if (start == null)
            {
                foreach (var renderer in GetComponents(root).OfType<Renderer>()) yield return renderer;
                yield break;
            }
            var stack = new Stack<Transform>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (current == null || !seen.Add(ObjectIdentity(current))) continue;
                var go = GetGameObject(current);
                if (go != null)
                    foreach (var renderer in GetComponents(go).OfType<Renderer>())
                        yield return renderer;
                foreach (var childPtr in current.m_Children ?? Array.Empty<PPtr<Transform>>())
                    if (childPtr != null && childPtr.TryGet(out Transform child) && child != null)
                        stack.Push(child);
            }
        }

        private IEnumerable<Material> FindMaterialsForMesh(Mesh target)
        {
            foreach (var go in _bundleObjects.OfType<GameObject>())
            {
                var comps = GetComponents(go).ToArray();
                bool usesMesh = comps.OfType<MeshFilter>().Any(filter => ReferenceEquals(TryGet(filter.m_Mesh), target)) ||
                    comps.OfType<SkinnedMeshRenderer>().Any(renderer => ReferenceEquals(TryGet(renderer.m_Mesh), target));
                if (!usesMesh) continue;
                foreach (var renderer in comps.OfType<Renderer>())
                    foreach (var material in ResolveMaterials(renderer))
                        yield return material;
            }
        }

        private List<Material> ResolveMaterials(Renderer renderer)
        {
            var materials = new List<Material>();
            foreach (var pointer in renderer.m_Materials ?? Array.Empty<PPtr<Material>>())
                if (pointer != null && pointer.TryGet(out Material material) && material != null) materials.Add(material);
            return materials;
        }

        private int AddMaterialCard(Material material)
        {
            if (material == null) return 0;
            return AddTextureCardsFromMaterial(material, material.m_Name);
        }

        private int AddTextureCardsFromMaterial(Material material, string title)
        {
            if (material?.m_SavedProperties?.m_TexEnvs == null) return 0;
            var added = 0;
            foreach (var slot in material.m_SavedProperties.m_TexEnvs)
            {
                Texture texture = null;
                try { slot.Value?.m_Texture?.TryGet(out texture); } catch { }
                var display = texture as Texture2D;
                var panel = new Panel { Width = 190, Height = 190, Margin = new Padding(6), BorderStyle = BorderStyle.FixedSingle };
                var picture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = System.Drawing.Color.FromArgb(38, 38, 42) };
                if (display != null)
                {
                    picture.Image = TryConvertTexture(display, out var diagnostic);
                }
                panel.Controls.Add(picture);
                var caption = new Label
                {
                    Text = title + "\r\n" + slot.Key + "\r\n" + (texture == null ? "(empty / unresolved)" : GetObjectName(texture)),
                    Dock = DockStyle.Top,
                    Height = 46,
                    AutoEllipsis = true
                };
                if (display != null && picture.Image == null)
                    caption.Text += "\r\nDecode failed.";
                panel.Controls.Add(caption);
                caption.BringToFront();
                _materialTextureCards.Controls.Add(panel);
                added++;
                if (added >= 16) break;
            }
            return added;
        }

        private static Bitmap TryConvertTexture(Texture2D texture, out string diagnostic)
        {
            diagnostic = "";
            try
            {
                using (var image = texture.ConvertToImage(true))
                {
                    if (image == null)
                    {
                        diagnostic = "AssetStudio returned no decoded image. The format may be unsupported or external stream data may be missing.";
                        return null;
                    }
                    var bytes = image.ConvertToBytes();
                    using (var direct = new DirectBitmap(bytes, texture.m_Width, texture.m_Height))
                        return new Bitmap(direct.Bitmap);
                }
            }
            catch (Exception ex)
            {
                diagnostic = ex.Message;
                return null;
            }
        }

        private void ClearAppearance()
        {
            if (_texturePreview.Image != null)
            {
                var old = _texturePreview.Image;
                _texturePreview.Image = null;
                old.Dispose();
            }
            foreach (Control control in _materialTextureCards.Controls.Cast<Control>().ToArray())
            {
                DisposeControlImages(control);
                control.Dispose();
            }
            _materialTextureCards.Controls.Clear();
            _textureInfo.Text = "Select a Texture2D or a material/renderer to inspect its texture slots.";
        }

        private static void DisposeControlImages(Control control)
        {
            if (control is PictureBox picture && picture.Image != null)
            {
                var image = picture.Image;
                picture.Image = null;
                image.Dispose();
            }
            foreach (Control child in control.Controls) DisposeControlImages(child);
        }

        private string DescribeObject(UnityObject obj)
        {
            var lines = new List<string>
            {
                "Name: " + GetObjectName(obj),
                "Unity type: " + obj.type + " (" + obj.GetType().Name + ")",
                "Path ID: " + obj.m_PathID,
                "Source bundle: " + (_state.SourceByFile.TryGetValue(obj.assetsFile, out var path) ? path : "(external / unresolved source)"),
                "Serialized file: " + (obj.assetsFile?.fileName ?? ""),
                "Serialized byte size: " + obj.byteSize,
                "Unity version: " + (obj.assetsFile?.unityVersion ?? ""),
                "Build platform: " + (obj.assetsFile?.m_TargetPlatform.ToString() ?? "")
            };

            switch (obj)
            {
                case GameObject go:
                    lines.Add("Components: " + string.Join(", ", GetComponents(go).Select(x => x.GetType().Name)));
                    var tr = GetTransform(go);
                    if (tr != null)
                    {
                        lines.Add("Child transforms: " + (tr.m_Children?.Length ?? 0));
                        lines.Add("Parent: " + ResolveTransformName(tr.m_Father));
                    }
                    break;
                case Transform transform:
                    lines.Add("GameObject: " + (GetGameObject(transform)?.m_Name ?? "(unresolved)"));
                    lines.Add("Parent: " + ResolveTransformName(transform.m_Father));
                    lines.Add("Children: " + (transform.m_Children?.Length ?? 0));
                    lines.Add("Local position: " + transform.m_LocalPosition);
                    lines.Add("Local rotation (xyzw): " + transform.m_LocalRotation);
                    lines.Add("Local scale: " + transform.m_LocalScale);
                    break;
                case SkinnedMeshRenderer skinned:
                    var smesh = TryGet(skinned.m_Mesh);
                    lines.Add("Mesh: " + (smesh?.m_Name ?? "(unresolved)"));
                    lines.Add("Bone references: " + (skinned.m_Bones?.Length ?? 0));
                    lines.Add("Material slots: " + (skinned.m_Materials?.Length ?? 0));
                    lines.Add("Resolved bones: " + (skinned.m_Bones ?? Array.Empty<PPtr<Transform>>()).Count(pointer => pointer != null && pointer.TryGet(out Transform bone) && bone != null));
                    if (smesh != null) lines.Add(DescribeMesh(smesh));
                    break;
                case MeshFilter filter:
                    var filterMesh = TryGet(filter.m_Mesh);
                    lines.Add("Mesh: " + (filterMesh?.m_Name ?? "(unresolved)"));
                    if (filterMesh != null) lines.Add(DescribeMesh(filterMesh));
                    break;
                case Renderer renderer:
                    lines.Add("Material slots: " + (renderer.m_Materials?.Length ?? 0));
                    lines.Add("Resolved material slots: " + ResolveMaterials(renderer).Count);
                    lines.Add("Materials: " + string.Join(", ", ResolveMaterials(renderer).Select(x => x.m_Name)));
                    break;
                case Mesh mesh:
                    lines.Add(DescribeMesh(mesh));
                    lines.Add("Bound material(s): " + string.Join(", ", FindMaterialsForMesh(mesh).Select(x => x.m_Name).Distinct(StringComparer.OrdinalIgnoreCase)));
                    break;
                case Material material:
                    Shader shader = null;
                    try { material.m_Shader?.TryGet(out shader); } catch { }
                    lines.Add("Shader: " + (shader?.m_ParsedForm?.m_Name ?? shader?.m_Name ?? "(unresolved)"));
                    lines.Add("Saved texture slots: " + (material.m_SavedProperties?.m_TexEnvs?.Length ?? 0));
                    foreach (var slot in material.m_SavedProperties?.m_TexEnvs ?? Array.Empty<KeyValuePair<string, UnityTexEnv>>())
                    {
                        Texture texture = null;
                        try { slot.Value?.m_Texture?.TryGet(out texture); } catch { }
                        lines.Add("  " + slot.Key + ": " + (texture == null ? "(empty / unresolved)" : GetObjectName(texture)));
                    }
                    foreach (var value in material.m_SavedProperties?.m_Floats ?? Array.Empty<KeyValuePair<string, float>>())
                        lines.Add("  float " + value.Key + " = " + value.Value);
                    foreach (var value in material.m_SavedProperties?.m_Colors ?? Array.Empty<KeyValuePair<string, AssetStudio.Color>>())
                        lines.Add("  color " + value.Key + " = " + value.Value);
                    break;
                case Texture2D texture:
                    lines.Add(TextureMetadata(texture));
                    break;
                case Animator animator:
                    Avatar avatar = null;
                    RuntimeAnimatorController controller = null;
                    try { animator.m_Avatar?.TryGet(out avatar); } catch { }
                    try { animator.m_Controller?.TryGet(out controller); } catch { }
                    lines.Add("Avatar: " + (avatar?.m_Name ?? "(unresolved)"));
                    lines.Add("Controller: " + (controller?.m_Name ?? "(unresolved)"));
                    lines.Add("Has transform hierarchy: " + animator.m_HasTransformHierarchy);
                    break;
                case AnimationClip clip:
                    lines.Add("Legacy: " + clip.m_Legacy);
                    lines.Add("Compressed: " + clip.m_Compressed);
                    lines.Add("Sample rate: " + clip.m_SampleRate);
                    lines.Add("Muscle clip present: " + (clip.m_MuscleClip != null));
                    lines.Add("Events: " + (clip.m_Events?.Length ?? 0));
                    break;
                case Shader selectedShader:
                    lines.Add("Shader name: " + (selectedShader.m_ParsedForm?.m_Name ?? selectedShader.m_Name));
                    break;
            }

            if (!(obj is GameObject || obj is Transform || obj is Component || obj is Mesh || obj is Material || obj is Texture2D || obj is AnimationClip || obj is Shader))
            {
                try
                {
                    var dump = obj.Dump();
                    if (!string.IsNullOrWhiteSpace(dump))
                    {
                        lines.Add("");
                        lines.Add("Serialized TypeTree dump:");
                        lines.Add(dump.Length > 16000 ? dump.Substring(0, 16000) + "\r\n[truncated]" : dump);
                    }
                }
                catch (Exception ex) { lines.Add("TypeTree dump unavailable: " + ex.Message); }
            }
            return string.Join(Environment.NewLine, lines);
        }

        private static string DescribeMesh(Mesh mesh)
        {
            var submeshes = mesh.m_SubMeshes ?? Array.Empty<SubMesh>();
            var uv0 = mesh.m_UV0?.Length ?? 0;
            var vertices = mesh.m_Vertices?.Length ?? 0;
            return string.Format("Vertices: {0:N0} · triangles/indices: {1:N0} · submeshes: {2} · normals: {3:N0} floats · UV0: {4:N0} floats · skin weights: {5:N0} · bind poses: {6:N0} · blend-shape channels: {7:N0}",
                mesh.m_VertexCount, mesh.m_Indices?.Count ?? 0, submeshes.Length,
                mesh.m_Normals?.Length ?? 0, uv0, mesh.m_Skin?.Length ?? 0,
                mesh.m_BindPose?.Length ?? 0, mesh.m_Shapes?.channels?.Length ?? 0);
        }

        private static string TextureMetadata(Texture2D texture)
        {
            var streamInfo = texture.m_StreamData == null ? "(embedded image data)" :
                (string.IsNullOrWhiteSpace(texture.m_StreamData.path) ? "(embedded image data)" :
                texture.m_StreamData.path + " · offset " + texture.m_StreamData.offset + " · bytes " + texture.m_StreamData.size);
            return string.Format("Texture2D: {0} × {1}\r\nFormat: {2}\r\nMip count: {3}\r\nReadable mip/image data: {4}\r\nStream: {5}",
                texture.m_Width, texture.m_Height, texture.m_TextureFormat, texture.m_MipCount,
                texture.image_data?.Size ?? 0, streamInfo);
        }

        private static string ResolveTransformName(PPtr<Transform> pointer)
        {
            if (pointer != null && pointer.TryGet(out Transform transform) && transform != null)
            {
                var go = GetGameObject(transform);
                return go?.m_Name ?? "Transform " + transform.m_PathID;
            }
            return "(root / unresolved)";
        }

        private static Transform GetTransform(GameObject gameObject)
        {
            foreach (var component in GetComponents(gameObject))
                if (component is Transform transform) return transform;
            return null;
        }

        private static GameObject GetGameObject(Transform transform)
        {
            try { if (transform?.m_GameObject != null && transform.m_GameObject.TryGet(out GameObject go)) return go; } catch { }
            return null;
        }

        private static IEnumerable<Component> GetComponents(GameObject gameObject)
        {
            foreach (var pointer in gameObject?.m_Components ?? Array.Empty<PPtr<Component>>())
                if (pointer != null && pointer.TryGet(out Component component) && component != null)
                    yield return component;
        }

        private static T TryGet<T>(PPtr<T> pointer) where T : UnityObject
        {
            try { return pointer != null && pointer.TryGet(out T target) ? target : null; }
            catch { return null; }
        }

        private string ObjectTooltip(UnityObject obj)
        {
            if (obj == null) return "";
            var source = _state != null && _state.SourceByFile.TryGetValue(obj.assetsFile, out var path)
                ? System.IO.Path.GetFileName(path) : "(external reference)";
            return obj.GetType().Name + " · " + GetObjectName(obj) + " · Path ID " + obj.m_PathID + " · " + source;
        }

        private static string GetObjectName(UnityObject obj)
        {
            if (obj == null) return "(unresolved)";
            if (obj is GameObject go) return go.m_Name ?? "";
            if (obj is Transform transform)
            {
                var goName = GetGameObject(transform)?.m_Name;
                return string.IsNullOrWhiteSpace(goName) ? "Transform_" + transform.m_PathID : goName;
            }
            if (obj is Animator animator && animator.m_GameObject != null && animator.m_GameObject.TryGet(out GameObject animatorGo) && animatorGo != null)
                return animatorGo.m_Name;
            if (obj is Component component && component.m_GameObject != null && component.m_GameObject.TryGet(out GameObject componentGo) && componentGo != null)
                return component.GetType().Name + " (" + componentGo.m_Name + ")";
            if (obj is Shader shader && shader.m_ParsedForm != null && !string.IsNullOrWhiteSpace(shader.m_ParsedForm.m_Name))
                return shader.m_ParsedForm.m_Name;
            if (obj is NamedObject named && !string.IsNullOrWhiteSpace(named.m_Name)) return named.m_Name;
            return obj.type + "_" + obj.m_PathID;
        }

        private static string ObjectIdentity(UnityObject obj)
        {
            var fileName = obj?.assetsFile?.fullName ?? "";
            var originalPath = obj?.assetsFile?.originalPath ?? "";
            var shortName = obj?.assetsFile?.fileName ?? "?";
            return fileName + "|" + originalPath + "|" + shortName + "|" +
                (obj?.m_PathID.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0");
        }

        private static bool IsHeroSkinBundle(string path)
        {
            var name = System.IO.Path.GetFileName(path ?? "");
            return name.StartsWith("hero_", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith("_skin.unity3d", StringComparison.OrdinalIgnoreCase);
        }

        private static string[] CopyInputs(string[] inputs, string workspace, Dictionary<string, string> workspaceToOriginal)
        {
            var commonRoot = GetCommonDirectory(inputs);
            var destinations = new List<string>();
            for (int i = 0; i < inputs.Length; i++)
            {
                var original = SafeFullPath(inputs[i]);
                var relative = Path.GetRelativePath(commonRoot, original);
                if (Path.IsPathRooted(relative) || relative == ".." ||
                    relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                    relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
                    relative = Path.Combine("_external_inputs", i.ToString("D5"), Path.GetFileName(original));
                var destination = SafeFullPath(Path.Combine(workspace, relative));
                var safeRoot = SafeFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!destination.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Input escaped the temporary workspace.");
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(original, destination, true);
                workspaceToOriginal[destination] = original;
                destinations.Add(destination);
            }
            return destinations.ToArray();
        }

        private static string GetCommonDirectory(string[] files)
        {
            if (files == null || files.Length == 0) return Path.GetTempPath();
            var common = Path.GetDirectoryName(SafeFullPath(files[0]));
            foreach (var file in files.Skip(1))
            {
                var directory = Path.GetDirectoryName(SafeFullPath(file));
                while (!string.IsNullOrWhiteSpace(common) && !DirectoryEqualsOrContains(common, directory))
                    common = Path.GetDirectoryName(common);
            }
            return string.IsNullOrWhiteSpace(common) ? Path.GetPathRoot(SafeFullPath(files[0])) : common;
        }

        private static bool DirectoryEqualsOrContains(string directory, string candidate)
        {
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(candidate)) return false;
            var d = SafeFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var c = SafeFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(d, c, StringComparison.OrdinalIgnoreCase) ||
                c.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static void MergeSplitParts(string workspace)
        {
            foreach (var firstPart in Directory.EnumerateFiles(workspace, "*.split0", SearchOption.AllDirectories).ToArray())
            {
                var stem = firstPart.Substring(0, firstPart.Length - ".split0".Length);
                var parts = Directory.EnumerateFiles(Path.GetDirectoryName(firstPart), Path.GetFileName(stem) + ".split*", SearchOption.TopDirectoryOnly)
                    .Where(path => System.IO.Path.GetExtension(path).StartsWith(".split", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path =>
                    {
                        var ext = System.IO.Path.GetExtension(path);
                        return int.TryParse(ext.Length > 6 ? ext.Substring(6) : "", out int n) ? n : int.MaxValue;
                    }).ToArray();
                if (parts.Length == 0) continue;
                var destination = stem;
                if (File.Exists(destination)) continue;
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    foreach (var part in parts)
                        using (var input = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read))
                            input.CopyTo(output);
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

        private static string SafeFullPath(string path)
        {
            try { return string.IsNullOrWhiteSpace(path) ? "" : Path.GetFullPath(path); }
            catch { return ""; }
        }

        private void Cleanup()
        {
            ClearAppearance();
            if (_state == null) return;
            try { _state.Manager?.Clear(); } catch { }
            try { if (Directory.Exists(_state.Workspace)) Directory.Delete(_state.Workspace, true); } catch { }
            _state = null;
        }
    }
}


namespace AssetStudioGUI
{
    /// <summary>
    /// Best-effort textured 3D preview assembled from Unity's serialized renderers,
    /// mesh UVs, material texture slots and Transform/bone references. It does not
    /// execute Unity's platform-specific MLBB shader programs.
    /// </summary>
    public sealed class JuanchoVisualLabTexturedMeshPreviewForm : Form
    {
        private sealed class TextureImage
        {
            public int Width;
            public int Height;
            public byte[] Bgra = Array.Empty<byte>();
        }

        private sealed class DrawPart
        {
            public string Name = "";
            public string MaterialName = "";
            public NVector3[] Positions = Array.Empty<NVector3>();
            public NVector3[] Normals = Array.Empty<NVector3>();
            public NVector2[] Uvs = Array.Empty<NVector2>();
            public uint[] Indices = Array.Empty<uint>();
            public string TextureKey = "";
            public TextureImage Texture;
            public System.Drawing.Color Tint = System.Drawing.Color.White;
            public int Vao;
            public int PositionBuffer;
            public int NormalBuffer;
            public int UvBuffer;
            public int IndexBuffer;
            public int GlTexture;
        }

        private readonly List<UnityObject> _objects;
        private readonly HashSet<UnityObject> _objectSet;
        private readonly GameObject _focus;
        private readonly List<DrawPart> _parts = new List<DrawPart>();
        private readonly Dictionary<string, TextureImage> _textures = new Dictionary<string, TextureImage>(StringComparer.OrdinalIgnoreCase);
        private readonly OpenTK.GLControl _gl = new OpenTK.GLControl();
        private readonly CheckBox _textureToggle = new CheckBox { Text = "Apply material texture maps", Checked = true, AutoSize = true };
        private readonly CheckBox _wireframe = new CheckBox { Text = "Wireframe", AutoSize = true };
        private readonly Label _status = new Label { AutoSize = true, MaximumSize = new System.Drawing.Size(1200, 0) };
        private readonly System.Windows.Forms.Timer _unusedTimer = new System.Windows.Forms.Timer();
        private bool _glReady;
        private bool _built;
        private bool _leftDrag;
        private bool _panDrag;
        private int _lastX;
        private int _lastY;
        private float _yaw = -0.55f;
        private float _pitch = -0.32f;
        private float _distance = 3.2f;
        private float _panX;
        private float _panY;
        private GlMatrix4 _model = GlMatrix4.Identity;
        private int _program;
        private int _positionLocation;
        private int _normalLocation;
        private int _uvLocation;
        private int _modelLocation;
        private int _viewLocation;
        private int _projectionLocation;
        private int _hasTextureLocation;
        private int _textureLocation;
        private int _tintLocation;

        public JuanchoVisualLabTexturedMeshPreviewForm(IEnumerable<UnityObject> allBundleObjects, GameObject focus)
        {
            _objects = (allBundleObjects ?? Enumerable.Empty<UnityObject>()).Where(x => x != null).ToList();
            _objectSet = new HashSet<UnityObject>(_objects);
            _focus = focus;
            Text = "Juancho MLBB Visual Lab — Textured Model Preview" +
                (focus == null ? " — whole hero skin bundle" : " — " + (focus.m_Name ?? "GameObject"));
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new System.Drawing.Size(800, 560);
            Size = new System.Drawing.Size(1220, 850);
            Font = new System.Drawing.Font("Segoe UI", 9F);

            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(6) };
            var reset = new Button { Text = "Reset View", AutoSize = true };
            reset.Click += (s, e) => ResetView();
            _textureToggle.CheckedChanged += (s, e) => _gl.Invalidate();
            _wireframe.CheckedChanged += (s, e) => _gl.Invalidate();
            toolbar.Controls.Add(reset);
            toolbar.Controls.Add(_textureToggle);
            toolbar.Controls.Add(_wireframe);
            toolbar.Controls.Add(new Label
            {
                Text = "Left drag: rotate · Right/middle drag: pan · Wheel: zoom",
                AutoSize = true,
                Padding = new Padding(6, 7, 0, 0)
            });

            _gl.Dock = DockStyle.Fill;
            _gl.BackColor = System.Drawing.Color.FromArgb(20, 23, 30);
            _gl.Load += Gl_Load;
            _gl.Paint += Gl_Paint;
            _gl.Resize += (s, e) => _gl.Invalidate();
            _gl.MouseDown += Gl_MouseDown;
            _gl.MouseUp += Gl_MouseUp;
            _gl.MouseMove += Gl_MouseMove;
            _gl.MouseWheel += Gl_MouseWheel;
            _gl.DoubleClick += (s, e) => ResetView();
            _status.Dock = DockStyle.Bottom;
            _status.Padding = new Padding(8);
            _status.Text = "Preparing mesh, UV, bone and material links from the parsed hero bundle…";
            Controls.Add(_gl);
            Controls.Add(toolbar);
            Controls.Add(_status);
        }

        private void Gl_Load(object sender, EventArgs e)
        {
            try
            {
                _gl.MakeCurrent();
                if (!_built) BuildScene();
                InitializeGl();
                UploadGeometryAndTextures();
                _glReady = true;
                _gl.Invalidate();
            }
            catch (Exception ex)
            {
                _glReady = false;
                _status.Text = "Textured viewport failed: " + ex.Message;
            }
        }

        private void BuildScene()
        {
            var transformSet = new HashSet<Transform>(_objects.OfType<Transform>());
            var worldCache = new Dictionary<Transform, NMatrix>();
            var worldVisiting = new HashSet<Transform>();
            NMatrix WorldOf(Transform transform)
            {
                if (transform == null) return NMatrix.Identity;
                if (worldCache.TryGetValue(transform, out var cached)) return cached;
                if (!worldVisiting.Add(transform)) return NMatrix.Identity;
                var p = transform.m_LocalPosition;
                var q = transform.m_LocalRotation;
                var sc = transform.m_LocalScale;
                var local = NMatrix.CreateScale(sc.X, sc.Y, sc.Z) *
                    NMatrix.CreateFromQuaternion(new NQuaternion(q.X, q.Y, q.Z, q.W)) *
                    NMatrix.CreateTranslation(p.X, p.Y, p.Z);
                NMatrix world = local;
                try
                {
                    if (transform.m_Father != null && transform.m_Father.TryGet(out Transform parent) &&
                        parent != null && transformSet.Contains(parent))
                        world = local * WorldOf(parent);
                }
                catch { }
                worldVisiting.Remove(transform);
                worldCache[transform] = world;
                return world;
            }

            var transformToGo = new Dictionary<Transform, GameObject>();
            foreach (var go in _objects.OfType<GameObject>())
            {
                var tr = FindTransform(go);
                if (tr != null) transformToGo[tr] = go;
            }

            IEnumerable<GameObject> targets;
            if (_focus != null)
            {
                var found = new List<GameObject>();
                var seen = new HashSet<Transform>();
                var start = FindTransform(_focus);
                if (start != null)
                {
                    var stack = new Stack<Transform>();
                    stack.Push(start);
                    while (stack.Count > 0)
                    {
                        var current = stack.Pop();
                        if (current == null || !seen.Add(current)) continue;
                        var go = GetGameObject(current);
                        if (go != null) found.Add(go);
                        foreach (var childPtr in current.m_Children ?? Array.Empty<PPtr<Transform>>())
                            if (childPtr != null && childPtr.TryGet(out Transform child) && child != null && transformSet.Contains(child))
                                stack.Push(child);
                    }
                }
                else found.Add(_focus);
                targets = found;
            }
            else
            {
                // All GameObjects from the selected hero skin bundle; each renderer appears once.
                targets = _objects.OfType<GameObject>().ToArray();
            }

            var visitedRenderers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var go in targets)
            {
                if (go == null) continue;
                var components = GetComponents(go).ToArray();
                var renderers = components.OfType<SkinnedMeshRenderer>().Cast<Renderer>()
                    .Concat(components.OfType<MeshRenderer>())
                    .Distinct()
                    .ToArray();
                foreach (var renderer in renderers)
                {
                    var renderKey = ObjectIdentity(renderer);
                    if (!visitedRenderers.Add(renderKey)) continue;
                    Mesh mesh = null;
                    SkinnedMeshRenderer skinned = renderer as SkinnedMeshRenderer;
                    if (skinned != null) mesh = TryGet(skinned.m_Mesh);
                    else
                    {
                        var filter = components.OfType<MeshFilter>().FirstOrDefault();
                        if (filter != null) mesh = TryGet(filter.m_Mesh);
                    }
                    if (mesh == null || mesh.m_VertexCount <= 0 || mesh.m_Vertices == null ||
                        mesh.m_Vertices.Length < mesh.m_VertexCount * 3 ||
                        mesh.m_Indices == null || mesh.m_Indices.Count < 3)
                        continue;

                    var transform = FindTransform(go);
                    var nodeWorld = WorldOf(transform);
                    var vertices = ExtractVertices(mesh);
                    var normals = ExtractNormals(mesh, vertices);
                    var uvs = ExtractUvs(mesh);
                    if (skinned != null)
                        ApplySkinning(mesh, skinned, vertices, normals, nodeWorld, WorldOf);
                    else
                    {
                        for (int v = 0; v < vertices.Length; v++)
                        {
                            vertices[v] = NVector3.Transform(vertices[v], nodeWorld);
                            normals[v] = NormalizeSafe(NVector3.TransformNormal(normals[v], nodeWorld));
                        }
                    }

                    var materials = ResolveMaterials(renderer);
                    var submeshes = mesh.m_SubMeshes ?? Array.Empty<SubMesh>();
                    if (submeshes.Length == 0)
                    {
                        var material = materials.FirstOrDefault();
                        AddDrawPart(mesh.m_Name, material, vertices, normals, uvs,
                            mesh.m_Indices.Where(x => x < vertices.Length).ToArray(), null, NVector2.One, NVector2.Zero);
                        continue;
                    }

                    int indexOffset = 0;
                    bool rangesValid = submeshes.All(sub => sub.indexCount <= int.MaxValue) &&
                        submeshes.Sum(sub => (long)sub.indexCount) == mesh.m_Indices.Count;
                    if (!rangesValid)
                    {
                        var material = materials.FirstOrDefault();
                        AddDrawPart(mesh.m_Name, material, vertices, normals, uvs,
                            mesh.m_Indices.Where(x => x < vertices.Length).ToArray(), null, NVector2.One, NVector2.Zero);
                        continue;
                    }

                    for (int slot = 0; slot < submeshes.Length; slot++)
                    {
                        var sub = submeshes[slot];
                        int count = (int)sub.indexCount;
                        if (count < 3 || indexOffset + count > mesh.m_Indices.Count)
                        {
                            indexOffset += count;
                            continue;
                        }
                        var indices = mesh.m_Indices.Skip(indexOffset).Take(count).Where(x => x < vertices.Length).ToArray();
                        indexOffset += count;
                        var material = slot < materials.Count ? materials[slot] : materials.FirstOrDefault();
                        AddDrawPart(mesh.m_Name, material, vertices, normals, uvs, indices, null, NVector2.One, NVector2.Zero);
                    }
                }
            }

            if (_parts.Count == 0)
                throw new InvalidDataException("No drawable MeshFilter or SkinnedMeshRenderer parts were found in the chosen hierarchy. Expand the prefab tree, select a model GameObject, or verify that the hero skin bundle contains its Mesh objects.");

            var min = new NVector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            var max = new NVector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            foreach (var part in _parts)
                foreach (var p in part.Positions)
                {
                    min = NVector3.Min(min, p);
                    max = NVector3.Max(max, p);
                }
            var center = (min + max) * 0.5f;
            var size = max - min;
            var diagonal = Math.Max(size.X, Math.Max(size.Y, size.Z));
            var scale = 2.0f / Math.Max(diagonal, 0.00001f);
            _model = GlMatrix4.CreateTranslation(-center.X, -center.Y, -center.Z) * GlMatrix4.CreateScale(scale);

            int textured = _parts.Count(x => !string.IsNullOrWhiteSpace(x.TextureKey));
            int distinctTextures = _textures.Count;
            _status.Text = string.Format(
                "Draw parts: {0:N0} · textured material slots: {1:N0} · decoded textures: {2:N0} · {3}",
                _parts.Count, textured, distinctTextures,
                _focus == null ? "whole hero skin bundle" : "selected GameObject hierarchy") +
                " · diagnostic fallback for missing/unsupported textures; original Unity shaders are not executed.";
            _built = true;
        }

        private void AddDrawPart(string meshName, Material material, NVector3[] positions, NVector3[] normals,
            NVector2[] uvs, uint[] indices, string unusedTextureKey, NVector2 unusedScale, NVector2 unusedOffset)
        {
            if (indices == null || indices.Length < 3) return;
            string textureKey = "";
            TextureImage image = null;
            var uvScale = NVector2.One;
            var uvOffset = NVector2.Zero;
            var tint = System.Drawing.Color.White;
            if (material != null)
            {
                var colors = material.m_SavedProperties?.m_Colors ?? Array.Empty<KeyValuePair<string, AssetStudio.Color>>();
                var color = colors.FirstOrDefault(x => string.Equals(x.Key, "_Color", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x.Key, "_BaseColor", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(color.Key))
                    tint = System.Drawing.Color.FromArgb(ToByte(color.Value.A), ToByte(color.Value.R), ToByte(color.Value.G), ToByte(color.Value.B));

                var textureSlots = material.m_SavedProperties?.m_TexEnvs ?? Array.Empty<KeyValuePair<string, UnityTexEnv>>();
                var orderedSlots = textureSlots
                    .OrderByDescending(slot => IsMainTextureProperty(slot.Key))
                    .ThenBy(slot => slot.Key, StringComparer.OrdinalIgnoreCase);
                foreach (var slot in orderedSlots)
                {
                    Texture texture = null;
                    try { slot.Value?.m_Texture?.TryGet(out texture); } catch { }
                    if (!(texture is Texture2D texture2D)) continue;
                    textureKey = ObjectIdentity(texture2D);
                    uvScale = slot.Value == null ? NVector2.One :
                        new NVector2(slot.Value.m_Scale.X, slot.Value.m_Scale.Y);
                    uvOffset = slot.Value == null ? NVector2.Zero :
                        new NVector2(slot.Value.m_Offset.X, slot.Value.m_Offset.Y);
                    if (!_textures.TryGetValue(textureKey, out image))
                    {
                        image = DecodeTexture(texture2D);
                        if (image != null) _textures[textureKey] = image;
                    }
                    if (image != null) break;
                    textureKey = "";
                }
            }

            var adjustedUvs = new NVector2[uvs.Length];
            for (int i = 0; i < uvs.Length; i++)
                adjustedUvs[i] = new NVector2(uvs[i].X * uvScale.X + uvOffset.X, uvs[i].Y * uvScale.Y + uvOffset.Y);

            _parts.Add(new DrawPart
            {
                Name = meshName ?? "(mesh)",
                MaterialName = material?.m_Name ?? "(no material)",
                Positions = positions,
                Normals = normals,
                Uvs = adjustedUvs,
                Indices = indices,
                TextureKey = textureKey,
                Texture = image,
                Tint = tint
            });
        }

        private static bool IsMainTextureProperty(string key)
        {
            return string.Equals(key, "_MainTex", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "_BaseMap", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "_BaseColorMap", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "_BaseColorTexture", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "MainTex", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "BaseMap", StringComparison.OrdinalIgnoreCase);
        }

        private static byte ToByte(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 255;
            return (byte)Math.Max(0, Math.Min(255, (int)Math.Round(value * 255f)));
        }

        private static TextureImage DecodeTexture(Texture2D texture)
        {
            try
            {
                using (var image = texture.ConvertToImage(true))
                {
                    if (image == null) return null;
                    var bytes = image.ConvertToBytes();
                    if (bytes == null || bytes.Length < texture.m_Width * texture.m_Height * 4) return null;
                    return new TextureImage { Width = texture.m_Width, Height = texture.m_Height, Bgra = bytes };
                }
            }
            catch { return null; }
        }

        private static NVector3[] ExtractVertices(Mesh mesh)
        {
            var result = new NVector3[mesh.m_VertexCount];
            int stride = mesh.m_Vertices.Length / mesh.m_VertexCount;
            if (stride < 3) throw new InvalidDataException("Mesh vertex data has an invalid stride.");
            for (int i = 0; i < result.Length; i++)
                result[i] = new NVector3(mesh.m_Vertices[i * stride], mesh.m_Vertices[i * stride + 1], mesh.m_Vertices[i * stride + 2]);
            return result;
        }

        private static NVector3[] ExtractNormals(Mesh mesh, NVector3[] vertices)
        {
            var source = mesh.m_Normals;
            var count = vertices.Length;
            if (source != null && source.Length >= count * 3)
            {
                int stride = source.Length / count;
                var result = new NVector3[count];
                for (int i = 0; i < count; i++)
                    result[i] = NormalizeSafe(new NVector3(source[i * stride], source[i * stride + 1], source[i * stride + 2]));
                return result;
            }

            var normals = new NVector3[count];
            foreach (var indices in GetAllIndices(mesh))
            {
                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    int a = (int)indices[i], b = (int)indices[i + 1], c = (int)indices[i + 2];
                    if (a >= count || b >= count || c >= count) continue;
                    var n = NVector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
                    normals[a] += n; normals[b] += n; normals[c] += n;
                }
            }
            for (int i = 0; i < normals.Length; i++) normals[i] = NormalizeSafe(normals[i]);
            return normals;
        }

        private static uint[][] GetAllIndices(Mesh mesh)
        {
            var result = new List<uint[]>();
            if (mesh.m_SubMeshes != null && mesh.m_SubMeshes.Length > 0)
            {
                int offset = 0;
                foreach (var sub in mesh.m_SubMeshes)
                {
                    int count = (int)Math.Min(int.MaxValue, sub.indexCount);
                    if (count >= 0 && offset + count <= mesh.m_Indices.Count)
                        result.Add(mesh.m_Indices.Skip(offset).Take(count).ToArray());
                    offset += Math.Max(0, count);
                }
            }
            else result.Add(mesh.m_Indices.ToArray());
            return result.ToArray();
        }

        private static NVector2[] ExtractUvs(Mesh mesh)
        {
            int count = mesh.m_VertexCount;
            var source = mesh.m_UV0;
            var result = new NVector2[count];
            if (source == null || source.Length < count * 2) return result;
            int stride = source.Length / count;
            if (stride < 2) return result;
            for (int i = 0; i < count; i++)
            {
                float u = source[i * stride], v = source[i * stride + 1];
                result[i] = new NVector2(u, v);
            }
            return result;
        }

        private static void ApplySkinning(Mesh mesh, SkinnedMeshRenderer renderer, NVector3[] positions,
            NVector3[] normals, NMatrix rendererWorld, Func<Transform, NMatrix> worldOf)
        {
            var skin = mesh.m_Skin;
            var bindPoses = mesh.m_BindPose;
            var bones = renderer.m_Bones ?? Array.Empty<PPtr<Transform>>();
            if (skin == null || skin.Length != positions.Length || bindPoses == null || bindPoses.Length == 0 || bones.Length == 0)
            {
                for (int i = 0; i < positions.Length; i++)
                {
                    positions[i] = NVector3.Transform(positions[i], rendererWorld);
                    normals[i] = NormalizeSafe(NVector3.TransformNormal(normals[i], rendererWorld));
                }
                return;
            }

            for (int vertex = 0; vertex < positions.Length; vertex++)
            {
                var original = positions[vertex];
                var originalNormal = normals[vertex];
                NVector3 p = NVector3.Zero;
                NVector3 n = NVector3.Zero;
                float total = 0;
                for (int k = 0; k < 4; k++)
                {
                    var weight = skin[vertex].weight[k];
                    int boneIndex = skin[vertex].boneIndex[k];
                    if (!IsFinite(weight) || weight <= 0 || boneIndex < 0 || boneIndex >= bindPoses.Length || boneIndex >= bones.Length)
                        continue;
                    Transform bone = null;
                    try { bones[boneIndex]?.TryGet(out bone); } catch { }
                    if (bone == null) continue;
                    var matrix = ToNumerics(bindPoses[boneIndex]) * worldOf(bone);
                    p += NVector3.Transform(original, matrix) * weight;
                    n += NVector3.TransformNormal(originalNormal, matrix) * weight;
                    total += weight;
                }
                if (total > 0.0001f)
                {
                    positions[vertex] = p / total;
                    normals[vertex] = NormalizeSafe(n / total);
                }
                else
                {
                    positions[vertex] = NVector3.Transform(original, rendererWorld);
                    normals[vertex] = NormalizeSafe(NVector3.TransformNormal(originalNormal, rendererWorld));
                }
            }
        }

        private static NMatrix ToNumerics(AssetStudio.Matrix4x4 m) =>
            new NMatrix(m.M00, m.M10, m.M20, m.M30, m.M01, m.M11, m.M21, m.M31,
                m.M02, m.M12, m.M22, m.M32, m.M03, m.M13, m.M23, m.M33);

        private static NVector3 NormalizeSafe(NVector3 value)
        {
            var length = value.LengthSquared();
            return IsFinite(length) && length > 1e-10f ? NVector3.Normalize(value) : NVector3.UnitY;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static Transform FindTransform(GameObject gameObject)
        {
            foreach (var component in GetComponents(gameObject))
                if (component is Transform transform) return transform;
            return null;
        }

        private static GameObject GetGameObject(Transform transform)
        {
            try { if (transform?.m_GameObject != null && transform.m_GameObject.TryGet(out GameObject go)) return go; } catch { }
            return null;
        }

        private static IEnumerable<Component> GetComponents(GameObject gameObject)
        {
            foreach (var pointer in gameObject?.m_Components ?? Array.Empty<PPtr<Component>>())
                if (pointer != null && pointer.TryGet(out Component component) && component != null)
                    yield return component;
        }

        private static List<Material> ResolveMaterials(Renderer renderer)
        {
            var result = new List<Material>();
            foreach (var pointer in renderer.m_Materials ?? Array.Empty<PPtr<Material>>())
                try { if (pointer != null && pointer.TryGet(out Material material) && material != null) result.Add(material); } catch { }
            return result;
        }

        private static T TryGet<T>(PPtr<T> pointer) where T : UnityObject
        {
            try { return pointer != null && pointer.TryGet(out T target) ? target : null; }
            catch { return null; }
        }

        private string ObjectIdentity(UnityObject obj) =>
            (obj?.assetsFile?.fullName ?? "") + "|" + (obj?.assetsFile?.originalPath ?? "") + "|" +
            (obj?.assetsFile?.fileName ?? "?") + "|" + (obj?.m_PathID.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0");

        private static void ConfigureGlDefaults()
        {
            GL.ClearColor(System.Drawing.Color.FromArgb(20, 23, 30));
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Lequal);
            GL.Disable(EnableCap.CullFace);
        }

        private void InitializeGl()
        {
            ConfigureGlDefaults();
            const string vertexSource = @"#version 330 core
in vec3 vertexPosition;
in vec3 normalDirection;
in vec2 uvCoordinate;
uniform mat4 modelMatrix;
uniform mat4 viewMatrix;
uniform mat4 projMatrix;
out vec3 fragNormal;
out vec2 fragUv;
void main()
{
    gl_Position = projMatrix * viewMatrix * modelMatrix * vec4(vertexPosition, 1.0);
    fragNormal = normalize(mat3(modelMatrix) * normalDirection);
    fragUv = uvCoordinate;
}";
            const string fragmentSource = @"#version 330 core
in vec3 fragNormal;
in vec2 fragUv;
uniform sampler2D diffuseMap;
uniform int hasTexture;
uniform vec4 tintColor;
out vec4 outputColor;
void main()
{
    vec3 n = normalize(fragNormal);
    vec3 lightDir = normalize(vec3(-0.45, 0.78, 0.61));
    float diffuse = max(dot(n, lightDir), 0.0);
    float light = 0.28 + diffuse * 0.72;
    vec4 base = hasTexture != 0 ? texture(diffuseMap, fragUv) : vec4(0.69, 0.75, 0.84, 1.0);
    outputColor = vec4(base.rgb * tintColor.rgb * light, base.a * tintColor.a);
}";
            var vertex = CompileShader(ShaderType.VertexShader, vertexSource);
            var fragment = CompileShader(ShaderType.FragmentShader, fragmentSource);
            _program = GL.CreateProgram();
            GL.AttachShader(_program, vertex);
            GL.AttachShader(_program, fragment);
            GL.LinkProgram(_program);
            GL.GetProgram(_program, GetProgramParameterName.LinkStatus, out var linked);
            var log = GL.GetProgramInfoLog(_program);
            GL.DeleteShader(vertex);
            GL.DeleteShader(fragment);
            if (linked == 0) throw new InvalidOperationException("Textured material shader link failed: " + log);

            _positionLocation = GL.GetAttribLocation(_program, "vertexPosition");
            _normalLocation = GL.GetAttribLocation(_program, "normalDirection");
            _uvLocation = GL.GetAttribLocation(_program, "uvCoordinate");
            _modelLocation = GL.GetUniformLocation(_program, "modelMatrix");
            _viewLocation = GL.GetUniformLocation(_program, "viewMatrix");
            _projectionLocation = GL.GetUniformLocation(_program, "projMatrix");
            _hasTextureLocation = GL.GetUniformLocation(_program, "hasTexture");
            _textureLocation = GL.GetUniformLocation(_program, "diffuseMap");
            _tintLocation = GL.GetUniformLocation(_program, "tintColor");
        }

        private static int CompileShader(ShaderType type, string source)
        {
            var shader = GL.CreateShader(type);
            GL.ShaderSource(shader, source);
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out var compiled);
            var log = GL.GetShaderInfoLog(shader);
            if (compiled == 0)
            {
                GL.DeleteShader(shader);
                throw new InvalidOperationException(type + " shader failed to compile: " + log);
            }
            return shader;
        }

        private void UploadGeometryAndTextures()
        {
            foreach (var part in _parts)
            {
                GL.GenVertexArrays(1, out part.Vao);
                GL.BindVertexArray(part.Vao);

                GL.GenBuffers(1, out part.PositionBuffer);
                GL.BindBuffer(BufferTarget.ArrayBuffer, part.PositionBuffer);
                var p = part.Positions.Select(v => new GlVector3(v.X, v.Y, v.Z)).ToArray();
                GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(p.Length * GlVector3.SizeInBytes), p, BufferUsageHint.StaticDraw);
                GL.VertexAttribPointer(_positionLocation, 3, VertexAttribPointerType.Float, false, 0, IntPtr.Zero);
                GL.EnableVertexAttribArray(_positionLocation);

                GL.GenBuffers(1, out part.NormalBuffer);
                GL.BindBuffer(BufferTarget.ArrayBuffer, part.NormalBuffer);
                var n = part.Normals.Select(v => new GlVector3(v.X, v.Y, v.Z)).ToArray();
                GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(n.Length * GlVector3.SizeInBytes), n, BufferUsageHint.StaticDraw);
                GL.VertexAttribPointer(_normalLocation, 3, VertexAttribPointerType.Float, false, 0, IntPtr.Zero);
                GL.EnableVertexAttribArray(_normalLocation);

                GL.GenBuffers(1, out part.UvBuffer);
                GL.BindBuffer(BufferTarget.ArrayBuffer, part.UvBuffer);
                var uv = part.Uvs.Select(v => new OpenTK.Mathematics.Vector2(v.X, v.Y)).ToArray();
                GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(uv.Length * OpenTK.Mathematics.Vector2.SizeInBytes), uv, BufferUsageHint.StaticDraw);
                GL.VertexAttribPointer(_uvLocation, 2, VertexAttribPointerType.Float, false, 0, IntPtr.Zero);
                GL.EnableVertexAttribArray(_uvLocation);

                GL.GenBuffers(1, out part.IndexBuffer);
                GL.BindBuffer(BufferTarget.ElementArrayBuffer, part.IndexBuffer);
                GL.BufferData(BufferTarget.ElementArrayBuffer, (IntPtr)(part.Indices.Length * sizeof(uint)), part.Indices, BufferUsageHint.StaticDraw);

                if (part.Texture != null && part.Texture.Width > 0 && part.Texture.Height > 0)
                {
                    GL.GenTextures(1, out part.GlTexture);
                    GL.BindTexture(TextureTarget.Texture2D, part.GlTexture);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
                    var handle = GCHandle.Alloc(part.Texture.Bgra, GCHandleType.Pinned);
                    try
                    {
                        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                            part.Texture.Width, part.Texture.Height, 0, PixelFormat.Bgra,
                            PixelType.UnsignedByte, handle.AddrOfPinnedObject());
                    }
                    finally { handle.Free(); }
                }

                GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
                GL.BindVertexArray(0);
            }
        }

        private void Gl_Paint(object sender, PaintEventArgs e)
        {
            if (!_glReady || _gl.IsDisposed || _gl.ClientSize.Width <= 0 || _gl.ClientSize.Height <= 0) return;
            try
            {
                _gl.MakeCurrent();
                GL.Viewport(0, 0, _gl.ClientSize.Width, _gl.ClientSize.Height);
                GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                GL.UseProgram(_program);
                var view = GlMatrix4.CreateTranslation(_panX, _panY, -_distance) *
                    GlMatrix4.CreateRotationX(_pitch) * GlMatrix4.CreateRotationY(_yaw);
                var aspect = Math.Max(0.01f, (float)_gl.ClientSize.Width / _gl.ClientSize.Height);
                var projection = GlMatrix4.CreatePerspectiveFieldOfView(GlMathHelper.DegreesToRadians(42f), aspect, 0.01f, 100f);
                GL.UniformMatrix4(_modelLocation, false, ref _model);
                GL.UniformMatrix4(_viewLocation, false, ref view);
                GL.UniformMatrix4(_projectionLocation, false, ref projection);
                GL.Uniform1(_textureLocation, 0);

                foreach (var part in _parts)
                {
                    GL.BindVertexArray(part.Vao);
                    bool useTexture = _textureToggle.Checked && part.GlTexture != 0;
                    GL.Uniform1(_hasTextureLocation, useTexture ? 1 : 0);
                    GL.Uniform4(_tintLocation, part.Tint.R / 255f, part.Tint.G / 255f, part.Tint.B / 255f, part.Tint.A / 255f);
                    if (useTexture)
                    {
                        GL.ActiveTexture(TextureUnit.Texture0);
                        GL.BindTexture(TextureTarget.Texture2D, part.GlTexture);
                    }
                    GL.PolygonMode(MaterialFace.FrontAndBack, _wireframe.Checked ? PolygonMode.Line : PolygonMode.Fill);
                    GL.DrawElements(BeginMode.Triangles, part.Indices.Length, DrawElementsType.UnsignedInt, IntPtr.Zero);
                }
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
                GL.BindVertexArray(0);
                GL.BindTexture(TextureTarget.Texture2D, 0);
                GL.UseProgram(0);
                _gl.SwapBuffers();
            }
            catch (Exception ex) { _status.Text = "Viewport draw failed: " + ex.Message; }
        }

        private void Gl_MouseDown(object sender, MouseEventArgs e)
        {
            _gl.Focus();
            _lastX = e.X; _lastY = e.Y;
            _leftDrag = e.Button == MouseButtons.Left;
            _panDrag = e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle;
        }

        private void Gl_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) _leftDrag = false;
            if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle) _panDrag = false;
        }

        private void Gl_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_leftDrag && !_panDrag) return;
            var dx = e.X - _lastX; var dy = e.Y - _lastY;
            _lastX = e.X; _lastY = e.Y;
            if (_leftDrag)
            {
                _yaw += dx * 0.01f;
                _pitch = Math.Max(-1.52f, Math.Min(1.52f, _pitch + dy * 0.01f));
            }
            else
            {
                var pan = _distance * 0.0017f;
                _panX += dx * pan;
                _panY -= dy * pan;
            }
            _gl.Invalidate();
        }

        private void Gl_MouseWheel(object sender, MouseEventArgs e)
        {
            _distance *= e.Delta > 0 ? 0.88f : 1.14f;
            _distance = Math.Max(0.35f, Math.Min(50f, _distance));
            _gl.Invalidate();
        }

        private void ResetView()
        {
            _yaw = -0.55f; _pitch = -0.32f; _distance = 3.2f; _panX = _panY = 0;
            _gl.Invalidate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try
            {
                if (_glReady && !_gl.IsDisposed)
                {
                    _gl.MakeCurrent();
                    foreach (var part in _parts)
                    {
                        if (part.GlTexture != 0) GL.DeleteTexture(part.GlTexture);
                        if (part.IndexBuffer != 0) GL.DeleteBuffer(part.IndexBuffer);
                        if (part.UvBuffer != 0) GL.DeleteBuffer(part.UvBuffer);
                        if (part.NormalBuffer != 0) GL.DeleteBuffer(part.NormalBuffer);
                        if (part.PositionBuffer != 0) GL.DeleteBuffer(part.PositionBuffer);
                        if (part.Vao != 0) GL.DeleteVertexArray(part.Vao);
                    }
                    if (_program != 0) GL.DeleteProgram(_program);
                }
            }
            catch { }
            _unusedTimer.Dispose();
            _glReady = false;
            base.OnFormClosing(e);
        }
    }
}
