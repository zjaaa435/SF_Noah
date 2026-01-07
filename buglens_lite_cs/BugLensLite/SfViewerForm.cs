using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;

namespace BugLensLite;

public sealed class SfViewerForm : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _sfCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 520 };
    private readonly ComboBox _androidCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 520 };
    private readonly ComboBox _mp4Combo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 520 };
    private readonly Button _btnLoad = new() { Text = "加载", AutoSize = true };
    private readonly Button _btnOpenFolder = new() { Text = "打开目录", AutoSize = true };
    private readonly CheckBox _chkLoadVideo = new() { Text = "加载视频", Checked = false, AutoSize = true };
    private readonly Label _lblHint = new() { AutoSize = true };
    private readonly ToolTip _tip = new();

    private bool _navigated;
    private TaskCompletionSource<bool>? _readyTcs;

    public SfViewerForm(string title)
    {
        Text = title;
        Width = 1400;
        Height = 900;
        StartPosition = FormStartPosition.CenterScreen;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(10, 10, 10, 8)
        };
        bar.Controls.Add(new Label { Text = "SF:", AutoSize = true, Padding = new Padding(0, 4, 0, 0) });
        bar.Controls.Add(_sfCombo);
        bar.Controls.Add(new Label { Text = "Android:", AutoSize = true, Padding = new Padding(12, 4, 0, 0) });
        bar.Controls.Add(_androidCombo);
        bar.Controls.Add(new Label { Text = "视频:", AutoSize = true, Padding = new Padding(12, 4, 0, 0) });
        bar.Controls.Add(_mp4Combo);
        bar.Controls.Add(new Panel { Width = 10 });
        bar.Controls.Add(_btnLoad);
        bar.Controls.Add(_chkLoadVideo);
        bar.Controls.Add(_btnOpenFolder);
        bar.Controls.Add(new Panel { Width = 10 });
        _lblHint.Text = "";
        bar.Controls.Add(_lblHint);

        root.Controls.Add(bar, 0, 0);
        root.Controls.Add(_web, 0, 1);
        Controls.Add(root);

        _btnLoad.Click += async (_, _) => await LoadSelectedAsync();
        _btnOpenFolder.Click += (_, _) =>
        {
            try
            {
                var p = _sfCombo.SelectedItem?.ToString();
                if (!string.IsNullOrEmpty(p)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = Path.GetDirectoryName(p)!, UseShellExecute = true });
            }
            catch { }
        };

        _sfCombo.SelectedIndexChanged += (_, _) => UpdatePathHints();
        _androidCombo.SelectedIndexChanged += (_, _) => UpdatePathHints();
        _mp4Combo.SelectedIndexChanged += (_, _) => UpdatePathHints();
        _tip.AutoPopDelay = 15000;
        _tip.InitialDelay = 200;
        _tip.ReshowDelay = 100;
    }

    public void SetCandidates(string[] sfLogsPaths, string[] androidLogPaths, string[] mp4Paths)
    {
        _sfCombo.Items.Clear();
        _androidCombo.Items.Clear();
        _mp4Combo.Items.Clear();

        foreach (var p in sfLogsPaths ?? Array.Empty<string>()) _sfCombo.Items.Add(p);
        foreach (var p in androidLogPaths ?? Array.Empty<string>()) _androidCombo.Items.Add(p);
        foreach (var p in mp4Paths ?? Array.Empty<string>()) _mp4Combo.Items.Add(p);

        if (_sfCombo.Items.Count > 0) _sfCombo.SelectedIndex = 0;
        if (_androidCombo.Items.Count > 0) _androidCombo.SelectedIndex = 0;
        if (_mp4Combo.Items.Count > 0) _mp4Combo.SelectedIndex = 0;
        UpdatePathHints();
    }

    // Back-compat: load a specific pair (used by older call sites).
    // IMPORTANT: do NOT overwrite existing candidate dropdowns.
    public async Task LoadAsync(string sfLogsPath, string? androidLogPath)
    {
        if (!string.IsNullOrEmpty(sfLogsPath))
        {
            // If candidates already set and contains this path, select it; otherwise insert.
            if (_sfCombo.Items.Count == 0 || !_sfCombo.Items.Contains(sfLogsPath))
            {
                _sfCombo.Items.Clear();
                _sfCombo.Items.Add(sfLogsPath);
            }
            _sfCombo.SelectedItem = sfLogsPath;
        }

        if (!string.IsNullOrEmpty(androidLogPath))
        {
            if (_androidCombo.Items.Count == 0 || !_androidCombo.Items.Contains(androidLogPath))
            {
                _androidCombo.Items.Clear();
                _androidCombo.Items.Add(androidLogPath);
            }
            _androidCombo.SelectedItem = androidLogPath;
        }

        await LoadSelectedAsync();
    }

    public async Task LoadCurrentSelectionAsync()
    {
        await LoadSelectedAsync();
    }

    private async Task EnsureViewerReadyAsync()
    {
        var viewerPath = Path.Combine(AppContext.BaseDirectory, "Assets", "sf_log_viewerDemo.html");
        if (!File.Exists(viewerPath))
            throw new Exception($"找不到 SF viewer: {viewerPath}");

        await _web.EnsureCoreWebView2Async();
        if (_readyTcs == null)
        {
            _readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _web.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                try { _readyTcs?.TrySetResult(true); } catch { }
            };
        }
        if (!_navigated)
        {
            _navigated = true;
            _readyTcs.TrySetResult(false); // reset-ish
            _readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _web.Source = new Uri(viewerPath);
        }
        await _readyTcs.Task;
    }

    private async Task LoadSelectedAsync()
    {
        try
        {
            var sf = _sfCombo.SelectedItem?.ToString() ?? "";
            if (string.IsNullOrEmpty(sf) || !File.Exists(sf))
            {
                MessageBox.Show(this, "请选择有效的 sf_logs.txt", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var android = _androidCombo.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(android) || !File.Exists(android)) android = null;

            await EnsureViewerReadyAsync();

            // Load sf
            var sfText = await File.ReadAllTextAsync(sf);
            var sfJson = System.Text.Json.JsonSerializer.Serialize(sfText);
            var js =
                "(async () => {" +
                "  for (let i=0;i<120;i++) {" +
                "    if (typeof loadText === 'function') { await loadText(" + sfJson + "); return 'ok'; }" +
                "    await new Promise(r => setTimeout(r, 50));" +
                "  }" +
                "  return 'loadText-not-ready';" +
                "})()";
            await _web.CoreWebView2.ExecuteScriptAsync(js);

            // Load android (optional)
            if (!string.IsNullOrEmpty(android))
            {
                var aText = await File.ReadAllTextAsync(android);
                var aJson = System.Text.Json.JsonSerializer.Serialize(aText);
                var js2 =
                    "(async () => {" +
                    "  for (let i=0;i<120;i++) {" +
                    "    if (typeof indexAndroidLogText === 'function') { await indexAndroidLogText(" + aJson + "); return 'android-ok'; }" +
                    "    await new Promise(r => setTimeout(r, 50));" +
                    "  }" +
                    "  return 'android-not-ready';" +
                    "})()";
                await _web.CoreWebView2.ExecuteScriptAsync(js2);
            }

            // Load mp4 (optional): map local folder to virtual host and set video src to URL
            var mp4 = _mp4Combo.SelectedItem?.ToString();
            if (_chkLoadVideo.Checked && !string.IsNullOrEmpty(mp4) && File.Exists(mp4))
            {
                var name = Path.GetFileName(mp4);
                // Prefer file:// for speed and to avoid virtual host / CORS quirks for media.
                var url = new Uri(mp4).AbsoluteUri;
                var js3 = $"window.__BUGLENS_LOAD_VIDEO_URL__({System.Text.Json.JsonSerializer.Serialize(url)}, {System.Text.Json.JsonSerializer.Serialize(name)});";
                await _web.CoreWebView2.ExecuteScriptAsync(js3);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "加载失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdatePathHints()
    {
        try
        {
            var sf = _sfCombo.SelectedItem?.ToString() ?? "";
            var a = _androidCombo.SelectedItem?.ToString() ?? "";
            var m = _mp4Combo.SelectedItem?.ToString() ?? "";

            // Show full paths in tooltip + compact summary in label
            _tip.SetToolTip(_sfCombo, sf);
            _tip.SetToolTip(_androidCombo, a);
            _tip.SetToolTip(_mp4Combo, m);

            // Make dropdown wider so path is easier to read
            _sfCombo.DropDownWidth = Math.Max(_sfCombo.Width, 900);
            _androidCombo.DropDownWidth = Math.Max(_androidCombo.Width, 900);
            _mp4Combo.DropDownWidth = Math.Max(_mp4Combo.Width, 900);

            _lblHint.Text = $"sf={_sfCombo.Items.Count}, android={_androidCombo.Items.Count}, mp4={_mp4Combo.Items.Count}";
        }
        catch
        {
            // ignore
        }
    }
}


