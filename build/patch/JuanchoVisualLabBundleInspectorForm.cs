using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using AssetStudio;
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
            Font = new Font("Segoe UI", 9F);
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
            _details.Font = new Font("Consolas", 9F);
            _texturePreview.Dock = DockStyle.Fill;
            _texturePreview.SizeMode = PictureBoxSizeMode.Zoom;
            _texturePreview.BackColor = Color.FromArgb(38, 38, 42);
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
                    var gameObjects = _bundleObjects.OfType<GameObject>().ToArray();
                    foreach (var go in gameObjects)
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
                var go = GetGameObject(component);
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
                var picture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(38, 38, 42) };
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
                    lines.Add("High quality curve: " + clip.m_HighQualityCurve);
                    lines.Add("Sample rate: " + clip.m_SampleRate);
                    lines.Add("Muscle clip present: " + (clip.m_MuscleClip != null));
                    lines.Add("Events: " + (clip.m_Events?.Length ?? 0));
                    break;
                case Shader shader:
                    lines.Add("Shader name: " + (shader.m_ParsedForm?.m_Name ?? shader.m_Name));
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
