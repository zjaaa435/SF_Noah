using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using BugLensLite.Services;

namespace BugLensLite;

public sealed class PickArchiveEntriesForm : Form
{
    private readonly ListBox _sf = new() { IntegralHeight = false, Dock = DockStyle.Fill };
    private readonly ListBox _android = new() { IntegralHeight = false, Dock = DockStyle.Fill };
    private readonly ListBox _mp4 = new() { IntegralHeight = false, Dock = DockStyle.Fill };
    private readonly Button _ok = new() { Text = "确定", Dock = DockStyle.Bottom, Height = 40 };
    private readonly Button _cancel = new() { Text = "取消", Dock = DockStyle.Bottom, Height = 40 };

    public string? SelectedSfEntry { get; private set; }
    public string? SelectedAndroidEntry { get; private set; }
    public string? SelectedMp4Entry { get; private set; }

    public PickArchiveEntriesForm(ArchiveCandidates c)
    {
        Text = "选择要解压/加载的文件（压缩包内）";
        Width = 1100;
        Height = 700;
        StartPosition = FormStartPosition.CenterParent;

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Padding = new Padding(10) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        grid.Controls.Add(new Label { Text = $"SF logs ({c.SfLogsEntries.Count})", AutoSize = true }, 0, 0);
        grid.Controls.Add(new Label { Text = $"Android logs ({c.AndroidLogEntries.Count})", AutoSize = true }, 1, 0);
        grid.Controls.Add(new Label { Text = $"Screen record ({c.ScreenRecordEntries.Count})", AutoSize = true }, 2, 0);

        foreach (var p in c.SfLogsEntries) _sf.Items.Add(p);
        foreach (var p in c.AndroidLogEntries) _android.Items.Add(p);
        foreach (var p in c.ScreenRecordEntries) _mp4.Items.Add(p);

        if (_sf.Items.Count > 0) _sf.SelectedIndex = 0;
        if (_android.Items.Count > 0) _android.SelectedIndex = 0;
        if (_mp4.Items.Count > 0) _mp4.SelectedIndex = 0;

        grid.Controls.Add(_sf, 0, 1);
        grid.Controls.Add(_android, 1, 1);
        grid.Controls.Add(_mp4, 2, 1);

        Controls.Add(grid);
        Controls.Add(_ok);
        Controls.Add(_cancel);

        _ok.Click += (_, _) =>
        {
            SelectedSfEntry = _sf.SelectedItem?.ToString();
            SelectedAndroidEntry = _android.SelectedItem?.ToString();
            SelectedMp4Entry = _mp4.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(SelectedSfEntry))
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
        _sf.DoubleClick += (_, _) => _ok.PerformClick();
    }
}






