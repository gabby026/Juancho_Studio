using System;
using System.Drawing;
using System.Windows.Forms;
using Juancho.VisualLab;

namespace AssetStudioGUI
{
    /// <summary>
    /// Read-only view of serialized AnimationClip timing, curve bindings, and embedded events.
    /// It does not claim to play the animation.
    /// </summary>
    public sealed class JuanchoVisualLabAnimationInspectorForm : Form
    {
        private readonly DataGridView _curveGrid = new DataGridView();
        private readonly DataGridView _eventGrid = new DataGridView();

        public JuanchoVisualLabAnimationInspectorForm(VisualLabAnimationClipInfo info)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));
            Text = "Juancho MLBB Visual Lab — AnimationClip Inspector — " + (string.IsNullOrWhiteSpace(info.Name) ? "Clip" : info.Name);
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(800, 540);
            Size = new Size(1180, 790);
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(8) };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 210));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var summary = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                WordWrap = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point),
                Text = BuildSummary(info)
            };

            ConfigureCurveGrid();
            _curveGrid.DataSource = info.Curves;
            ConfigureEventGrid();
            _eventGrid.DataSource = info.Events;

            var tabs = new TabControl { Dock = DockStyle.Fill };
            var curveTab = new TabPage("Curves / bindings");
            var eventTab = new TabPage("Embedded events");
            curveTab.Controls.Add(_curveGrid);
            eventTab.Controls.Add(_eventGrid);
            tabs.TabPages.Add(curveTab);
            tabs.TabPages.Add(eventTab);

            root.Controls.Add(summary, 0, 0);
            root.Controls.Add(tabs, 0, 1);
            Controls.Add(root);
        }

        private static string BuildSummary(VisualLabAnimationClipInfo info)
        {
            return
                "AnimationClip: " + info.Name + Environment.NewLine +
                "Source: " + info.SourceBundlePath + Environment.NewLine +
                "Path ID: " + info.PathId + "   Unity: " + info.UnityVersion + "   Platform: " + info.Platform + Environment.NewLine +
                "Duration: " + info.DurationSeconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) +
                " s   Sample rate: " + info.SampleRate.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) +
                " Hz   Wrap mode: " + info.WrapMode + Environment.NewLine +
                "Legacy: " + info.Legacy + "   Compressed: " + info.Compressed + "   High quality curve: " + info.HighQualityCurve +
                "   Humanoid/muscle data: " + info.HasMuscleClip + "   Muscle loop: " + info.MuscleClipLoops + Environment.NewLine +
                "Bindings: " + info.BindingCount + "   Position: " + info.PositionCurveCount + "   Rotation: " + info.RotationCurveCount +
                "   Euler: " + info.EulerCurveCount + "   Scale: " + info.ScaleCurveCount + "   Float/property: " + info.FloatCurveCount +
                "   Compressed rotation: " + info.CompressedRotationCurveCount + "   Object reference: " + info.PPtrCurveCount +
                "   Events: " + info.EventCount + Environment.NewLine + Environment.NewLine +
                "Playback readiness: " + info.PlaybackReadiness + Environment.NewLine +
                "This is a data inspector only. It does not play or deform the hero; rig binding and the playback runtime are still pending.";
        }

        private void ConfigureCurveGrid()
        {
            _curveGrid.Dock = DockStyle.Fill;
            _curveGrid.ReadOnly = true;
            _curveGrid.AllowUserToAddRows = false;
            _curveGrid.AllowUserToDeleteRows = false;
            _curveGrid.AllowUserToOrderColumns = true;
            _curveGrid.RowHeadersVisible = false;
            _curveGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _curveGrid.AutoGenerateColumns = false;
            _curveGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _curveGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Channel", HeaderText = "Channel", FillWeight = 90 });
            _curveGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "BindingPath", HeaderText = "Transform path", FillWeight = 150 });
            _curveGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Attribute", HeaderText = "Attribute", FillWeight = 120 });
            _curveGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "KeyCount", HeaderText = "Keys", FillWeight = 45 });
            _curveGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "FirstKeySeconds", HeaderText = "First key (s)", FillWeight = 65 });
            _curveGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "LastKeySeconds", HeaderText = "Last key (s)", FillWeight = 65 });
            _curveGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Notes", HeaderText = "Notes", FillWeight = 160 });
        }

        private void ConfigureEventGrid()
        {
            _eventGrid.Dock = DockStyle.Fill;
            _eventGrid.ReadOnly = true;
            _eventGrid.AllowUserToAddRows = false;
            _eventGrid.AllowUserToDeleteRows = false;
            _eventGrid.AllowUserToOrderColumns = true;
            _eventGrid.RowHeadersVisible = false;
            _eventGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _eventGrid.AutoGenerateColumns = false;
            _eventGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _eventGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "TimeSeconds", HeaderText = "Time (s)", FillWeight = 45 });
            _eventGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "FunctionName", HeaderText = "Function / event", FillWeight = 90 });
            _eventGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Data", HeaderText = "Data", FillWeight = 140 });
            _eventGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "FloatParameter", HeaderText = "Float", FillWeight = 45 });
            _eventGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "IntParameter", HeaderText = "Integer", FillWeight = 45 });
            _eventGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "ObjectReferencePathId", HeaderText = "Object Path ID", FillWeight = 70 });
        }
    }
}
