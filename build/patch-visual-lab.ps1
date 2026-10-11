$ErrorActionPreference = 'Stop'

$workspace = $env:GITHUB_WORKSPACE
$gui = Join-Path $workspace 'AssetStudio\AssetStudioGUI'
$formPath = Join-Path $gui 'AssetStudioGUIForm.cs'
$coreSource = Join-Path $workspace 'build\patch\JuanchoVisualLabCore.cs'
$coreTarget = Join-Path $gui 'JuanchoVisualLabCore.cs'
$formSource = Join-Path $workspace 'build\patch\JuanchoVisualLabForm.cs'
$formTarget = Join-Path $gui 'JuanchoVisualLabForm.cs'

if (-not (Test-Path $formPath)) { throw "AssetStudioGUIForm.cs was not found at $formPath" }
if (-not (Test-Path $coreSource)) { throw "Visual Lab core source was not found at $coreSource" }
if (-not (Test-Path $formSource)) { throw "Visual Lab form source was not found at $formSource" }

Copy-Item $coreSource $coreTarget -Force
Copy-Item $formSource $formTarget -Force

$formText = Get-Content $formPath -Raw
$marker = 'JUANCHO_VISUAL_LAB_MENU_START'
if ($formText -notmatch [regex]::Escape($marker)) {
    $needle = '            InitializeComponent();'
    if (-not $formText.Contains($needle)) { throw "Could not find the AssetStudioGUIForm constructor insertion point." }
    $replacement = @'
            InitializeComponent();
            // JUANCHO_VISUAL_LAB_MENU_START
            var visualLabMenuItem = new System.Windows.Forms.ToolStripMenuItem("MLBB Visual Lab");
            visualLabMenuItem.Click += (sender, args) =>
            {
                using (var visualLabForm = new JuanchoVisualLabForm())
                    visualLabForm.ShowDialog(this);
            };
            menuStrip1.Items.Add(visualLabMenuItem);
            // JUANCHO_VISUAL_LAB_MENU_END
'@
    $formText = $formText.Replace($needle, $replacement.TrimEnd() + [Environment]::NewLine)
    Set-Content -Path $formPath -Value $formText -Encoding UTF8
}

Write-Host "Juancho MLBB Visual Lab foundation was added to AssetStudioGUI."
