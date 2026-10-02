$ErrorActionPreference = 'Stop'

$repo = Join-Path $env:GITHUB_WORKSPACE 'AssetStudio'
$gui = Join-Path $repo 'AssetStudioGUI'
$csproj = Join-Path $gui 'AssetStudioGUI.csproj'
$form = Join-Path $gui 'AssetStudioGUIForm.cs'
$designer = Join-Path $gui 'AssetStudioGUIForm.Designer.cs'
$helperSource = Join-Path $env:GITHUB_WORKSPACE 'build\patch\JuanchoTextureReplacer.cs'
$helperDest = Join-Path $gui 'JuanchoTextureReplacer.cs'
$dialogSource = Join-Path $env:GITHUB_WORKSPACE 'build\patch\JuanchoTextureSettingsDialog.cs'
$dialogDest = Join-Path $gui 'JuanchoTextureSettingsDialog.cs'

Copy-Item $helperSource $helperDest -Force
Copy-Item $dialogSource $dialogDest -Force

$projText = Get-Content $csproj -Raw
$projText = $projText.Replace('<TargetFrameworks>net472;net5.0-windows;net6.0-windows</TargetFrameworks>', '<TargetFrameworks>net8.0-windows</TargetFrameworks>')
if ($projText -notmatch 'AssetsTools\.NET"') {
    $tab = [char]9
    $marker = $tab + $tab + '<PackageReference Include="Newtonsoft.Json" Version="13.0.1" />'
    if (-not $projText.Contains($marker)) { throw "Could not find Newtonsoft.Json PackageReference marker." }
    $projText = $projText.Replace(
        $marker,
        $marker + [Environment]::NewLine + $tab + $tab + '<PackageReference Include="AssetsTools.NET" Version="3.0.5" />' + [Environment]::NewLine + $tab + $tab + '<PackageReference Include="AssetsTools.NET.Texture" Version="3.0.2" />' + [Environment]::NewLine + $tab + $tab + '<PackageReference Include="StbImageSharp" Version="2.30.16" />'
    )
    Set-Content $csproj $projText -Encoding UTF8
}

$designerText = Get-Content $designer -Raw
if ($designerText -notmatch 'juanchoToolStripMenuItem') {
    $designerText = $designerText.Replace(
        "            this.fileToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();",
        "            this.fileToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();" + [Environment]::NewLine + "            this.juanchoToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();" + [Environment]::NewLine + "            this.replaceSelectedTextureToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();"
    )
    $designerText = $designerText.Replace(
        "            this.fileToolStripMenuItem," + [Environment]::NewLine + "            this.optionsToolStripMenuItem,",
        "            this.fileToolStripMenuItem," + [Environment]::NewLine + "            this.juanchoToolStripMenuItem," + [Environment]::NewLine + "            this.optionsToolStripMenuItem,"
    )

    $oldOptions = "            // " + [Environment]::NewLine + "            // optionsToolStripMenuItem"
    $juanchoBlock = "            // " + [Environment]::NewLine +
"            // juanchoToolStripMenuItem" + [Environment]::NewLine +
"            // " + [Environment]::NewLine +
"            this.juanchoToolStripMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {" + [Environment]::NewLine +
"            this.replaceSelectedTextureToolStripMenuItem});" + [Environment]::NewLine +
"            this.juanchoToolStripMenuItem.Name = ""juanchoToolStripMenuItem"";" + [Environment]::NewLine +
"            this.juanchoToolStripMenuItem.Size = new System.Drawing.Size(65, 21);" + [Environment]::NewLine +
"            this.juanchoToolStripMenuItem.Text = ""Juancho"";" + [Environment]::NewLine +
"            // " + [Environment]::NewLine +
"            // replaceSelectedTextureToolStripMenuItem" + [Environment]::NewLine +
"            // " + [Environment]::NewLine +
"            this.replaceSelectedTextureToolStripMenuItem.Name = ""replaceSelectedTextureToolStripMenuItem"";" + [Environment]::NewLine +
"            this.replaceSelectedTextureToolStripMenuItem.Size = new System.Drawing.Size(230, 22);" + [Environment]::NewLine +
"            this.replaceSelectedTextureToolStripMenuItem.Text = ""Replace Selected Texture2D"";" + [Environment]::NewLine +
"            this.replaceSelectedTextureToolStripMenuItem.Click += new System.EventHandler(this.replaceSelectedTextureToolStripMenuItem_Click);" + [Environment]::NewLine +
"            // " + [Environment]::NewLine +
"            // optionsToolStripMenuItem"
    $designerText = $designerText.Replace($oldOptions, $juanchoBlock)

    $fieldOld = "        private System.Windows.Forms.ToolStripMenuItem fileToolStripMenuItem;" + [Environment]::NewLine + "        private System.Windows.Forms.SplitContainer splitContainer1;"
    $fieldNew = "        private System.Windows.Forms.ToolStripMenuItem fileToolStripMenuItem;" + [Environment]::NewLine + "        private System.Windows.Forms.ToolStripMenuItem juanchoToolStripMenuItem;" + [Environment]::NewLine + "        private System.Windows.Forms.ToolStripMenuItem replaceSelectedTextureToolStripMenuItem;" + [Environment]::NewLine + "        private System.Windows.Forms.SplitContainer splitContainer1;"
    $designerText = $designerText.Replace($fieldOld, $fieldNew)

    if ($designerText -notmatch [regex]::Escape('juanchoToolStripMenuItem')) { throw "Failed to patch Designer.cs" }
    Set-Content $designer $designerText -Encoding UTF8
}

$formText = Get-Content $form -Raw
if ($formText -notmatch 'replaceSelectedTextureToolStripMenuItem_Click') {
    $nl = [Environment]::NewLine
    $handler =
"        private async void replaceSelectedTextureToolStripMenuItem_Click(object sender, EventArgs e)" + $nl +
"        {" + $nl +
"            var selectedAssets = GetSelectedAssets();" + $nl +
"            if (selectedAssets.Count != 1 || selectedAssets[0].Type != ClassIDType.Texture2D)" + $nl +
"            {" + $nl +
"                MessageBox.Show(this, ""Select exactly one Texture2D in the asset list first."", ""Juancho"", MessageBoxButtons.OK, MessageBoxIcon.Information);" + $nl +
"                return;" + $nl +
"            }" + $nl + $nl +
"            AssetItem selectedAsset = selectedAssets[0];" + $nl +
"            var sourceTexture = selectedAsset.Asset as Texture2D;" + $nl +
"            if (sourceTexture == null)" + $nl +
"            {" + $nl +
"                MessageBox.Show(this, ""The selected Texture2D data is not loaded yet."", ""Juancho"", MessageBoxButtons.OK, MessageBoxIcon.Warning);" + $nl +
"                return;" + $nl +
"            }" + $nl + $nl +
"            using (var imageDialog = new OpenFileDialog())" + $nl +
"            {" + $nl +
"                imageDialog.Title = ""Choose replacement texture"";" + $nl +
"                imageDialog.Filter = ""Image files|*.png;*.jpg;*.jpeg;*.bmp;*.tga|PNG|*.png|JPEG|*.jpg;*.jpeg|Bitmap|*.bmp|TGA|*.tga|All files|*.*"";" + $nl +
"                imageDialog.RestoreDirectory = true;" + $nl +
"                if (imageDialog.ShowDialog(this) != DialogResult.OK) return;" + $nl + $nl +
"                JuanchoTextureSettings settings;" + $nl +
"                using (var settingsDialog = new JuanchoTextureSettingsDialog(selectedAsset.Text, imageDialog.FileName, sourceTexture))" + $nl +
"                {" + $nl +
"                    if (settingsDialog.ShowDialog(this) != DialogResult.OK) return;" + $nl +
"                    settings = settingsDialog.Settings;" + $nl +
"                }" + $nl + $nl +
"                string sourcePath = string.IsNullOrWhiteSpace(selectedAsset.SourceFile.originalPath) ? selectedAsset.SourceFile.fullName : selectedAsset.SourceFile.originalPath;" + $nl +
"                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))" + $nl +
"                {" + $nl +
"                    MessageBox.Show(this, ""The opened Unity file could not be found on disk."", ""Juancho"", MessageBoxButtons.OK, MessageBoxIcon.Error);" + $nl +
"                    return;" + $nl +
"                }" + $nl + $nl +
"                sourcePath = Path.GetFullPath(sourcePath);" + $nl +
"                string backupPath = sourcePath + "".bak"";" + $nl +
"                var confirm = MessageBox.Show(" + $nl +
"                    this," + $nl +
"                    ""Save will replace the selected Texture2D directly inside the Unity file that is currently open."" + Environment.NewLine + Environment.NewLine +" + $nl +
"                    ""File:"" + Environment.NewLine + sourcePath + Environment.NewLine + Environment.NewLine +" + $nl +
"                    ""A backup of the current file will be kept as:"" + Environment.NewLine + backupPath + Environment.NewLine + Environment.NewLine +" + $nl +
"                    ""Continue?""," + $nl +
"                    ""Juancho - Replace Texture2D""," + $nl +
"                    MessageBoxButtons.YesNo," + $nl +
"                    MessageBoxIcon.Warning);" + $nl + $nl +
"                if (confirm != DialogResult.Yes) return;" + $nl + $nl +
"                string[] reloadPaths = assetsManager.assetsFileList" + $nl +
"                    .Select(x => string.IsNullOrWhiteSpace(x.originalPath) ? x.fullName : x.originalPath)" + $nl +
"                    .Where(File.Exists)" + $nl +
"                    .Select(Path.GetFullPath)" + $nl +
"                    .Distinct(StringComparer.OrdinalIgnoreCase)" + $nl +
"                    .ToArray();" + $nl + $nl +
"                replaceSelectedTextureToolStripMenuItem.Enabled = false;" + $nl +
"                StatusStripUpdate(""Juancho: preparing in-place Texture2D replacement..."");" + $nl +
"                try" + $nl +
"                {" + $nl +
"                    // Release AssetStudio's open file handles before replacing the file on disk." + $nl +
"                    ResetForm();" + $nl + $nl +
"                    StatusStripUpdate(""Juancho: replacing selected Texture2D..."");" + $nl +
"                    JuanchoReplacementResult result = await Task.Run(() =>" + $nl +
"                        JuanchoTextureReplacer.ReplaceTexture(" + $nl +
"                            sourcePath," + $nl +
"                            selectedAsset.m_PathID," + $nl +
"                            selectedAsset.Text," + $nl +
"                            imageDialog.FileName," + $nl +
"                            settings));" + $nl + $nl +
"                    StatusStripUpdate(""Juancho: Texture2D replacement finished. Reloading..."");" + $nl +
"                    assetsManager.SpecifyUnityVersion = specifyUnityVersion.Text;" + $nl +
"                    await Task.Run(() => assetsManager.LoadFiles(reloadPaths.Length > 0 ? reloadPaths : new[] { sourcePath }));" + $nl +
"                    await BuildAssetStructuresAndSelectAsync(selectedAsset.m_PathID, selectedAsset.Text);" + $nl +
"                    await ForceJuanchoSavedTexturePreviewAsync(sourcePath, selectedAsset.m_PathID, selectedAsset.Text, specifyUnityVersion.Text);" + $nl + $nl +
"                    string actual = $""{result.Width} × {result.Height}, {(AssetsTools.NET.Texture.TextureFormat)result.Format}, {result.MipCount} mip(s)"";" + $nl +
"                    MessageBox.Show(this," + $nl +
"                        ""Texture2D replaced and verified successfully in the opened Unity file."" + Environment.NewLine + Environment.NewLine +" + $nl +
"                        ""Final Texture2D settings:"" + Environment.NewLine + actual + Environment.NewLine + Environment.NewLine +" + $nl +
"                        ""Backup:"" + Environment.NewLine + backupPath," + $nl +
"                        ""Juancho"", MessageBoxButtons.OK, MessageBoxIcon.Information);" + $nl +
"                }" + $nl +
"                catch (Exception ex)" + $nl +
"                {" + $nl +
"                    StatusStripUpdate(""Juancho: Texture2D replacement failed."");" + $nl +
"                    try" + $nl +
"                    {" + $nl +
"                        assetsManager.SpecifyUnityVersion = specifyUnityVersion.Text;" + $nl +
"                        await Task.Run(() => assetsManager.LoadFiles(reloadPaths.Length > 0 ? reloadPaths : new[] { sourcePath }));" + $nl +
"                        await BuildAssetStructuresAndSelectAsync(selectedAsset.m_PathID, selectedAsset.Text);" + $nl +
"                    }" + $nl +
"                    catch" + $nl +
"                    {" + $nl +
"                        // Keep the original replacement error as the message shown to the user." + $nl +
"                    }" + $nl + $nl +
"                    MessageBox.Show(this," + $nl +
"                        ""Texture2D replacement failed."" + Environment.NewLine + Environment.NewLine + ex.Message," + $nl +
"                        ""Juancho"", MessageBoxButtons.OK, MessageBoxIcon.Error);" + $nl +
"                }" + $nl +
"                finally" + $nl +
"                {" + $nl +
"                    replaceSelectedTextureToolStripMenuItem.Enabled = true;" + $nl +
"                }" + $nl +
"            }" + $nl +
"        }" + $nl + $nl

    $marker = "        private void showExpOpt_Click(object sender, EventArgs e)"
    if ($formText -notmatch 'BuildAssetStructuresAndSelectAsync') {
        $helper =
"        private async Task BuildAssetStructuresAndSelectAsync(long targetPathId, string targetName)" + $nl +
"        {" + $nl +
"            if (assetsManager.assetsFileList.Count == 0)" + $nl +
"            {" + $nl +
"                StatusStripUpdate(""No Unity file can be loaded."");" + $nl +
"                return;" + $nl +
"            }" + $nl + $nl +
"            (var productName, var treeNodeCollection) = await Task.Run(() => BuildAssetData());" + $nl +
"            var typeMap = await Task.Run(() => BuildClassStructure());" + $nl + $nl +
"            if (!string.IsNullOrEmpty(productName))" + $nl +
"                Text = $""AssetStudioGUI v{Application.ProductVersion} - {productName} - {assetsManager.assetsFileList[0].unityVersion} - {assetsManager.assetsFileList[0].m_TargetPlatform}"";" + $nl +
"            else" + $nl +
"                Text = $""AssetStudioGUI v{Application.ProductVersion} - no productName - {assetsManager.assetsFileList[0].unityVersion} - {assetsManager.assetsFileList[0].m_TargetPlatform}"";" + $nl + $nl +
"            assetListView.VirtualListSize = visibleAssets.Count;" + $nl + $nl +
"            sceneTreeView.BeginUpdate();" + $nl +
"            sceneTreeView.Nodes.AddRange(treeNodeCollection.ToArray());" + $nl +
"            sceneTreeView.EndUpdate();" + $nl +
"            treeNodeCollection.Clear();" + $nl + $nl +
"            classesListView.BeginUpdate();" + $nl +
"            foreach (var version in typeMap)" + $nl +
"            {" + $nl +
"                var versionGroup = new ListViewGroup(version.Key);" + $nl +
"                classesListView.Groups.Add(versionGroup);" + $nl +
"                foreach (var uclass in version.Value)" + $nl +
"                {" + $nl +
"                    uclass.Value.Group = versionGroup;" + $nl +
"                    classesListView.Items.Add(uclass.Value);" + $nl +
"                }" + $nl +
"            }" + $nl +
"            typeMap.Clear();" + $nl +
"            classesListView.EndUpdate();" + $nl + $nl +
"            var types = exportableAssets.Select(x => x.Type).Distinct().OrderBy(x => x.ToString()).ToArray();" + $nl +
"            foreach (var type in types)" + $nl +
"            {" + $nl +
"                var typeItem = new ToolStripMenuItem" + $nl +
"                {" + $nl +
"                    CheckOnClick = true," + $nl +
"                    Name = type.ToString()," + $nl +
"                    Size = new Size(180, 22)," + $nl +
"                    Text = type.ToString()" + $nl +
"                };" + $nl +
"                typeItem.Click += typeToolStripMenuItem_Click;" + $nl +
"                filterTypeToolStripMenuItem.DropDownItems.Add(typeItem);" + $nl +
"            }" + $nl +
"            allToolStripMenuItem.Checked = true;" + $nl + $nl +
"            FilterAssetList();" + $nl + $nl +
"            int targetIndex = -1;" + $nl +
"            for (int i = 0; i < visibleAssets.Count; i++)" + $nl +
"            {" + $nl +
"                var item = visibleAssets[i];" + $nl +
"                if (item.Type == ClassIDType.Texture2D && item.m_PathID == targetPathId &&" + $nl +
"                    (string.IsNullOrEmpty(targetName) || string.Equals(item.Text, targetName, StringComparison.Ordinal)))" + $nl +
"                {" + $nl +
"                    targetIndex = i;" + $nl +
"                    break;" + $nl +
"                }" + $nl +
"            }" + $nl + $nl +
"            if (targetIndex >= 0)" + $nl +
"            {" + $nl +
"                assetListView.SelectedIndices.Clear();" + $nl +
"                assetListView.SelectedIndices.Add(targetIndex);" + $nl +
"                assetListView.EnsureVisible(targetIndex);" + $nl +
"                lastSelectedItem = visibleAssets[targetIndex];" + $nl +
"                if (enablePreview.Checked)" + $nl +
"                    PreviewAsset(lastSelectedItem);" + $nl +
"                if (displayInfo.Checked && lastSelectedItem.InfoText != null)" + $nl +
"                {" + $nl +
"                    assetInfoLabel.Text = lastSelectedItem.InfoText;" + $nl +
"                    assetInfoLabel.Visible = true;" + $nl +
"                }" + $nl +
"            }" + $nl + $nl +
"            var log = $""Finished loading {assetsManager.assetsFileList.Count} files with {assetListView.Items.Count} exportable assets"";" + $nl +
"            StatusStripUpdate(log);" + $nl +
"        }" + $nl + $nl;
        $formText = $formText.Replace($marker, $helper + $marker)
    }
    if (-not $formText.Contains($marker)) { throw "Could not find form insertion marker." }

    if ($formText -notmatch 'ForceJuanchoSavedTexturePreviewAsync') {
        $previewHelper =
"        private async Task ForceJuanchoSavedTexturePreviewAsync(string sourcePath, long targetPathId, string targetName, string unityVersion)" + $nl +
"        {" + $nl +
"            try" + $nl +
"            {" + $nl +
"                var previewData = await Task.Run(() => LoadJuanchoSavedTexturePreviewData(sourcePath, targetPathId, targetName, unityVersion));" + $nl +
"                if (previewData.data == null || previewData.data.Length == 0)" + $nl +
"                {" + $nl +
"                    StatusStripUpdate(""Juancho: saved Texture2D preview data was empty; keeping normal preview."");" + $nl +
"                    return;" + $nl +
"                }" + $nl + $nl +
"                imageTexture?.Dispose();" + $nl +
"                imageTexture = new DirectBitmap(previewData.data, previewData.width, previewData.height);" + $nl +
"                previewPanel.BackgroundImage = imageTexture.Bitmap;" + $nl +
"                previewPanel.BackgroundImageLayout = imageTexture.Width > previewPanel.Width || imageTexture.Height > previewPanel.Height" + $nl +
"                    ? ImageLayout.Zoom" + $nl +
"                    : ImageLayout.Center;" + $nl +
"                StatusStripUpdate(""Juancho: preview refreshed from the newly saved Texture2D on disk."");" + $nl +
"            }" + $nl +
"            catch (Exception ex)" + $nl +
"            {" + $nl +
"                StatusStripUpdate(""Juancho: saved Texture2D preview refresh failed: "" + ex.Message);" + $nl +
"            }" + $nl +
"        }" + $nl + $nl +
"        private static (byte[] data, int width, int height) LoadJuanchoSavedTexturePreviewData(string sourcePath, long targetPathId, string targetName, string unityVersion)" + $nl +
"        {" + $nl +
"            var manager = new AssetStudio.AssetsManager();" + $nl +
"            try" + $nl +
"            {" + $nl +
"                manager.SpecifyUnityVersion = unityVersion;" + $nl +
"                manager.LoadFiles(sourcePath);" + $nl +
"                Texture2D texture = null;" + $nl +
"                foreach (var assetsFile in manager.assetsFileList)" + $nl +
"                {" + $nl +
"                    foreach (var obj in assetsFile.Objects)" + $nl +
"                    {" + $nl +
"                        if (obj is Texture2D candidate && candidate.m_PathID == targetPathId &&" + $nl +
"                            (string.IsNullOrEmpty(targetName) || string.Equals(candidate.m_Name, targetName, StringComparison.Ordinal)))" + $nl +
"                        {" + $nl +
"                            texture = candidate;" + $nl +
"                            break;" + $nl +
"                        }" + $nl +
"                    }" + $nl +
"                    if (texture != null) break;" + $nl +
"                }" + $nl + $nl +
"                if (texture == null)" + $nl +
"                    throw new InvalidOperationException(""The freshly saved Texture2D could not be found for preview."");" + $nl + $nl +
"                using (var image = texture.ConvertToImage(true))" + $nl +
"                {" + $nl +
"                    if (image == null)" + $nl +
"                        throw new InvalidOperationException(""The freshly saved Texture2D could not be decoded for preview."");" + $nl +
"                    return (image.ConvertToBytes(), texture.m_Width, texture.m_Height);" + $nl +
"                }" + $nl +
"            }" + $nl +
"            finally" + $nl +
"            {" + $nl +
"                manager.Clear();" + $nl +
"            }" + $nl +
"        }" + $nl + $nl;
        if (-not $formText.Contains($marker)) { throw "Could not find preview helper insertion marker." }
        $formText = $formText.Replace($marker, $previewHelper + $handler + $marker)
    }
    Set-Content $form $formText -Encoding UTF8
}
