using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace BugLensLite;

public sealed class PickArchiveForm : Form
{
    private readonly ListBox _list = new() { IntegralHeight = false, Dock = DockStyle.Fill };
    private readonly Button _ok = new() { Text = "导入", Dock = DockStyle.Bottom, Height = 40 };
    private readonly Button _cancel = new() { Text = "取消", Dock = DockStyle.Bottom, Height = 40 };

    public string SelectedPath { get; private set; } = "";

    public PickArchiveForm(string title, List<string> archivePaths)
    {
        Text = title;
        Width = 980;
        Height = 600;
        StartPosition = FormStartPosition.CenterParent;

        var top = new Label
        {
            Dock = DockStyle.Top,
            Height = 44,
            Text = $"找到 {archivePaths.Count} 个压缩包，请选择要导入的一个：",
            Padding = new Padding(10, 12, 10, 10)
        };

        Controls.Add(_list);
        Controls.Add(_ok);
        Controls.Add(_cancel);
        Controls.Add(top);

        foreach (var p in archivePaths.OrderByDescending(File.GetLastWriteTimeUtc))
        {
            var name = Path.GetFileName(p);
            var ts = File.GetLastWriteTime(p).ToString("yyyy-MM-dd HH:mm:ss");
            _list.Items.Add($"{ts} | {name}");
        }
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;

        _ok.Click += (_, _) =>
        {
            if (_list.SelectedIndex < 0) return;
            var display = _list.SelectedItem?.ToString() ?? "";
            // map back by index (same order)
            var picked = archivePaths.OrderByDescending(File.GetLastWriteTimeUtc).ElementAt(_list.SelectedIndex);
            SelectedPath = picked;
            DialogResult = DialogResult.OK;
            Close();
        };
        _cancel.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };
        _list.DoubleClick += (_, _) => _ok.PerformClick();
    }
}






