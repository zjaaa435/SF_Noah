using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace BugLensLite;

public sealed class PickDownloadUrlForm : Form
{
    private readonly ListBox _list = new() { IntegralHeight = false, Dock = DockStyle.Fill };
    private readonly Button _ok = new() { Text = "下载", Dock = DockStyle.Bottom, Height = 40 };
    private readonly Button _cancel = new() { Text = "取消", Dock = DockStyle.Bottom, Height = 40 };

    public string SelectedUrl { get; private set; } = "";

    public PickDownloadUrlForm(string bugId, List<string> urls)
    {
        Text = $"选择下载链接（bugId={bugId}）";
        Width = 980;
        Height = 600;
        StartPosition = FormStartPosition.CenterParent;

        var top = new Label
        {
            Dock = DockStyle.Top,
            Height = 40,
            Text = $"该缺陷有 {urls.Count} 个下载链接（可能包含多份日志/附件），请选择一个直接下载：",
            Padding = new Padding(10, 10, 10, 10)
        };

        Controls.Add(_list);
        Controls.Add(_ok);
        Controls.Add(_cancel);
        Controls.Add(top);

        foreach (var u in urls.Where(x => !string.IsNullOrWhiteSpace(x)))
            _list.Items.Add(u);
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;

        _ok.Click += (_, _) =>
        {
            SelectedUrl = _list.SelectedItem?.ToString() ?? "";
            if (string.IsNullOrEmpty(SelectedUrl))
            {
                MessageBox.Show(this, "请选择一个链接", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

        _list.DoubleClick += (_, _) => _ok.PerformClick();
    }
}






