using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using BugLensLite.Controls;
using BugLensLite.Services;
using Microsoft.Web.WebView2.WinForms;

namespace BugLensLite;

public sealed class MainForm : Form
{
    private readonly Button _btnLogin = new() { Text = "TT登录", AutoSize = true };
    private readonly Button _btnFetch = new() { Text = "拉取/刷新", AutoSize = true };
    private readonly Button _btnAlog = new() { Text = "ALOG", AutoSize = true };
    private readonly Button _btnUpdate = new() { Text = "检查更新", AutoSize = true };
    private readonly Button _btnAddComment = new() { Text = "发表评论", AutoSize = true };
    // 下载按钮改为表格行内按钮（Download column）
    private readonly TextBox _txtBugId = new() { PlaceholderText = "例如：10638213" };
    private readonly Label _lblAuth = new() { Text = "auth: -", AutoSize = true };
    private readonly Label _lblOperator = new() { Text = "operator: -", AutoSize = true, ForeColor = SystemColors.GrayText, Padding = new Padding(8, 3, 0, 0) };
    private readonly ListBox _bugList = new() { IntegralHeight = false };
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly WebView2 _webBase = new() { Dock = DockStyle.Fill };
    private readonly FlowLayoutPanel _attachFlow = new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    private readonly Label _lblAttachHint = new() { Dock = DockStyle.Top, AutoSize = false, Height = 22, Text = "附件", ForeColor = SystemColors.GrayText };
    // Comments are rich (images/links); render as HTML cards for better UX
    private readonly WebView2 _webComment = new() { Dock = DockStyle.Fill };
    private readonly WebView2 _webDl = new() { Visible = false, Width = 1, Height = 1 };
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusText = new() { Text = "Ready" };
    private readonly ContextMenuStrip _gridMenu = new();
    private readonly CursorIntegrationService _cursor = new();
    private string _operatorId = "";

    private readonly NoahPoseidonService _service = new();
    private readonly AppUpdateService _update = new();
    private readonly LogProjectDiscovery _discovery = new();
    private readonly ZipLogDownloader _zip = new();
    private readonly ArchiveScanner _scanner = new();

    private readonly List<BugItem> _items = new();
    private string _token = "";
    private string _currentBugId = "";
    // SF 可视化改为弹出独立窗口（SfViewerForm），不在本工具内展示
    private bool _dlHooked = false;
    private string _dlFolder = "";
    private string _dlBugId = "";
    private readonly Dictionary<string, string> _activeDownloads = new(StringComparer.OrdinalIgnoreCase); // filePath -> bugId

    // 日志包 Tab
    private readonly Label _lblDownloadDir = new() { AutoSize = true };
    private readonly Button _btnOpenDownloadDir = new() { Text = "打开目录", AutoSize = true };
    private readonly Button _btnRefreshArchives = new() { Text = "刷新", AutoSize = true };
    private readonly Button _btnPickArchive = new() { Text = "选择压缩包", AutoSize = true };
    private readonly ListBox _archiveList = new() { IntegralHeight = false, Dock = DockStyle.Fill };

    // Runtime layout safety: never let SplitContainer distances/min sizes cause startup crash on small windows / high DPI.
    private SplitContainer? _rightSplit;
    private bool _applyingRightLayout = false;

    public MainForm()
    {
        Text = "SF_Noah";
        Width = 1280;
        Height = 800;
        StartPosition = FormStartPosition.CenterScreen;

        // operator is auto-detected from TT login; no manual input in UI.

        _status.Items.Add(_statusText);
        Controls.Add(_status);
        // hidden downloader WebView2 (required to set download path programmatically)
        Controls.Add(_webDl);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        // Top toolbar (spans both columns)
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(10, 8, 10, 8)
        };
        toolbar.Controls.Add(new Label { Text = "BugLens Lite", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        toolbar.Controls.Add(new Label { Text = "Noah SF", AutoSize = true, ForeColor = SystemColors.GrayText, Padding = new Padding(8, 3, 0, 0) });
        toolbar.Controls.Add(new Panel { Width = 24 });
        toolbar.Controls.Add(_btnLogin);
        toolbar.Controls.Add(_btnAlog);
        toolbar.Controls.Add(_btnUpdate);
        toolbar.Controls.Add(_btnAddComment);
        toolbar.Controls.Add(_lblAuth);
        toolbar.Controls.Add(_lblOperator);
        root.Controls.Add(toolbar, 0, 0);
        root.SetColumnSpan(toolbar, 2);

        // Left panel: input + list
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(10) };
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.Controls.Add(new Label { Text = "缺陷分析", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, 0, 0);

        var inputRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        inputRow.Controls.Add(_txtBugId, 0, 0);
        inputRow.Controls.Add(_btnFetch, 1, 0);
        left.Controls.Add(inputRow, 0, 1);
        left.Controls.Add(_bugList, 0, 2);
        root.Controls.Add(left, 0, 1);

        // Right panel: grid + tabs
        // We want the detail area (基础信息/附件/评论) to be larger, but must avoid hard-coded min sizes that can crash
        // when the window is small or on high DPI. So we calculate splitter distance at runtime and clamp it.
        var right = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal
        };
        _rightSplit = right;
        root.Controls.Add(right, 1, 1);

        InitGrid();
        right.Panel1.Controls.Add(_grid);

        // Tabs align to the "old tool" style: 基础信息 / 附件 / 评论
        // Explicitly do NOT add: 屏幕 / 触控 / 绘制
        var tabBase = new TabPage("基础信息") { Padding = new Padding(8) };
        tabBase.Controls.Add(_webBase);

        var tabAttach = new TabPage("附件") { Padding = new Padding(8) };
        // Top: our archive list/import helper; Bottom: attachment info from Noah
        var attachSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 220 };
        attachSplit.Panel1.Controls.Add(BuildArchiveTab());
        var attachBottom = new Panel { Dock = DockStyle.Fill };
        attachBottom.Controls.Add(_attachFlow);
        attachBottom.Controls.Add(_lblAttachHint);
        attachSplit.Panel2.Controls.Add(attachBottom);
        tabAttach.Controls.Add(attachSplit);

        var tabComment = new TabPage("评论") { Padding = new Padding(8) };
        tabComment.Controls.Add(_webComment);

        _tabs.TabPages.AddRange(new[] { tabBase, tabAttach, tabComment });
        right.Panel2.Controls.Add(_tabs);

        _btnLogin.Click += async (_, _) => await DoLoginAsync();
        _btnFetch.Click += async (_, _) => await DoFetchAsync();
        _btnAlog.Image = CreateAlogIcon();
        _btnAlog.ImageAlign = ContentAlignment.MiddleLeft;
        _btnAlog.TextAlign = ContentAlignment.MiddleRight;
        _btnAlog.Click += (_, _) => OpenUrl("https://alog.wanyol.com/mine");

        _btnUpdate.Click += async (_, _) => await CheckUpdateAsync();
        _btnAddComment.Click += async (_, _) => await DoAddCommentAsync();
        _txtBugId.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                await DoFetchAsync();
            }
        };
        _bugList.SelectedIndexChanged += (_, _) => SelectFromList();
        _grid.SelectionChanged += (_, _) => SelectFromGrid();
        _grid.CellContentClick += Grid_CellContentClick;
        _grid.MouseDown += Grid_MouseDown;

        _gridMenu.Items.Add("删除缺陷", null, (_, _) => DeleteSelectedBug());

        _btnOpenDownloadDir.Click += (_, _) => OpenDownloadDir();
        _btnRefreshArchives.Click += (_, _) => RefreshArchiveList();
        _btnPickArchive.Click += async (_, _) => await PickArchiveAndLoadAsync();
        _archiveList.DoubleClick += async (_, _) => await LoadSelectedArchiveAsync();

        // Apply safe layout after the form is shown and whenever the window size changes.
        Shown += (_, _) => ApplyRightLayout();
        SizeChanged += (_, _) => ApplyRightLayout();
    }

    private void ApplyRightLayout()
    {
        if (_rightSplit == null) return;
        if (_applyingRightLayout) return;
        try
        {
            _applyingRightLayout = true;

            // Total available height inside the split container.
            var total = _rightSplit.ClientSize.Height;
            if (total <= 0) return;

            // Desired: give more space to bottom (tabs). Keep grid area usable but smaller.
            // Use ratio rather than fixed pixels.
            var desiredPanel1 = (int)Math.Round(total * 0.34); // grid ~34%, details ~66%

            // Minimums (safe small values; do NOT set large min sizes).
            const int minPanel1 = 140;
            const int minPanel2 = 220;

            // SplitterDistance defines Panel1 height. Clamp so Panel2 can still meet its minimum.
            var maxPanel1 = Math.Max(minPanel1, total - minPanel2 - _rightSplit.SplitterWidth);
            var clamped = Math.Max(minPanel1, Math.Min(desiredPanel1, maxPanel1));

            // Only set when it actually changes (reduce layout churn).
            if (Math.Abs(_rightSplit.SplitterDistance - clamped) > 2)
                _rightSplit.SplitterDistance = clamped;
        }
        catch
        {
            // Never block app startup due to layout. Worst case: keep WinForms default behavior.
        }
        finally
        {
            _applyingRightLayout = false;
        }
    }

    private async Task DoAddCommentAsync()
    {
        try
        {
            var bugId = (_currentBugId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(bugId))
            {
                MessageBox.Show(this, "请先选择一个缺陷（bugId）", "发表评论", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (string.IsNullOrWhiteSpace(_token))
            {
                MessageBox.Show(this, "请先点击 TT登录（获取 SIAMTGT + 工号）", "发表评论", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(_operatorId))
            {
                MessageBox.Show(this, "未识别到工号（operatorId）。请重新 TT登录 后再试。", "发表评论", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using var dlg = new AddCommentForm(bugId, _operatorId);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            var content = dlg.CommentText ?? "";
            if (dlg.ConvertNewlinesToBr)
            {
                // If user typed plain text, convert to Noah-friendly rich text: <p>..</p> with <br/>
                var plain = content.Replace("\r\n", "\n").TrimEnd();
                var looksHtml = plain.Contains("<p", StringComparison.OrdinalIgnoreCase) ||
                                plain.Contains("<br", StringComparison.OrdinalIgnoreCase) ||
                                plain.Contains("</", StringComparison.OrdinalIgnoreCase);
                if (!looksHtml)
                {
                    plain = System.Net.WebUtility.HtmlEncode(plain);
                    plain = plain.Replace("\n", "<br/>");
                    content = $"<p>{plain}</p>";
                }
                else
                {
                    content = plain;
                }
            }

            SetStatus("发表评论中...");
            await _service.AddCommentAsync(bugId, content);
            SetStatus("评论已发布，刷新中...");

            var detail = await _service.FetchBugAsync(bugId);
            Upsert(detail);
            SelectBug(bugId);

            MessageBox.Show(this, "评论已发布并已刷新。", "发表评论", MessageBoxButtons.OK, MessageBoxIcon.Information);
            SetStatus("评论已发布");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "发表评论失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("发表评论失败");
        }
    }

    private Control BuildArchiveTab()
    {
        var pnl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        pnl.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        pnl.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));
        pnl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var top = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        _lblDownloadDir.Text = "下载目录：-";
        top.Controls.Add(_lblDownloadDir);
        top.Controls.Add(new Panel { Width = 12 });
        top.Controls.Add(_btnOpenDownloadDir);
        top.Controls.Add(_btnRefreshArchives);
        top.Controls.Add(_btnPickArchive);

        pnl.Controls.Add(top, 0, 0);
        pnl.Controls.Add(new Panel { Height = 8 }, 0, 1);
        pnl.Controls.Add(_archiveList, 0, 2);
        return pnl;
    }

    private void InitGrid()
    {
        _grid.AutoGenerateColumns = false;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "缺陷id", DataPropertyName = nameof(BugItem.BugId), Width = 120 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "描述(节选)", DataPropertyName = nameof(BugItem.Snippet), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "APK", DataPropertyName = nameof(BugItem.ApkName), Width = 160 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "版本", DataPropertyName = nameof(BugItem.OsVer), Width = 120 });

        _grid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "Download",
            HeaderText = "日志",
            Text = "下载日志",
            UseColumnTextForButtonValue = true,
            Width = 90
        });

        _grid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "Import",
            HeaderText = "导入",
            Text = "导入日志",
            UseColumnTextForButtonValue = true,
            Width = 90
        });

        _grid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "CursorAnalyze",
            HeaderText = "分析",
            Text = "Cursor分析",
            UseColumnTextForButtonValue = true,
            Width = 90
        });

        _grid.Columns.Add(new DataGridViewProgressColumn
        {
            Name = "DlProgress",
            HeaderText = "下载进度",
            Width = 120
        });
        _grid.Columns.Add(new DataGridViewProgressColumn
        {
            Name = "ImpProgress",
            HeaderText = "导入进度",
            Width = 120
        });
    }

    private void Grid_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        var hit = _grid.HitTest(e.X, e.Y);
        if (hit.RowIndex < 0) return;
        try
        {
            _grid.ClearSelection();
            _grid.Rows[hit.RowIndex].Selected = true;
            var bugId = _grid.Rows[hit.RowIndex].Cells[0].Value?.ToString() ?? "";
            if (!string.IsNullOrEmpty(bugId))
            {
                SelectBug(bugId);
            }
        }
        catch { }
        _gridMenu.Show(_grid, e.Location);
    }

    private void DeleteSelectedBug()
    {
        try
        {
            if (_grid.SelectedRows.Count == 0) return;
            var bugId = _grid.SelectedRows[0].Cells[0].Value?.ToString() ?? "";
            if (string.IsNullOrEmpty(bugId)) return;

            var confirm = MessageBox.Show(this, $"确认删除缺陷 {bugId}？", "删除缺陷", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            var idx = _items.FindIndex(x => string.Equals(x.BugId, bugId, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) _items.RemoveAt(idx);

            _bugList.BeginUpdate();
            _bugList.Items.Clear();
            foreach (var it in _items) _bugList.Items.Add(it.BugId);
            _bugList.EndUpdate();

            _grid.DataSource = null;
            _grid.DataSource = _items.Select(x => new
            {
                x.BugId,
                x.Snippet,
                x.ApkName,
                x.OsVer
            }).ToList();

            if (_items.Count > 0)
            {
                SelectBug(_items[0].BugId);
            }
            else
            {
                _currentBugId = "";
                _ = SetBaseHtmlAsync("", "-");
                RenderAttachments(new BugItem());
                _ = SetCommentHtmlAsync("", "-");
                RefreshArchiveList();
            }
            SetStatus($"已删除：{bugId}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "删除失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetStatus(string msg) => _statusText.Text = msg;

    private async Task DoLoginAsync()
    {
        try
        {
            SetStatus("打开登录窗口...");
            using var dlg = new LoginForm();
            var token = await dlg.LoginAndGetSiamTgtAsync();
            _token = token;
            _lblAuth.Text = string.IsNullOrEmpty(_token) ? "auth: -" : $"auth: TT ({Mask(_token)})";
            _service.SetToken(_token);
            _update.SetToken(_token);
            // Attachments API requires operator(工号); extracted from TT login cookies.
            _service.SetOperator(dlg.OperatorId);
            _operatorId = dlg.OperatorId ?? "";
            _lblOperator.Text = string.IsNullOrWhiteSpace(dlg.OperatorId) ? "operator: -" : $"operator: {dlg.OperatorId}";
            SetStatus("登录成功");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "TT登录失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("登录失败");
        }
    }

    private static string Mask(string token)
    {
        token ??= "";
        if (token.Length <= 12) return token + "***";
        return token[..8] + "***" + token[^4..];
    }

    private async Task DoFetchAsync()
    {
        var bugId = _txtBugId.Text.Trim();
        if (string.IsNullOrEmpty(bugId))
        {
            MessageBox.Show(this, "请输入 bugId", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (string.IsNullOrEmpty(_token))
        {
            MessageBox.Show(this, "请先点击 TT登录（获取 SIAMTGT）", "需要登录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            SetStatus($"拉取 {bugId} ...");
            var detail = await _service.FetchBugAsync(bugId);
            Upsert(detail);
            SelectBug(bugId);
            SetStatus($"ok: {bugId}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "拉取失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("拉取失败");
        }
    }

    // 下载按钮已移动到表格行内（Download column）。

    private async void Grid_CellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        try
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            var col = _grid.Columns[e.ColumnIndex];
            if (col == null) return;

            var bugId = _grid.Rows[e.RowIndex].Cells[0].Value?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(bugId)) return;

            if (col.Name == "Download")
            {
                await DownloadLogsForBugAsync(bugId.Trim());
            }
            else if (col.Name == "Import")
            {
                await ImportLogsForBugAsync(bugId.Trim());
            }
            else if (col.Name == "CursorAnalyze")
            {
                CursorAnalyzeBug(bugId.Trim());
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "下载失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void CursorAnalyzeBug(string bugId)
    {
        try
        {
            var bug = _items.FirstOrDefault(x => string.Equals(x.BugId, bugId, StringComparison.OrdinalIgnoreCase))
                ?? new BugItem { BugId = bugId };

            var pkg = _cursor.BuildAnalysisPackage(bug, _operatorId);
            if (_cursor.TryOpenInCursor(pkg, out var err))
            {
                SetStatus($"已生成 Cursor 分析包：{pkg.RootDir}（提示词已复制到剪贴板）");
                // Make it explicit for users: even if Cursor opens an empty window, the prompt is ready to paste.
                MessageBox.Show(this,
                    $"已生成分析包并尝试打开 Cursor。\n\n提示词已复制到剪贴板：请在 Cursor Chat 里直接粘贴。\n\n分析包目录：\n{pkg.RootDir}\n\n若 Cursor 里暂时看不到文件，请在左侧打开该目录或打开：\n{Path.GetFileName(pkg.WorkspaceFilePath)} / {Path.GetFileName(pkg.PromptMarkdownPath)}",
                    "Cursor 一键分析",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            SetStatus($"已生成 Cursor 分析包：{pkg.RootDir}");
            MessageBox.Show(this,
                $"已生成分析包，但未能自动启动 Cursor。\n\n你可以手动用 Cursor 打开该目录：\n{pkg.RootDir}\n\n（提示词已写入 cursor_prompt.md / cursor_chat_prompt.txt）\n\n原因：{err}",
                "Cursor 一键分析",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            OpenFolder(pkg.RootDir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cursor 分析失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task ImportLogsForBugAsync(string bugId)
    {
        SetRowProgress(bugId, "ImpProgress", 0);
        var dir = FixedPaths.GetBugDownloadDir(bugId);
        Directory.CreateDirectory(dir);
        _lblDownloadDir.Text = $"下载目录：{dir}";

        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".zip", ".7z", ".rar" };
        var archives = Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(p => exts.Contains(Path.GetExtension(p)))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();

        if (archives.Count == 0)
        {
            MessageBox.Show(this, $"目录下没有找到压缩包：\n{dir}\n\n请先点“下载日志”或手动把压缩包放到该目录。", "未找到日志包", MessageBoxButtons.OK, MessageBoxIcon.Information);
            OpenFolder(dir);
            return;
        }

        string archivePath;
        if (archives.Count == 1)
        {
            archivePath = archives[0];
        }
        else
        {
            using var pick = new PickArchiveForm($"选择导入的日志包（bugId={bugId}）", archives);
            if (pick.ShowDialog(this) != DialogResult.OK) return;
            archivePath = pick.SelectedPath;
            if (string.IsNullOrEmpty(archivePath)) return;
        }

        // Open SF viewer immediately; user can switch candidates inside this window.
        var viewer = new SfViewerForm($"SF Viewer - bugId={bugId}");
        viewer.Show();

        SetStatus("解压并解析中...");
        var importDir = FixedPaths.GetBugImportCacheDir(bugId);
        Directory.CreateDirectory(importDir);
        var extractDir = Path.Combine(importDir, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(extractDir);

        SetRowProgress(bugId, "ImpProgress", 5);
        await WaitForFileReadyAsync(archivePath, TimeSpan.FromSeconds(45));
        SetRowProgress(bugId, "ImpProgress", 8);
        // Fast import: scan archive entries and only extract *candidate files* (sf_logs/android/mp4), not full archive.
        var cand = _scanner.Scan(archivePath);
        if (cand.SfLogsEntries.Count == 0)
        {
            viewer.Text = $"SF Viewer - bugId={bugId} - no sf_logs";
            MessageBox.Show(this, "压缩包里未找到 sf_logs.txt（可能不是SF日志包/结构不同）", "导入失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetRowProgress(bugId, "ImpProgress", 0);
            return;
        }

        // Extract all candidates so user can freely switch after viewer shows up.
        var toExtract = new List<string>();
        toExtract.AddRange(cand.SfLogsEntries);
        toExtract.AddRange(cand.AndroidLogEntries);
        toExtract.AddRange(cand.ScreenRecordEntries);
        toExtract = toExtract.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        await Task.Run(() => _zip.ExtractSelectedEntries(archivePath, extractDir, toExtract, null, p =>
        {
            var pct = 10 + (int)Math.Round(p * 80.0); // 10..90
            try { if (IsHandleCreated) BeginInvoke(() => SetRowProgress(bugId, "ImpProgress", pct)); } catch { }
        }));
        SetRowProgress(bugId, "ImpProgress", 92);

        string Map(string entry) => Path.Combine(extractDir, entry.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
        var sfPaths = cand.SfLogsEntries.Select(Map).Where(File.Exists).ToArray();
        var androidPaths = cand.AndroidLogEntries.Select(Map).Where(File.Exists).ToArray();
        var mp4Paths = cand.ScreenRecordEntries.Select(Map).Where(File.Exists).ToArray();

        viewer.SetCandidates(sfPaths, androidPaths, mp4Paths);
        // auto-load best default immediately (without overwriting candidate dropdowns)
        await viewer.LoadCurrentSelectionAsync();
        SetRowProgress(bugId, "ImpProgress", 100);

        // Optionally open video folder (viewer can be enhanced later to align and display video)
        if (mp4Paths.Length > 0 && File.Exists(mp4Paths[0]))
        {
            try { OpenFolder(Path.GetDirectoryName(mp4Paths[0])!); } catch { }
        }

        SetStatus("导入完成（已打开独立 SF 可视化窗口）");
    }

    private static async Task WaitForFileReadyAsync(string path, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        long lastLen = -1;
        DateTime lastWrite = DateTime.MinValue;
        int stableCount = 0;

        while (DateTime.UtcNow - start < timeout)
        {
            try
            {
                if (!File.Exists(path))
                {
                    await Task.Delay(400);
                    continue;
                }

                var fi = new FileInfo(path);
                var len = fi.Length;
                var lw = fi.LastWriteTimeUtc;
                if (len > 0 && len == lastLen && lw == lastWrite)
                {
                    stableCount++;
                }
                else
                {
                    stableCount = 0;
                }
                lastLen = len;
                lastWrite = lw;

                // stable for ~2s
                if (stableCount >= 2)
                {
                    // can we open and read header?
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (fs.Length < 32) throw new IOException("file too small");
                    var buf = new byte[8];
                    _ = await fs.ReadAsync(buf, 0, buf.Length);
                    return;
                }
            }
            catch
            {
                // ignore and retry
            }
            await Task.Delay(800);
        }

        // still not ready: proceed will likely fail, but error will be shown by caller
    }

    private async Task DownloadLogsForBugAsync(string bugId)
    {
        if (string.IsNullOrEmpty(_token))
        {
            MessageBox.Show(this, "请先点击 TT登录（获取 SIAMTGT）", "需要登录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetStatus($"拉取下载链接 {bugId} ...");
        var detail = await _service.FetchBugAsync(bugId);
        Upsert(detail);
        SelectBug(bugId);

        // Prefer direct preSignedUrl (one-click download)
        var urls = detail.DownloadUrls ?? new List<string>();
        if (urls.Count == 0)
        {
            MessageBox.Show(this, "下载链接为空：Poseidon 没返回 preSignedUrl（可能权限/附件类型/接口变化）。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Show fixed folder (user may save/move there)
        var dir = FixedPaths.GetBugDownloadDir(bugId);
        Directory.CreateDirectory(dir);
        _lblDownloadDir.Text = $"下载目录：{dir}";

        if (urls.Count == 1)
        {
            await StartDownloadToFolderAsync(bugId, urls[0], dir);
            return;
        }

        using var pick = new PickDownloadUrlForm(bugId, urls);
        if (pick.ShowDialog(this) != DialogResult.OK) return;
        if (string.IsNullOrEmpty(pick.SelectedUrl)) return;

        await StartDownloadToFolderAsync(bugId, pick.SelectedUrl, dir);
    }

    private async Task StartDownloadToFolderAsync(string bugId, string url, string folder)
    {
        Directory.CreateDirectory(folder);
        _dlFolder = folder;
        _dlBugId = bugId;
        SetStatus("开始下载（写入固定目录）...");
        SetRowProgress(bugId, "DlProgress", 0);

        await _webDl.EnsureCoreWebView2Async();
        if (!_dlHooked)
        {
            _dlHooked = true;
            _webDl.CoreWebView2.DownloadStarting += (_, e) =>
            {
                try
                {
                    // Default suggested path includes filename; we only force folder.
                    var suggested = e.ResultFilePath ?? "";
                    var fileName = Path.GetFileName(suggested);
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        // fallback name
                        fileName = "poseidon_download_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    }

                    var targetFolder = string.IsNullOrWhiteSpace(_dlFolder) ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) : _dlFolder;
                    Directory.CreateDirectory(targetFolder);
                    var target = Path.Combine(targetFolder, fileName);
                    // avoid overwrite
                    if (File.Exists(target))
                    {
                        var ext = Path.GetExtension(target);
                        var stem = Path.GetFileNameWithoutExtension(target);
                        target = Path.Combine(targetFolder, $"{stem}_{DateTime.Now:HHmmss}{ext}");
                    }

                    e.ResultFilePath = target;
                    e.Handled = true; // suppress default Save As dialog, download directly

                    // Track completion so status won't get stuck at "downloading"
                    var op = e.DownloadOperation;
                    var opBugId = _dlBugId;
                    // reset before any other download starts
                    _dlBugId = "";

                    void Ui(Action a)
                    {
                        try
                        {
                            if (IsHandleCreated) BeginInvoke(a);
                            else a();
                        }
                        catch { }
                    }

                    Ui(() =>
                    {
                        _activeDownloads[target] = opBugId;
                        SetStatus($"[{opBugId}] 下载中：{Path.GetFileName(target)}");
                    });
                    Ui(() => SetRowProgress(opBugId, "DlProgress", 1));

                    op.BytesReceivedChanged += (_, _) =>
                    {
                        try
                        {
                            // TotalBytesToReceive is nullable in WebView2 .NET bindings
                            if (op.TotalBytesToReceive is ulong total && total > 0)
                            {
                                var pctD = Math.Round(op.BytesReceived * 100.0 / (double)total);
                                var pct = (int)Math.Clamp(pctD, 0.0, 100.0);
                                Ui(() => SetRowProgress(opBugId, "DlProgress", pct));
                                // Some cases report 100% before StateChanged fires; don't leave "下载中" hanging.
                                if (pct >= 100)
                                {
                                    Ui(() => SetStatus($"[{opBugId}] 下载写入完成：{Path.GetFileName(op.ResultFilePath ?? target)}（等待完成回调）"));
                                }
                            }
                        }
                        catch { }
                    };

                    op.StateChanged += (_, _) =>
                    {
                        try
                        {
                            var st = op.State;
                            if (st == Microsoft.Web.WebView2.Core.CoreWebView2DownloadState.Completed)
                            {
                                Ui(() =>
                                {
                                    var fp = op.ResultFilePath ?? target;
                                    _activeDownloads.Remove(fp);
                                    SetStatus($"[{opBugId}] 下载完成：{Path.GetFileName(fp)}");
                                    SetRowProgress(opBugId, "DlProgress", 100);
                                    RefreshArchiveList();
                                });
                            }
                            else if (st == Microsoft.Web.WebView2.Core.CoreWebView2DownloadState.Interrupted)
                            {
                                Ui(() =>
                                {
                                    var fp = op.ResultFilePath ?? target;
                                    _activeDownloads.Remove(fp);
                                    SetStatus($"[{opBugId}] 下载中断：{op.InterruptReason}");
                                    // leave progress as-is
                                });
                            }
                        }
                        catch { }
                    };
                }
                catch (Exception ex)
                {
                    try
                    {
                        e.Handled = false;
                    }
                    catch { }
                    SetStatus("下载设置路径失败：" + ex.Message);
                }
            };
        }

        // Kick off download
        _webDl.CoreWebView2.Navigate(url);
        // Give some time; actual completion is handled by browser download manager.
        OpenFolder(folder);
        // refresh archive list so user sees it after download completes
        RefreshArchiveList();
    }

    private void Upsert(BugItem item)
    {
        var idx = _items.FindIndex(x => x.BugId == item.BugId);
        if (idx >= 0) _items[idx] = item;
        else _items.Insert(0, item);

        _bugList.BeginUpdate();
        _bugList.Items.Clear();
        foreach (var it in _items) _bugList.Items.Add(it.BugId);
        _bugList.EndUpdate();

        _grid.DataSource = null;
        _grid.DataSource = _items.Select(x => new
        {
            x.BugId,
            x.Snippet,
            x.ApkName,
            x.OsVer
        }).ToList();
    }

    private void SelectBug(string bugId)
    {
        _currentBugId = bugId;
        var idx = _items.FindIndex(x => x.BugId == bugId);
        if (idx < 0) return;
        _bugList.SelectedIndex = idx;

        // also select in grid
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Cells.Count > 0 && string.Equals(row.Cells[0].Value?.ToString(), bugId, StringComparison.OrdinalIgnoreCase))
            {
                row.Selected = true;
                _grid.FirstDisplayedScrollingRowIndex = row.Index;
                break;
            }
        }
        RenderDetail(_items[idx]);
        RefreshArchiveList();
    }

    private void SelectFromList()
    {
        var bugId = _bugList.SelectedItem?.ToString() ?? "";
        if (string.IsNullOrEmpty(bugId)) return;
        var it = _items.FirstOrDefault(x => x.BugId == bugId);
        if (it != null) RenderDetail(it);
    }

    private void SelectFromGrid()
    {
        if (_grid.SelectedRows.Count == 0) return;
        var bugId = _grid.SelectedRows[0].Cells[0].Value?.ToString() ?? "";
        if (string.IsNullOrEmpty(bugId)) return;
        var it = _items.FirstOrDefault(x => x.BugId == bugId);
        if (it != null)
        {
            var idx = _items.IndexOf(it);
            if (idx >= 0 && _bugList.SelectedIndex != idx) _bugList.SelectedIndex = idx;
            RenderDetail(it);
        }
    }

    private void RenderDetail(BugItem it)
    {
        _ = SetBaseHtmlAsync(it.BaseHtml, it.BaseInfo);
        RenderAttachments(it);
        _ = SetCommentHtmlAsync(it.CommentHtml, it.CommentInfo);
    }

    private async Task SetBaseHtmlAsync(string html, string fallbackText)
    {
        try
        {
            await _webBase.EnsureCoreWebView2Async();
            var content = !string.IsNullOrWhiteSpace(html) ? html : HtmlFromPlainText(fallbackText ?? "-");
            _webBase.CoreWebView2.NavigateToString(content);
        }
        catch
        {
            // ignore
        }
    }

    private static Bitmap? _dlIcon;

    private void RenderAttachments(BugItem it)
    {
        _attachFlow.SuspendLayout();
        _attachFlow.Controls.Clear();

        if (!string.IsNullOrWhiteSpace(it.AttachError))
        {
            _lblAttachHint.Text = "附件（失败）";
            _attachFlow.Controls.Add(new Label
            {
                AutoSize = true,
                Text = it.AttachError,
                ForeColor = Color.DarkRed,
                MaximumSize = new Size(_attachFlow.Width - 30, 0)
            });
            _attachFlow.ResumeLayout();
            return;
        }

        var items = it.Attachments ?? Array.Empty<AttachmentItem>();
        _lblAttachHint.Text = $"附件（{items.Length}）";
        if (items.Length == 0)
        {
            _attachFlow.Controls.Add(new Label { AutoSize = true, Text = "（无附件）", ForeColor = SystemColors.GrayText });
            _attachFlow.ResumeLayout();
            return;
        }

        _dlIcon ??= CreateDownloadIcon();

        foreach (var a in items)
        {
            var card = new Panel
            {
                Width = Math.Max(300, _attachFlow.ClientSize.Width - 30),
                Height = 56,
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(8),
                BackColor = Color.White
            };

            var btn = new Button
            {
                Width = 40,
                Height = 40,
                Image = _dlIcon,
                FlatStyle = FlatStyle.Flat
            };
            btn.FlatAppearance.BorderSize = 0;

            var name = new Label
            {
                AutoSize = false,
                Left = 52,
                Top = 6,
                Width = card.Width - 60,
                Height = 18,
                Text = string.IsNullOrWhiteSpace(a.Name) ? "(unnamed)" : a.Name,
                Font = new Font(Font, FontStyle.Bold)
            };

            var meta = new Label
            {
                AutoSize = false,
                Left = 52,
                Top = 28,
                Width = card.Width - 60,
                Height = 18,
                ForeColor = SystemColors.GrayText,
                Text = $"{PrettySize(a.SizeBytes)}  {a.Time}".Trim()
            };

            btn.Click += async (_, _) => await DownloadAttachmentAsync(it.BugId, a, btn);
            name.Click += async (_, _) => await DownloadAttachmentAsync(it.BugId, a, btn);

            card.Controls.Add(btn);
            card.Controls.Add(name);
            card.Controls.Add(meta);
            _attachFlow.Controls.Add(card);
        }

        _attachFlow.ResumeLayout();
    }

    private async Task DownloadAttachmentAsync(string bugId, AttachmentItem a, Control? disableCtl = null)
    {
        if (string.IsNullOrWhiteSpace(a.DownloadUrl))
        {
            MessageBox.Show(this, "该附件没有下载链接（downloadUrl 为空）", "无法下载", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var dir = Path.Combine(FixedPaths.GetBugDownloadDir(bugId), "attachments");
        Directory.CreateDirectory(dir);
        var fileName = SanitizeFileName(string.IsNullOrWhiteSpace(a.Name) ? "attachment.bin" : a.Name);
        var dest = Path.Combine(dir, fileName);

        try
        {
            if (disableCtl != null) disableCtl.Enabled = false;
            SetStatus($"下载附件中：{fileName}");
            await _service.DownloadFileAsync(a.DownloadUrl, dest);
            SetStatus($"附件已下载：{dest}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "下载失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("附件下载失败");
        }
        finally
        {
            if (disableCtl != null) disableCtl.Enabled = true;
        }
    }

    private static string SanitizeFileName(string s)
    {
        s ??= "attachment.bin";
        foreach (var ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, '_');
        return s.Length > 120 ? s[..120] : s;
    }

    private static string PrettySize(long bytes)
    {
        if (bytes <= 0) return "";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double d = bytes;
        int idx = 0;
        while (d >= 1024 && idx < units.Length - 1) { d /= 1024; idx++; }
        return $"{d:0.##}{units[idx]}";
    }

    private static Bitmap CreateDownloadIcon()
    {
        var bmp = new Bitmap(20, 20);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        using var p = new Pen(Color.FromArgb(50, 50, 50), 2);
        g.DrawLine(p, 10, 3, 10, 13);
        g.DrawLine(p, 6, 9, 10, 13);
        g.DrawLine(p, 14, 9, 10, 13);
        g.DrawLine(p, 4, 16, 16, 16);
        return bmp;
    }

    private static Bitmap CreateAlogIcon()
    {
        var bmp = new Bitmap(18, 18);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        using var bg = new SolidBrush(Color.FromArgb(35, 99, 198));
        using var fg = new SolidBrush(Color.White);
        g.FillEllipse(bg, 1, 1, 16, 16);
        using var f = new Font("Segoe UI", 8, FontStyle.Bold, GraphicsUnit.Point);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString("A", f, fg, new RectangleF(0, 0, 18, 18), sf);
        return bmp;
    }

    private async Task SetCommentHtmlAsync(string html, string fallbackText)
    {
        try
        {
            await _webComment.EnsureCoreWebView2Async();
            var content = !string.IsNullOrWhiteSpace(html) ? html : HtmlFromPlainText(fallbackText ?? "-");
            _webComment.CoreWebView2.NavigateToString(content);
        }
        catch
        {
            // ignore
        }
    }

    private static string HtmlFromPlainText(string text)
    {
        text ??= "";
        text = System.Net.WebUtility.HtmlEncode(text);
        text = text.Replace("\r\n", "\n").Replace("\n", "<br/>");
        return "<!doctype html><html><head><meta charset=\"utf-8\"/></head><body style=\"font-family:Segoe UI,Microsoft YaHei;\">" + text + "</body></html>";
    }

    private void RefreshArchiveList()
    {
        var bugId = _currentBugId;
        var dir = string.IsNullOrEmpty(bugId) ? FixedPaths.GetBaseDownloadDir() : FixedPaths.GetBugDownloadDir(bugId);
        Directory.CreateDirectory(dir);
        _lblDownloadDir.Text = $"下载目录：{dir}";

        _archiveList.BeginUpdate();
        _archiveList.Items.Clear();
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".zip", ".7z", ".rar" };
        var files = Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(p => exts.Contains(Path.GetExtension(p)))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
        foreach (var f in files) _archiveList.Items.Add(Path.GetFileName(f));
        _archiveList.EndUpdate();
    }

    private void OpenDownloadDir()
    {
        var bugId = _currentBugId;
        var dir = string.IsNullOrEmpty(bugId) ? FixedPaths.GetBaseDownloadDir() : FixedPaths.GetBugDownloadDir(bugId);
        Directory.CreateDirectory(dir);
        OpenFolder(dir);
    }

    private async Task PickArchiveAndLoadAsync()
    {
        var bugId = _currentBugId;
        if (string.IsNullOrEmpty(bugId))
        {
            MessageBox.Show(this, "请先拉取/选择一个 bugId", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var dir = FixedPaths.GetBugDownloadDir(bugId);
        Directory.CreateDirectory(dir);

        using var ofd = new OpenFileDialog
        {
            Title = "选择日志压缩包（zip/7z/rar）",
            InitialDirectory = dir,
            Filter = "Archives (*.zip;*.7z;*.rar)|*.zip;*.7z;*.rar|All files (*.*)|*.*"
        };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        await LoadArchiveAsync(ofd.FileName);
    }

    private async Task LoadSelectedArchiveAsync()
    {
        var bugId = _currentBugId;
        if (string.IsNullOrEmpty(bugId)) return;
        if (_archiveList.SelectedItem == null) return;
        var dir = FixedPaths.GetBugDownloadDir(bugId);
        var path = Path.Combine(dir, _archiveList.SelectedItem.ToString() ?? "");
        if (!File.Exists(path))
        {
            MessageBox.Show(this, "文件不存在，点击刷新", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        await LoadArchiveAsync(path);
    }

    private async Task LoadArchiveAsync(string archivePath)
    {
        var bugId = string.IsNullOrEmpty(_currentBugId) ? "unknown" : _currentBugId;
        try
        {
            SetStatus("解压日志包...");
            var importDir = FixedPaths.GetBugImportCacheDir(bugId);
            Directory.CreateDirectory(importDir);
            var extractDir = Path.Combine(importDir, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(extractDir);

            _zip.ExtractArchive(archivePath, extractDir, msg => SetStatus(msg));

            var all = _discovery.DiscoverAll(extractDir);
            if (all.SfLogsPaths.Count == 0)
                throw new Exception("解压后未找到 display/sf/raw/**/sf_logs.txt");

            var sf = all.SfLogsPaths.First();
            string? android = all.AndroidLogPaths.FirstOrDefault();
            string? mp4 = all.ScreenRecordPaths.FirstOrDefault();

            if (all.SfLogsPaths.Count > 1 || all.AndroidLogPaths.Count > 1 || all.ScreenRecordPaths.Count > 1)
            {
                using var pick = new PickArtifactsForm(all);
                if (pick.ShowDialog(this) != DialogResult.OK) return;
                sf = pick.SelectedSfLogsPath ?? sf;
                android = pick.SelectedAndroidLogPath;
                mp4 = pick.SelectedScreenRecordPath;
            }

            ShowInfoInAttachPanel(
                "导入信息",
                $"archive: {archivePath}\nextract: {extractDir}\n\n" +
                $"sf_logs: {sf}\n" +
                $"android: {android}\n" +
                $"screen_record: {mp4}\n\n" +
                "sf_logs candidates:\n" + string.Join("\n", all.SfLogsPaths.Take(20)) +
                (all.SfLogsPaths.Count > 20 ? "\n...(more)" : "") +
                "\n\nandroid candidates:\n" + string.Join("\n", all.AndroidLogPaths.Take(20)) +
                (all.AndroidLogPaths.Count > 20 ? "\n...(more)" : "") +
                "\n\nmp4 candidates:\n" + string.Join("\n", all.ScreenRecordPaths.Take(20)) +
                (all.ScreenRecordPaths.Count > 20 ? "\n...(more)" : "")
            );

            // Open separate SF viewer window (do not render SF inside this tool)
            var viewer = new SfViewerForm($"SF Viewer - bugId={bugId} - loading...");
            viewer.Show();
            await viewer.LoadAsync(sf, android);
            viewer.Text = $"SF Viewer - bugId={bugId} - {Path.GetFileName(Path.GetDirectoryName(sf) ?? sf)}";

            if (!string.IsNullOrEmpty(mp4) && File.Exists(mp4))
            {
                // 暂时外部打开目录，后续可对齐并喂给 viewer 的 video 逻辑
                try { OpenFolder(Path.GetDirectoryName(mp4)!); } catch { }
            }

            SetStatus("已打开独立 SF 可视化窗口");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "加载失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("加载失败");
        }
    }

    private static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }

    private async Task CheckUpdateAsync()
    {
        try
        {
            _btnUpdate.Enabled = false;
            SetStatus("检查更新中...");

            var latest = await _update.GetLatestReleaseAsync();
            if (latest == null)
            {
                MessageBox.Show(this,
                    "暂无可用更新。",
                    "检查更新",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                SetStatus("暂无可用更新");
                return;
            }

            var cur = AppUpdateService.GetCurrentVersion();
            var v = AppUpdateService.ParseTagVersion(latest.Tag);
            if (v == null)
            {
                MessageBox.Show(this, $"无法解析版本号：tag={latest.Tag}", "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                SetStatus("检查更新失败");
                return;
            }

            if (v <= cur)
            {
                MessageBox.Show(this, $"已是最新版本：{cur}\n\nRelease: {latest.Tag}", "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                SetStatus("已是最新版本");
                return;
            }

            var dr = MessageBox.Show(this,
                $"发现新版本：{v}\n当前版本：{cur}\n\n是否下载并更新？\n\n{latest.HtmlUrl}",
                "发现更新",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (dr != DialogResult.Yes)
            {
                SetStatus("已取消更新");
                return;
            }

            SetStatus("下载更新中...");
            await _update.DownloadAndApplyUpdateAsync(latest.AssetUrl, latest.AssetName);
            SetStatus("准备更新（即将退出）");
            await Task.Delay(300);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "更新失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("更新失败");
        }
        finally
        {
            _btnUpdate.Enabled = true;
        }
    }

    private static void OpenFolder(string dir)
    {
        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    private void ShowInfoInAttachPanel(string title, string text)
    {
        try
        {
            _lblAttachHint.Text = title;
            _attachFlow.Controls.Clear();
            var box = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Width = Math.Max(300, _attachFlow.ClientSize.Width - 30),
                Height = 180,
                Text = text ?? ""
            };
            _attachFlow.Controls.Add(box);
        }
        catch
        {
            // ignore
        }
    }

    private void SetRowProgress(string bugId, string colName, int percent)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(bugId)) return;
            percent = Math.Clamp(percent, 0, 100);
            foreach (DataGridViewRow row in _grid.Rows)
            {
                var id = row.Cells[0].Value?.ToString() ?? "";
                if (!string.Equals(id, bugId, StringComparison.OrdinalIgnoreCase)) continue;
                row.Cells[colName].Value = percent;
                _grid.InvalidateRow(row.Index);
                break;
            }
        }
        catch
        {
            // ignore
        }
    }

    // SF viewer is now opened in separate windows (SfViewerForm).
}

internal sealed class LoginForm : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly Button _btnDone = new() { Text = "已登录完成（提取SIAMTGT）", Dock = DockStyle.Bottom, Height = 40 };
    private readonly Label _lblState = new() { Text = "状态：等待登录…", Dock = DockStyle.Bottom, Height = 22, Padding = new Padding(10, 0, 10, 0) };
    private readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 900 };
    private string _detectedToken = "";
    private string _detectedOperator = "";

    public string OperatorId => _detectedOperator;

    public LoginForm()
    {
        Text = "TT登录（Noah）";
        Width = 900;
        Height = 700;
        _btnDone.Enabled = false;
        Controls.Add(_web);
        Controls.Add(_lblState);
        Controls.Add(_btnDone);
        StartPosition = FormStartPosition.CenterParent;

        Shown += async (_, _) =>
        {
            await _web.EnsureCoreWebView2Async();
            _web.CoreWebView2.Navigate("https://noah.myoas.com/");
            _pollTimer.Start();
        };

        FormClosed += (_, _) =>
        {
            try { _pollTimer.Stop(); } catch { }
            try { _pollTimer.Dispose(); } catch { }
        };

        _pollTimer.Tick += async (_, _) =>
        {
            try
            {
                var token = await TryExtractSiamTgtAsync();
                var op = await TryExtractOperatorIdAsync();
                if (!string.IsNullOrWhiteSpace(op)) _detectedOperator = op;
                if (!string.IsNullOrEmpty(token) && token.StartsWith("TGT-", StringComparison.OrdinalIgnoreCase))
                {
                    _detectedToken = token;
                    _btnDone.Enabled = true;
                    _lblState.Text = $"状态：已检测到 SIAMTGT = {Mask(token)}" +
                                     (string.IsNullOrWhiteSpace(_detectedOperator) ? "" : $" | 工号={_detectedOperator}");
                }
                else
                {
                    _lblState.Text = "状态：未检测到 SIAMTGT（请确认已完成登录并回到此窗口）";
                }
            }
            catch (Exception ex)
            {
                _lblState.Text = $"状态：检测异常：{ex.Message}";
            }
        };

        _btnDone.Click += async (_, _) =>
        {
            try
            {
                var token = !string.IsNullOrEmpty(_detectedToken) ? _detectedToken : await TryExtractSiamTgtAsync();
                if (string.IsNullOrEmpty(token))
                {
                    MessageBox.Show(this, "未找到 SIAMTGT，请确认已在页面完成登录。", "未登录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                var op = !string.IsNullOrWhiteSpace(_detectedOperator) ? _detectedOperator : await TryExtractOperatorIdAsync();
                if (!string.IsNullOrWhiteSpace(op)) _detectedOperator = op;
                Tag = token;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "提取失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
    }

    public async Task<string> LoginAndGetSiamTgtAsync()
    {
        var dr = ShowDialog();
        if (dr != DialogResult.OK) throw new Exception("Login cancelled.");
        var token = Tag?.ToString() ?? "";
        if (string.IsNullOrEmpty(token)) throw new Exception("SIAMTGT not found.");
        return await Task.FromResult(token);
    }

    private async Task<string> TryExtractSiamTgtAsync()
    {
        if (_web.CoreWebView2 == null) return "";
        // Cookies are per-domain; try both noah and g-agile-dms.
        foreach (var domain in new[]
                 {
                     "https://noah.myoas.com/",
                     "https://g-agile-dms.myoas.com/",
                     "https://alm.myoas.com/",
                     "https://myaos.com/", // best-effort (may be invalid but harmless)
                 })
        {
            try
            {
                var cookies = await _web.CoreWebView2.CookieManager.GetCookiesAsync(domain);
                var c = cookies.FirstOrDefault(x => string.Equals(x.Name, "SIAMTGT", StringComparison.OrdinalIgnoreCase));
                if (c != null && !string.IsNullOrEmpty(c.Value)) return c.Value;
            }
            catch
            {
                // ignore invalid domain formats
            }
        }
        return "";
    }

    private async Task<string> TryExtractOperatorIdAsync()
    {
        if (_web.CoreWebView2 == null) return "";

        // TT账号(工号) is often stored by sensorsdata cookie as distinct_id, e.g. {"distinct_id":"80415760", ...}
        foreach (var domain in new[]
                 {
                     "https://noah.myoas.com/",
                     "https://g-agile-dms.myoas.com/",
                     "https://alm.myoas.com/",
                 })
        {
            try
            {
                var cookies = await _web.CoreWebView2.CookieManager.GetCookiesAsync(domain);
                foreach (var c in cookies)
                {
                    if (string.IsNullOrEmpty(c?.Name) || string.IsNullOrEmpty(c.Value)) continue;
                    var name = c.Name ?? "";
                    var raw = c.Value ?? "";
                    var val = MultiUrlDecode(raw);

                    // Fast path: sensorsdata cookies are most likely, but in some envs the key can appear elsewhere.
                    // We scan all cookies but prefer sensorsdata hits.
                    var isLikely = name.Contains("sensorsdata", StringComparison.OrdinalIgnoreCase);

                    // 1) regex (works for non-json or partially-escaped json)
                    var m = System.Text.RegularExpressions.Regex.Match(val, @"""distinct_id""\s*:\s*""(\d{5,})""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (m.Success) return m.Groups[1].Value;

                    // 2) try parse json if it looks like json object
                    if (isLikely && val.Contains("{") && val.Contains("distinct_id", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(val);
                            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                                doc.RootElement.TryGetProperty("distinct_id", out var did))
                            {
                                var s = did.ValueKind == System.Text.Json.JsonValueKind.String ? (did.GetString() ?? "") : did.ToString();
                                s = (s ?? "").Trim();
                                if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^\d{5,}$"))
                                    return s;
                            }
                        }
                        catch
                        {
                            // ignore and continue
                        }
                    }
                }
            }
            catch
            {
                // ignore invalid domain formats / cookie manager errors
            }
        }
        return "";
    }

    private static string MultiUrlDecode(string s)
    {
        s ??= "";
        for (var i = 0; i < 3; i++)
        {
            string d;
            try { d = System.Net.WebUtility.UrlDecode(s); }
            catch { break; }
            if (string.Equals(d, s, StringComparison.Ordinal)) break;
            s = d ?? "";
        }
        return s;
    }

    private static string Mask(string token)
    {
        token ??= "";
        if (token.Length <= 12) return token + "***";
        return token[..8] + "***" + token[^4..];
    }
}

internal sealed class AddCommentForm : Form
{
    private readonly TextBox _txt = new()
    {
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        WordWrap = true,
        Dock = DockStyle.Fill
    };
    private readonly CheckBox _chkBr = new()
    {
        Text = "将换行自动转换为 <br>（适配 Noah 富文本）",
        Checked = true,
        AutoSize = true,
        Dock = DockStyle.Top
    };
    private readonly Button _btnOk = new() { Text = "发布", DialogResult = DialogResult.OK, Width = 90 };
    private readonly Button _btnCancel = new() { Text = "取消", DialogResult = DialogResult.Cancel, Width = 90 };

    public string CommentText => _txt.Text ?? "";
    public bool ConvertNewlinesToBr => _chkBr.Checked;

    public AddCommentForm(string bugId, string operatorId)
    {
        Text = $"发表评论 - Bug {bugId}";
        Width = 760;
        Height = 520;
        StartPosition = FormStartPosition.CenterParent;

        var hint = new Label
        {
            Text = $"评论人（工号）：{operatorId}    BugId：{bugId}",
            AutoSize = true,
            Dock = DockStyle.Top,
            ForeColor = SystemColors.GrayText,
            Padding = new Padding(2, 2, 2, 6)
        };

        var btnRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 46,
            Padding = new Padding(10, 8, 10, 8),
            WrapContents = false
        };
        btnRow.Controls.Add(_btnOk);
        btnRow.Controls.Add(_btnCancel);

        Controls.Add(_txt);
        Controls.Add(_chkBr);
        Controls.Add(hint);
        Controls.Add(btnRow);

        AcceptButton = _btnOk;
        CancelButton = _btnCancel;
    }
}

public sealed class BugItem
{
    public string BugId { get; init; } = "";
    public string Snippet { get; init; } = "";
    public string ApkName { get; init; } = "";
    public string OsVer { get; init; } = "";
    public string BaseInfo { get; init; } = "";
    public string BaseHtml { get; init; } = "";
    public string LogInfo { get; init; } = "";
    public string DownloadLinks { get; init; } = "";
    public List<string> DownloadUrls { get; init; } = new();
    public string ShareUrl { get; init; } = "";

    // UI placeholders to mimic the old tool tab layout
    public string AttachError { get; init; } = "";
    public BugLensLite.Services.AttachmentItem[] Attachments { get; init; } = Array.Empty<BugLensLite.Services.AttachmentItem>();
    public string CommentInfo { get; init; } = "";
    public string CommentHtml { get; init; } = "";
}


