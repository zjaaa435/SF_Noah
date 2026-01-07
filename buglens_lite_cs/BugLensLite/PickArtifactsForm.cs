using System;
using System.Linq;
using System.Windows.Forms;
using BugLensLite.Services;

namespace BugLensLite;

public sealed class PickArtifactsForm : Form
{
    private readonly ListBox _sfList = new() { IntegralHeight = false, Dock = DockStyle.Fill };
    private readonly ListBox _androidList = new() { IntegralHeight = false, Dock = DockStyle.Fill };
    private readonly ListBox _mp4List = new() { IntegralHeight = false, Dock = DockStyle.Fill };
    private readonly Button _ok = new() { Text = "确定", Dock = DockStyle.Bottom, Height = 40 };
    private readonly Button _cancel = new() { Text = "取消", Dock = DockStyle.Bottom, Height = 40 };

    public string? SelectedSfLogsPath { get; private set; }
    public string? SelectedAndroidLogPath { get; private set; }
    public string? SelectedScreenRecordPath { get; private set; }

    public PickArtifactsForm(LogProjectCandidates c)
    {
        Text = "选择要加载的文件（可多份）";
        Width = 1100;
        Height = 700;
        StartPosition = FormStartPosition.CenterParent;

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Padding = new Padding(10) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        grid.Controls.Add(new Label { Text = $"SF logs ({c.SfLogsPaths.Count})", AutoSize = true }, 0, 0);
        grid.Controls.Add(new Label { Text = $"Android logs ({c.AndroidLogPaths.Count})", AutoSize = true }, 1, 0);
        grid.Controls.Add(new Label { Text = $"Screen record ({c.ScreenRecordPaths.Count})", AutoSize = true }, 2, 0);

        foreach (var p in c.SfLogsPaths) _sfList.Items.Add(p);
        foreach (var p in c.AndroidLogPaths) _androidList.Items.Add(p);
        foreach (var p in c.ScreenRecordPaths) _mp4List.Items.Add(p);

        if (_sfList.Items.Count > 0) _sfList.SelectedIndex = 0;
        if (_androidList.Items.Count > 0) _androidList.SelectedIndex = 0;
        if (_mp4List.Items.Count > 0) _mp4List.SelectedIndex = 0;

        grid.Controls.Add(_sfList, 0, 1);
        grid.Controls.Add(_androidList, 1, 1);
        grid.Controls.Add(_mp4List, 2, 1);

        Controls.Add(grid);
        Controls.Add(_ok);
        Controls.Add(_cancel);

        _ok.Click += (_, _) =>
        {
            SelectedSfLogsPath = _sfList.SelectedItem?.ToString();
            SelectedAndroidLogPath = _androidList.SelectedItem?.ToString();
            SelectedScreenRecordPath = _mp4List.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(SelectedSfLogsPath))
            {
                MessageBox.Show(this, "必须选择一个 sf_logs.txt", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        };
        _cancel.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };

        _sfList.DoubleClick += (_, _) => _ok.PerformClick();
    }
}






