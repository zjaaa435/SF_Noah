using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace BugLensLite.Services;

public sealed class AppUpdateService
{
    // Optional default GitHub update repo (lets end-users update without setting env vars).
    // If you don't want to hard-code, leave it empty and rely on env var / future settings UI.
    private const string DefaultRepo = "zjaaa435/SF_Noah";

    // Optional internal update feed URL (JSON) for intranet distribution.
    // If set, the app will prefer this over GitHub releases.
    //
    // Example:
    // setx SF_NOAH_UPDATE_FEED_URL "https://intranet.example.com/sf_noah/latest.json"
    private static string? GetUpdateFeedUrlFromEnv()
        => (Environment.GetEnvironmentVariable("SF_NOAH_UPDATE_FEED_URL") ?? "").Trim();

    // TT login token (SIAMTGT) - used to access intranet update feed / zip if needed
    private string _ttToken = "";
    private readonly System.Collections.Generic.HashSet<string> _ttAuthAllowedHosts =
        new(System.StringComparer.OrdinalIgnoreCase);

    public void SetToken(string token)
    {
        _ttToken = token ?? "";
    }

    // Configure repo via env var to avoid hard-coding:
    // setx SF_NOAH_GITHUB_REPO "owner/repo"
    private static string? GetRepoFromEnv()
        => (Environment.GetEnvironmentVariable("SF_NOAH_GITHUB_REPO") ?? "").Trim();

    private static string? GetRepo()
    {
        var repo = GetRepoFromEnv();
        if (!string.IsNullOrWhiteSpace(repo)) return repo;
        return string.IsNullOrWhiteSpace(DefaultRepo) ? null : DefaultRepo.Trim();
    }

    private static bool IsHttpUrl(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        return Uri.TryCreate(s, UriKind.Absolute, out var u) &&
               (u.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                u.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase));
    }

    private static string? TryGetString(JsonElement obj, params string[] keys)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        foreach (var k in keys)
        {
            if (!obj.TryGetProperty(k, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String) return v.GetString();
            if (v.ValueKind == JsonValueKind.Number) return v.ToString();
        }
        return null;
    }

    private void AddTtAuthHeaders(HttpRequestMessage req)
    {
        if (string.IsNullOrWhiteSpace(_ttToken)) return;
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _ttToken);
        req.Headers.TryAddWithoutValidation("Cookie", $"SIAMTGT={_ttToken}");
    }

    private bool ShouldSendTtAuth(Uri uri)
    {
        if (string.IsNullOrWhiteSpace(_ttToken)) return false;
        if (_ttAuthAllowedHosts.Count == 0) return false;
        return _ttAuthAllowedHosts.Contains(uri.Host);
    }

    public sealed record LatestRelease(
        string Repo,
        string Tag,
        string Name,
        string HtmlUrl,
        string AssetName,
        string AssetUrl
    );

    public async Task<LatestRelease?> GetLatestReleaseAsync()
    {
        // 1) Prefer internal update feed if configured
        var feedUrl = GetUpdateFeedUrlFromEnv();
        if (IsHttpUrl(feedUrl))
        {
            if (string.IsNullOrWhiteSpace(_ttToken))
                throw new Exception("更新源为内网（需要 TT 登录鉴权）。请先点击 TT登录 后再检查更新。");

            _ttAuthAllowedHosts.Clear();
            var feedUri = new Uri(feedUrl!);
            _ttAuthAllowedHosts.Add(feedUri.Host);

            using var feedHttp = new HttpClient();
            feedHttp.DefaultRequestHeaders.UserAgent.ParseAdd("SF_Noah/1.0");

            using var req = new HttpRequestMessage(HttpMethod.Get, feedUrl);
            if (ShouldSendTtAuth(feedUri)) AddTtAuthHeaders(req);
            using var resp = await feedHttp.SendAsync(req);
            var feedJsonText = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"内网更新源请求失败：HTTP {(int)resp.StatusCode}\n{feedJsonText}");

            using var feedDoc = JsonDocument.Parse(feedJsonText);
            var feedRoot = feedDoc.RootElement;

            var version = (TryGetString(feedRoot, "version", "Version", "tag", "tagName", "tag_name") ?? "").Trim();
            var zipUrl = (TryGetString(feedRoot, "zipUrl", "zip_url", "assetUrl", "asset_url", "url") ?? "").Trim();
            var notesUrl = (TryGetString(feedRoot, "notesUrl", "notes_url", "htmlUrl", "html_url") ?? "").Trim();
            var displayName = (TryGetString(feedRoot, "name", "Name", "title", "Title") ?? "").Trim();

            if (version.StartsWith("v", StringComparison.OrdinalIgnoreCase)) version = version[1..];
            if (string.IsNullOrWhiteSpace(version) || !IsHttpUrl(zipUrl))
                throw new Exception("内网更新源返回内容不完整：需要 version + zipUrl");

            var zipUri = new Uri(zipUrl);
            _ttAuthAllowedHosts.Add(zipUri.Host);

            var assetName = Path.GetFileName(zipUri.LocalPath);
            if (string.IsNullOrWhiteSpace(assetName)) assetName = "SF_Noah-win-x64.zip";

            return new LatestRelease(
                Repo: "internal",
                Tag: "v" + version,
                Name: string.IsNullOrWhiteSpace(displayName) ? ("v" + version) : displayName,
                HtmlUrl: string.IsNullOrWhiteSpace(notesUrl) ? feedUrl! : notesUrl,
                AssetName: assetName,
                AssetUrl: zipUrl
            );
        }

        // 2) Fallback to GitHub releases
        var repo = GetRepo();
        if (string.IsNullOrWhiteSpace(repo) || !repo.Contains('/')) return null;

        using var ghHttp = new HttpClient();
        ghHttp.DefaultRequestHeaders.UserAgent.ParseAdd("SF_Noah/1.0");
        var url = $"https://api.github.com/repos/{repo}/releases/latest";
        using var ghResp = await ghHttp.GetAsync(url);
        var ghText = await ghResp.Content.ReadAsStringAsync();
        if (!ghResp.IsSuccessStatusCode)
        {
            // GitHub API returns 404 when the repo has no releases yet (normal behavior).
            // For end-users, treat it as "no update available" instead of showing maintainer instructions.
            if ((int)ghResp.StatusCode == 404)
                return null;
            if ((int)ghResp.StatusCode == 403)
                throw new Exception($"检查更新失败：更新源拒绝访问（HTTP 403）");
            if ((int)ghResp.StatusCode == 401)
                throw new Exception($"检查更新失败：更新源未授权（HTTP 401）");
            throw new Exception($"检查更新失败：更新源不可用（HTTP {(int)ghResp.StatusCode}）");
        }

        using var doc = JsonDocument.Parse(ghText);

        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
        var name = root.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
        var html = root.TryGetProperty("html_url", out var h) ? (h.GetString() ?? "") : "";

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        var best = assets.EnumerateArray()
            .Select(a => new
            {
                Name = a.TryGetProperty("name", out var an) ? (an.GetString() ?? "") : "",
                Url = a.TryGetProperty("browser_download_url", out var au) ? (au.GetString() ?? "") : "",
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.Url))
            .OrderByDescending(x => x.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

        if (best == null) return null;
        return new LatestRelease(repo, tag, name, html, best.Name, best.Url);
    }

    public static Version GetCurrentVersion()
    {
        try
        {
            return typeof(AppUpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
        }
        catch
        {
            return new Version(0, 0, 0, 0);
        }
    }

    public static Version? ParseTagVersion(string tag)
    {
        tag = (tag ?? "").Trim();
        if (tag.StartsWith("v", StringComparison.OrdinalIgnoreCase)) tag = tag[1..];
        return Version.TryParse(tag, out var v) ? v : null;
    }

    public async Task DownloadAndApplyUpdateAsync(string assetUrl, string assetName)
    {
        if (string.IsNullOrWhiteSpace(assetUrl)) throw new Exception("更新资源链接为空");

        var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
        if (string.IsNullOrWhiteSpace(exePath)) throw new Exception("无法定位当前 exe 路径");
        var appDir = Path.GetDirectoryName(exePath)!;

        var tmp = Path.Combine(Path.GetTempPath(), "SF_Noah_Update", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(tmp);

        var downloadPath = Path.Combine(tmp, string.IsNullOrWhiteSpace(assetName) ? "update.zip" : assetName);
        var assetUri = new Uri(assetUrl);
        using (var http = new HttpClient())
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SF_Noah/1.0");
            using var req = new HttpRequestMessage(HttpMethod.Get, assetUrl);
            if (ShouldSendTtAuth(assetUri)) AddTtAuthHeaders(req);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await src.CopyToAsync(dst);
        }

        // Extract (expect a zip containing the published app files)
        var extracted = Path.Combine(tmp, "payload");
        Directory.CreateDirectory(extracted);
        if (downloadPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(downloadPath, extracted, overwriteFiles: true);
        }
        else if (downloadPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            // Direct exe update
            File.Copy(downloadPath, Path.Combine(extracted, Path.GetFileName(exePath)), overwrite: true);
        }
        else
        {
            throw new Exception("未知更新包格式（需要 .zip 或 .exe）");
        }

        var newExe = Directory.EnumerateFiles(extracted, "*.exe", SearchOption.AllDirectories)
            .FirstOrDefault(f => string.Equals(Path.GetFileName(f), Path.GetFileName(exePath), StringComparison.OrdinalIgnoreCase))
            ?? Directory.EnumerateFiles(extracted, "*.exe", SearchOption.AllDirectories).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(newExe)) throw new Exception("更新包内未找到 exe");

        // Build updater bat: wait for current exe to exit, then copy files and restart
        var bat = Path.Combine(tmp, "update.bat");
        var newRoot = Path.GetDirectoryName(newExe)!;
        var exeName = Path.GetFileName(exePath);
        var batText = $"""
@echo off
setlocal
set "APPDIR={appDir}"
set "NEWROOT={newRoot}"
set "EXE={exeName}"

REM wait for app exit
:wait
tasklist /FI "IMAGENAME eq %EXE%" 2>NUL | find /I "%EXE%" >NUL
if "%ERRORLEVEL%"=="0" (
  timeout /t 1 /nobreak >NUL
  goto wait
)

REM copy payload
xcopy "%NEWROOT%\*" "%APPDIR%\" /E /I /Y >NUL

REM restart
start "" "%APPDIR%\%EXE%"
endlocal
""";
        await File.WriteAllTextAsync(bat, batText, System.Text.Encoding.UTF8);

        Process.Start(new ProcessStartInfo
        {
            FileName = bat,
            UseShellExecute = true,
            WorkingDirectory = tmp,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }
}




