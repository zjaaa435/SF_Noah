using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BugLensLite.Services;

public sealed class CursorIntegrationService
{
    public sealed record AnalysisPackage(
        string RootDir,
        string WorkspaceFilePath,
        string PromptMarkdownPath,
        string ChatPromptText
    );

    public AnalysisPackage BuildAnalysisPackage(BugLensLite.BugItem bug, string? operatorId)
    {
        if (bug == null) throw new ArgumentNullException(nameof(bug));
        var bugId = (bug.BugId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(bugId)) throw new Exception("bugId 为空");
        operatorId ??= "";

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BugLensLite",
            "cursor",
            bugId,
            DateTime.Now.ToString("yyyyMMdd_HHmmss")
        );
        Directory.CreateDirectory(root);

        // 1) Generate .cursorrules for Cursor (project-level rules)
        var cursorrulesPath = Path.Combine(root, ".cursorrules");
        File.WriteAllText(cursorrulesPath, BuildCursorRulesText(), Encoding.UTF8);

        // 1.5) Locate WMS(window) docs only (exclude WiFi / exclude knowledge_base)
        var docsRoot = FindWindowDocsRoot();
        var curatedDocsDir = Path.Combine(root, "window_docs");
        var copiedDocCount = 0;
        if (!string.IsNullOrWhiteSpace(docsRoot) && Directory.Exists(docsRoot))
        {
            copiedDocCount = CopyWindowDocs(docsRoot, curatedDocsDir);
        }

        // 1.6) Copy Noah upload helpers into the analysis package (so Cursor can run them locally)
        var uploaderToolsRel = CopyUploaderTools(root, operatorId);

        // 2) Collect local artifact paths (download dir + latest import dir)
        var downloadDir = FixedPaths.GetBugDownloadDir(bugId);
        var importBase = FixedPaths.GetBugImportCacheDir(bugId);
        var latestImportDir = FindLatestExtractDir(importBase);
        var latestArchive = FindLatestArchive(downloadDir);

        var evidenceList = new List<(string Title, string Path)>();
        if (Directory.Exists(downloadDir)) evidenceList.Add(("下载目录", downloadDir));
        if (!string.IsNullOrWhiteSpace(latestArchive) && File.Exists(latestArchive)) evidenceList.Add(("日志压缩包（下载的原始包）", latestArchive));
        if (!string.IsNullOrWhiteSpace(latestImportDir) && Directory.Exists(latestImportDir)) evidenceList.Add(("最近一次导入目录", latestImportDir));
        if (!string.IsNullOrWhiteSpace(docsRoot) && Directory.Exists(docsRoot)) evidenceList.Add(("数字人窗口文档（源目录）", docsRoot));
        if (Directory.Exists(curatedDocsDir) && copiedDocCount > 0) evidenceList.Add(($"数字人窗口文档（已整理，{copiedDocCount}份）", curatedDocsDir));
        if (uploaderToolsRel.Count > 0) evidenceList.Add(("NOAH 上传工具（已复制到分析包）", Path.Combine(root, "tools")));

        // 3) Best-effort copy key logs into the package (avoid massive copy)
        var copied = new List<(string Name, string Path)>();
        var logsDir = Path.Combine(root, "logs");
        Directory.CreateDirectory(logsDir);
        TryCopyKeyLogs(latestImportDir, logsDir, copied);

        // 3.5) Best-effort copy the *whole archive* if it's not too big (user said "日志" means the archive).
        var copiedArchive = TryCopyArchive(latestArchive, Path.Combine(root, "archives"));
        if (!string.IsNullOrWhiteSpace(copiedArchive) && File.Exists(copiedArchive))
        {
            copied.Add((Path.GetFileName(copiedArchive), copiedArchive));
        }

        // 3.8) Write a VS Code / Cursor workspace to mount "digital human" + bug artifacts together
        var wsPath = Path.Combine(root, $"SF_Noah_bug_{bugId}.code-workspace");
        File.WriteAllText(wsPath, BuildWorkspaceJson(root, curatedDocsDir, downloadDir, latestImportDir), Encoding.UTF8);

        // 4) Prompt files
        var promptMdPath = Path.Combine(root, "cursor_prompt.md");
        var chatPrompt = BuildChatPrompt(bug, evidenceList, copied, curatedDocsDir, uploaderToolsRel);
        File.WriteAllText(promptMdPath, BuildPromptMarkdown(bug, evidenceList, copied, chatPrompt), Encoding.UTF8);

        var chatTxtPath = Path.Combine(root, "cursor_chat_prompt.txt");
        File.WriteAllText(chatTxtPath, chatPrompt, Encoding.UTF8);

        // 5) Copy to clipboard (so user can paste to Cursor chat immediately)
        try
        {
            Clipboard.SetText(chatPrompt);
        }
        catch
        {
            // Clipboard may fail (RDP/permission). It's fine; prompt file still exists.
        }

        return new AnalysisPackage(root, wsPath, promptMdPath, chatPrompt);
    }

    public bool TryOpenInCursor(AnalysisPackage pkg, out string error)
    {
        error = "";
        if (pkg == null) { error = "分析包为空"; return false; }

        var root = pkg.RootDir;
        var ws = pkg.WorkspaceFilePath;
        var prompt = pkg.PromptMarkdownPath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) { error = "分析目录不存在"; return false; }

        // Prefer opening workspace + prompt together (so user won't see an "empty" window).
        if (!string.IsNullOrWhiteSpace(ws) && File.Exists(ws))
        {
            if (!string.IsNullOrWhiteSpace(prompt) && File.Exists(prompt))
            {
                if (TryStartProcess("cursor", $"\"{ws}\" \"{prompt}\"")) return true;
            }
            if (TryStartProcess("cursor", $"\"{ws}\"")) return true;
        }
        if (TryStartProcess("cursor", $"\"{root}\" \"{prompt}\"")) return true;
        if (TryStartProcess("cursor", $"\"{root}\"")) return true;

        // Fall back to well-known installation paths on Windows.
        foreach (var exe in GetCandidateCursorExePaths())
        {
            if (!File.Exists(exe)) continue;
            if (!string.IsNullOrWhiteSpace(ws) && File.Exists(ws))
            {
                if (!string.IsNullOrWhiteSpace(prompt) && File.Exists(prompt))
                {
                    if (TryStartProcess(exe, $"\"{ws}\" \"{prompt}\"")) return true;
                }
                if (TryStartProcess(exe, $"\"{ws}\"")) return true;
            }
            if (TryStartProcess(exe, $"\"{root}\" \"{prompt}\"")) return true;
            if (TryStartProcess(exe, $"\"{root}\"")) return true;
        }

        // Final fallback: open the workspace file (and prompt) via file association.
        // If Cursor is installed and registered, Windows will open it correctly.
        try
        {
            if (!string.IsNullOrWhiteSpace(ws) && File.Exists(ws))
            {
                Process.Start(new ProcessStartInfo { FileName = ws, UseShellExecute = true });
                if (!string.IsNullOrWhiteSpace(prompt) && File.Exists(prompt))
                {
                    Process.Start(new ProcessStartInfo { FileName = prompt, UseShellExecute = true });
                }
                return true;
            }
        }
        catch
        {
            // ignore
        }

        error = "未检测到 Cursor（或无法启动）。已生成分析包，可手动用 Cursor 打开该目录。";
        return false;
    }

    private static IEnumerable<string> GetCandidateCursorExePaths()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // Common variants observed for Cursor installs
        yield return Path.Combine(local, "Programs", "Cursor", "Cursor.exe");
        yield return Path.Combine(local, "Programs", "cursor", "Cursor.exe");
        yield return Path.Combine(local, "Programs", "Cursor", "resources", "app", "bin", "cursor.cmd");
        yield return Path.Combine(local, "Programs", "cursor", "resources", "app", "bin", "cursor.cmd");
    }

    private static bool TryStartProcess(string fileName, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = true,
                WorkingDirectory = Environment.CurrentDirectory
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildCursorRulesText()
    {
        // Inline essential WMS-only rules so Cursor won't route into WiFi.
        var assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets", "agent_rules");
        var baseRules = TryReadAssetRule(Path.Combine(assetsDir, "core", "base_rules.cursorrules"));
        var router = TryReadAssetRule(Path.Combine(assetsDir, "core", "router.cursorrules"));
        var orchestration = TryReadAssetRule(Path.Combine(assetsDir, "core", "orchestration.cursorrules"));
        var review = TryReadAssetRule(Path.Combine(assetsDir, "core", "review_synthesis.cursorrules"));
        var wmsDebug = TryReadAssetRule(Path.Combine(assetsDir, "analysisRule", "wms", "wms_debug.cursorrules"));
        var commentRule = TryReadAssetRule(Path.Combine(assetsDir, "analysisRule", "common", "defect_comment_format.cursorrules"));

        var sb = new StringBuilder();
        sb.AppendLine("# SF_Noah 窗口（WMS）数字人规则（自动生成）");
        sb.AppendLine();
        sb.AppendLine("最高优先级约束：只允许做窗口（WMS）领域分析。禁止将问题路由到 WiFi/WLAN 规则或文档。");
        sb.AppendLine("- 必须以“日志证据链”为准：时间点 + 关键字 + 原始日志压缩包/解压目录路径。");
        sb.AppendLine("- 必须优先引用 workspace 下的 window_docs（窗口领域文档/指南）。");
        sb.AppendLine("- 输出结论后：必须询问用户是否将结果上传到 NOAH；仅在用户确认后才允许执行上传。");
        sb.AppendLine();
        sb.AppendLine("提示：工具已在发布物中打包了完整规则目录，可在下列路径查看：");
        sb.AppendLine($"- {assetsDir}");
        sb.AppendLine();

        void AppendBlock(string title, string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return;
            sb.AppendLine($"---");
            sb.AppendLine($"## {title}");
            sb.AppendLine(content.Trim());
            sb.AppendLine();
        }

        AppendBlock("core/base_rules.cursorrules", baseRules);
        AppendBlock("core/router.cursorrules", router);
        AppendBlock("core/orchestration.cursorrules", orchestration);
        AppendBlock("core/review_synthesis.cursorrules", review);
        AppendBlock("analysisRule/wms/wms_debug.cursorrules", wmsDebug);
        AppendBlock("analysisRule/common/defect_comment_format.cursorrules（上传前必须询问用户）", commentRule);

        return sb.ToString();
    }

    private static string BuildWorkspaceJson(string analysisRoot, string? curatedDocsDir, string downloadDir, string? latestImportDir)
    {
        // Cursor is compatible with VSCode workspaces: open one file to mount multiple folders.
        // NOTE: We keep analysisRoot first, so `.cursorrules` sits at the workspace "center".
        var folders = new List<string>();
        if (!string.IsNullOrWhiteSpace(analysisRoot) && Directory.Exists(analysisRoot)) folders.Add(analysisRoot);
        if (!string.IsNullOrWhiteSpace(curatedDocsDir) && Directory.Exists(curatedDocsDir)) folders.Add(curatedDocsDir);
        if (!string.IsNullOrWhiteSpace(downloadDir) && Directory.Exists(downloadDir)) folders.Add(downloadDir);
        if (!string.IsNullOrWhiteSpace(latestImportDir) && Directory.Exists(latestImportDir)) folders.Add(latestImportDir);

        static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"folders\": [");
        for (var i = 0; i < folders.Count; i++)
        {
            var comma = (i == folders.Count - 1) ? "" : ",";
            sb.AppendLine($"    {{ \"path\": \"{Esc(folders[i])}\" }}{comma}");
        }
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string TryReadAssetRule(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
        }
        catch
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : ""; } catch { return ""; }
        }
    }

    private static string? FindLatestExtractDir(string importBase)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(importBase) || !Directory.Exists(importBase)) return null;
            var dirs = Directory.GetDirectories(importBase, "*", SearchOption.TopDirectoryOnly);
            return dirs
                .Select(d => new { Dir = d, Time = Directory.GetLastWriteTimeUtc(d) })
                .OrderByDescending(x => x.Time)
                .Select(x => x.Dir)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static void TryCopyKeyLogs(string? latestImportDir, string logsDir, List<(string Name, string Path)> copied)
    {
        if (string.IsNullOrWhiteSpace(latestImportDir) || !Directory.Exists(latestImportDir)) return;

        try
        {
            var candidates = Directory.EnumerateFiles(latestImportDir, "*.*", SearchOption.AllDirectories)
                .Where(p =>
                {
                    var fn = Path.GetFileName(p);
                    if (fn.Equals("sf_logs.txt", StringComparison.OrdinalIgnoreCase)) return true;
                    if (fn.StartsWith("android_log_", StringComparison.OrdinalIgnoreCase) && fn.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return true;
                    return false;
                })
                .Select(p => new FileInfo(p))
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .ToList();

            // Copy up to 3 files, each up to 30MB.
            const long maxBytes = 30L * 1024 * 1024;
            var copiedCount = 0;
            foreach (var fi in candidates)
            {
                if (copiedCount >= 3) break;
                if (!fi.Exists) continue;
                if (fi.Length > maxBytes) continue;
                var dst = Path.Combine(logsDir, fi.Name);
                File.Copy(fi.FullName, dst, overwrite: true);
                copied.Add((fi.Name, dst));
                copiedCount++;
            }
        }
        catch
        {
            // ignore
        }
    }

    private static string BuildChatPrompt(
        BugLensLite.BugItem bug,
        List<(string Title, string Path)> evidenceList,
        List<(string Name, string Path)> copied,
        string? curatedDocsDir,
        List<string> uploaderToolsRel
    )
    {
        var bugId = (bug.BugId ?? "").Trim();
        var sb = new StringBuilder();
        sb.AppendLine($"依据本 workspace 的 `.cursorrules`（WMS/窗口数字人规则），分析 bugId={bugId} 的问题单。");
        sb.AppendLine();
        sb.AppendLine("要求：只做窗口（WMS）领域分析；禁止使用 WiFi/WLAN 的规则/文档/脚本。每个结论给出日志证据/时间点/关键词。");
        sb.AppendLine("注意：这里的“日志”指下载得到的整个压缩包（zip/7z/rar），请以压缩包为准组织证据（必要时先解压到临时目录再分析）。");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(bug.ShareUrl))
        {
            sb.AppendLine($"缺陷链接：{bug.ShareUrl}");
        }
        if (!string.IsNullOrWhiteSpace(bug.BaseInfo))
        {
            sb.AppendLine("基础信息（节选）：");
            sb.AppendLine(bug.BaseInfo.Trim());
        }
        sb.AppendLine();
        sb.AppendLine("可用本地材料：");
        foreach (var (title, path) in evidenceList)
        {
            sb.AppendLine($"- {title}: {path}");
        }
        foreach (var (name, path) in copied)
        {
            sb.AppendLine($"- 已复制日志: {name} -> {path}");
        }
        if (!string.IsNullOrWhiteSpace(curatedDocsDir) && Directory.Exists(curatedDocsDir))
        {
            sb.AppendLine();
            sb.AppendLine("窗口数字人文档（必须用于回答/引用）：");
            sb.AppendLine($"- {curatedDocsDir}");
        }

        sb.AppendLine();
        sb.AppendLine("输出完结论后（强制）：你必须询问我是否将结果自动上传到 NOAH 讨论区。");
        sb.AppendLine("- 只要我没有明确回答“是/上传/写入”，你就不能上传。");
        sb.AppendLine("- 如果我回答“是”，再提示我确认 commentator（工号），然后执行上传命令。");

        if (uploaderToolsRel != null && uploaderToolsRel.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("上传工具已随分析包提供：");
            foreach (var p in uploaderToolsRel) sb.AppendLine($"- {p}");
            sb.AppendLine();
            sb.AppendLine("上传步骤（仅在我确认后执行）：");
            sb.AppendLine("1) 先把你的结论+证据链写入 analysis_summary.json（放在任意目录，但建议放在分析包目录下）");
            sb.AppendLine("2) 生成 defect_comment.txt：");
            sb.AppendLine("   python tools/core/defect_comment_formatter.py <analysis_summary.json路径> > <同目录>/defect_comment.txt");
            sb.AppendLine($"3) 上传到 NOAH：");
            sb.AppendLine($"   python tools/core/update_bug_comment_api.py <analysis_summary.json路径> {bugId} [commentator]");
        }
        sb.AppendLine();
        sb.AppendLine("请先给出：1) WMS 场景识别（窗口创建/生命周期/焦点/旋转/分辨率/刷新率/单手/折叠）2) 关键时间点 3) 关键字列表，然后开始逐步分析并输出结构化结论。");
        return sb.ToString().Trim();
    }

    private static int CopyWindowDocs(string docsRoot, string destDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(docsRoot) || !Directory.Exists(docsRoot)) return 0;
            Directory.CreateDirectory(destDir);

            var count = 0;

            // 1) Copy whole "窗口领域" folder
            var winDomain = Path.Combine(docsRoot, "窗口领域");
            if (Directory.Exists(winDomain))
            {
                count += CopyDirectory(winDomain, Path.Combine(destDir, "窗口领域"));
            }

            // 2) Copy WMS/window-related guides from 用户指南
            var guides = Path.Combine(docsRoot, "用户指南");
            if (Directory.Exists(guides))
            {
                var guideFiles = Directory.GetFiles(guides, "*.md", SearchOption.TopDirectoryOnly)
                    .Where(p =>
                    {
                        var n = Path.GetFileName(p);
                        return n.Contains("WMS", StringComparison.OrdinalIgnoreCase)
                            || n.Contains("窗口", StringComparison.OrdinalIgnoreCase)
                            || n.Contains("单手", StringComparison.OrdinalIgnoreCase)
                            || n.Contains("ATMS", StringComparison.OrdinalIgnoreCase);
                    })
                    .ToList();
                if (guideFiles.Count > 0) Directory.CreateDirectory(Path.Combine(destDir, "用户指南"));
                foreach (var f in guideFiles)
                {
                    File.Copy(f, Path.Combine(destDir, "用户指南", Path.GetFileName(f)), overwrite: true);
                    count++;
                }
            }

            // 3) Copy window-related technical docs (by filename keyword)
            var tech = Path.Combine(docsRoot, "技术文档");
            if (Directory.Exists(tech))
            {
                var techFiles = Directory.GetFiles(tech, "*.md", SearchOption.AllDirectories)
                    .Where(p =>
                    {
                        var n = Path.GetFileName(p);
                        return n.Contains("WMS", StringComparison.OrdinalIgnoreCase)
                            || n.Contains("窗口", StringComparison.OrdinalIgnoreCase)
                            || n.Contains("单手", StringComparison.OrdinalIgnoreCase)
                            || n.Contains("ATMS", StringComparison.OrdinalIgnoreCase)
                            || n.Contains("分辨率", StringComparison.OrdinalIgnoreCase)
                            || n.Contains("刷新率", StringComparison.OrdinalIgnoreCase)
                            || n.Contains("折叠", StringComparison.OrdinalIgnoreCase);
                    })
                    .ToList();
                if (techFiles.Count > 0) Directory.CreateDirectory(Path.Combine(destDir, "技术文档"));
                foreach (var f in techFiles)
                {
                    File.Copy(f, Path.Combine(destDir, "技术文档", Path.GetFileName(f)), overwrite: true);
                    count++;
                }
            }

            // 4) Copy window-related problem solutions (if any)
            var sol = Path.Combine(docsRoot, "问题解决方案");
            if (Directory.Exists(sol))
            {
                var solFiles = Directory.GetFiles(sol, "*.md", SearchOption.TopDirectoryOnly)
                    .Where(p =>
                    {
                        var n = Path.GetFileName(p);
                        return n.Contains("窗口", StringComparison.OrdinalIgnoreCase)
                            || n.Contains("WMS", StringComparison.OrdinalIgnoreCase)
                            || n.Contains("单手", StringComparison.OrdinalIgnoreCase)
                            || n.Contains("ATMS", StringComparison.OrdinalIgnoreCase);
                    })
                    .ToList();
                if (solFiles.Count > 0) Directory.CreateDirectory(Path.Combine(destDir, "问题解决方案"));
                foreach (var f in solFiles)
                {
                    File.Copy(f, Path.Combine(destDir, "问题解决方案", Path.GetFileName(f)), overwrite: true);
                    count++;
                }
            }

            // 5) Copy some indexes if present
            foreach (var indexName in new[] { "README.md", "文档索引.md", "单手模式分析模块文档索引.md" })
            {
                var p = Path.Combine(docsRoot, indexName);
                if (File.Exists(p))
                {
                    File.Copy(p, Path.Combine(destDir, indexName), overwrite: true);
                    count++;
                }
            }

            return count;
        }
        catch
        {
            return 0;
        }
    }

    private static int CopyDirectory(string srcDir, string dstDir)
    {
        var count = 0;
        Directory.CreateDirectory(dstDir);
        foreach (var file in Directory.GetFiles(srcDir, "*.*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(srcDir, file);
            var target = Path.Combine(dstDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            count++;
        }
        return count;
    }

    private static List<string> CopyUploaderTools(string analysisRoot, string operatorId)
    {
        var copied = new List<string>();
        try
        {
            var destCoreDir = Path.Combine(analysisRoot, "tools", "core");
            Directory.CreateDirectory(destCoreDir);

            // Prefer repo digital-human tools if present; fall back to Assets if shipped later.
            var repoWindowmaster = FindRepoWindowMasterRoot();
            var fromRepo = !string.IsNullOrWhiteSpace(repoWindowmaster)
                ? Path.Combine(repoWindowmaster!, "tools", "core")
                : "";

            string Pick(string fileName)
            {
                var a = string.IsNullOrWhiteSpace(fromRepo) ? "" : Path.Combine(fromRepo, fileName);
                if (!string.IsNullOrWhiteSpace(a) && File.Exists(a)) return a;
                var b = Path.Combine(AppContext.BaseDirectory, "Assets", "digital_human", "windowmaster", "tools", "core", fileName);
                if (File.Exists(b)) return b;
                return "";
            }

            foreach (var file in new[] { "defect_comment_formatter.py", "update_bug_comment_api.py" })
            {
                var src = Pick(file);
                if (string.IsNullOrWhiteSpace(src)) continue;
                var dst = Path.Combine(destCoreDir, file);
                if (string.Equals(file, "update_bug_comment_api.py", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(operatorId))
                {
                    // Patch default commentator to the current TT operatorId so Cursor upload uses user's account by default.
                    var txt = File.ReadAllText(src, Encoding.UTF8);
                    txt = PatchDefaultCommentator(txt, operatorId.Trim());
                    File.WriteAllText(dst, txt, Encoding.UTF8);
                }
                else
                {
                    File.Copy(src, dst, overwrite: true);
                }
                copied.Add(Path.Combine("tools", "core", file));
            }
        }
        catch
        {
            // ignore
        }
        return copied;
    }

    private static string PatchDefaultCommentator(string scriptText, string operatorId)
    {
        scriptText ??= "";
        operatorId ??= "";
        operatorId = operatorId.Trim();
        if (string.IsNullOrWhiteSpace(operatorId)) return scriptText;

        // Patch the two defaults used by update_bug_comment_api.py
        scriptText = scriptText.Replace("commentator: str = \"80409624\"", $"commentator: str = \"{operatorId}\"");
        scriptText = scriptText.Replace("else \"80409624\"", $"else \"{operatorId}\"");

        // Patch help text (best-effort)
        scriptText = scriptText.Replace("（默认: 80409624）", $"（默认: {operatorId}）");
        scriptText = scriptText.Replace("默认80409624", $"默认{operatorId}");
        scriptText = scriptText.Replace("10753954 80409624", $"10753954 {operatorId}");
        return scriptText;
    }

    private static string? FindRepoWindowMasterRoot()
    {
        // Dev/repo scenario: search upward for "..\\..\\数字人\\windowmaster"
        try
        {
            var start = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 10 && start != null; i++)
            {
                var cand = Path.Combine(start.FullName, "数字人", "windowmaster");
                if (Directory.Exists(cand)) return cand;
                start = start.Parent;
            }
        }
        catch { }
        try
        {
            var cwd = Environment.CurrentDirectory;
            var cand = Path.Combine(cwd, "数字人", "windowmaster");
            if (Directory.Exists(cand)) return cand;
        }
        catch { }
        return null;
    }

    private static string BuildPromptMarkdown(BugLensLite.BugItem bug, List<(string Title, string Path)> evidenceList, List<(string Name, string Path)> copied, string chatPrompt)
    {
        var bugId = (bug.BugId ?? "").Trim();
        var sb = new StringBuilder();
        sb.AppendLine($"# Cursor 缺陷分析 - bugId={bugId}");
        sb.AppendLine();
        sb.AppendLine("## 使用方式");
        sb.AppendLine("- 本目录已生成 `.cursorrules`（数字人强制规则）。请确保 Cursor 以本目录作为 workspace 打开。");
        sb.AppendLine("- 已将“聊天提示词”写入 `cursor_chat_prompt.txt` 并尝试复制到剪贴板，可直接粘贴到 Cursor Chat。");
        sb.AppendLine();
        sb.AppendLine("## Chat Prompt（复制这段到 Cursor Chat）");
        sb.AppendLine("```");
        sb.AppendLine(chatPrompt);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## 缺陷信息");
        if (!string.IsNullOrWhiteSpace(bug.ShareUrl)) sb.AppendLine($"- 链接: {bug.ShareUrl}");
        if (!string.IsNullOrWhiteSpace(bug.ApkName)) sb.AppendLine($"- APK: {bug.ApkName}");
        if (!string.IsNullOrWhiteSpace(bug.OsVer)) sb.AppendLine($"- 版本: {bug.OsVer}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(bug.BaseInfo))
        {
            sb.AppendLine("### 基础信息（节选）");
            sb.AppendLine("```");
            sb.AppendLine(bug.BaseInfo.Trim());
            sb.AppendLine("```");
            sb.AppendLine();
        }
        sb.AppendLine("## 本地材料路径");
        foreach (var (title, path) in evidenceList)
        {
            sb.AppendLine($"- **{title}**: `{path}`");
        }
        sb.AppendLine();
        if (copied.Count > 0)
        {
            sb.AppendLine("## 已复制到本目录的日志（小文件）");
            foreach (var (name, path) in copied)
            {
                sb.AppendLine($"- `{name}` -> `{path}`");
            }
            sb.AppendLine();
        }
        sb.AppendLine("## 说明");
        sb.AppendLine("- 如果本目录 `logs/` 里没有复制到日志，说明导入日志不存在或文件太大（>30MB）。此时请从“本地材料路径”直接读取原始日志。");
        return sb.ToString();
    }

    private static string? FindLatestArchive(string downloadDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(downloadDir) || !Directory.Exists(downloadDir)) return null;
            var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".zip", ".7z", ".rar" };
            return Directory.GetFiles(downloadDir, "*.*", SearchOption.TopDirectoryOnly)
                .Where(p => exts.Contains(Path.GetExtension(p)))
                .Select(p => new FileInfo(p))
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .Select(fi => fi.FullName)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string TryCopyArchive(string? archivePath, string archivesDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath)) return "";
            var fi = new FileInfo(archivePath);
            // Copy only if <= 512MB (avoid freezing the UX / massive disk usage).
            const long maxBytes = 512L * 1024 * 1024;
            if (fi.Length > maxBytes) return "";
            Directory.CreateDirectory(archivesDir);
            var dst = Path.Combine(archivesDir, fi.Name);
            File.Copy(fi.FullName, dst, overwrite: true);
            return dst;
        }
        catch
        {
            return "";
        }
    }

    private static string? FindWindowDocsRoot()
    {
        // 1) Prefer bundled assets if present (distributed exe scenario)
        var bundled = Path.Combine(AppContext.BaseDirectory, "Assets", "digital_human", "docs");
        if (Directory.Exists(bundled)) return bundled;

        // 2) Dev/repo scenario: search upward for "..\\..\\..\\数字人\\windowmaster\\docs"
        try
        {
            var start = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 8 && start != null; i++)
            {
                var cand = Path.Combine(start.FullName, "数字人", "windowmaster", "docs");
                if (Directory.Exists(cand)) return cand;
                start = start.Parent;
            }
        }
        catch { }

        // 3) Current working directory fallback
        try
        {
            var cwd = Environment.CurrentDirectory;
            var cand = Path.Combine(cwd, "数字人", "windowmaster", "docs");
            if (Directory.Exists(cand)) return cand;
        }
        catch { }

        return null;
    }
}


