using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using System.Text.Json;

namespace BugLensLite.Services;

public sealed class NoahPoseidonService
{
    private readonly HttpClient _http = new();
    private string _token = "";
    private string _operator = "";

    // Direct Noah comment endpoint (captured from browser network):
    // POST https://g-agile-dms.myoas.com/api/dms/api/landray/process/comment
    // payload: { collection:"OPPO", workItemId:"10638213", text:"<p>1</p>" }
    private const string CommentPostUrl = "https://g-agile-dms.myoas.com/api/dms/api/landray/process/comment";

    // MCP endpoint for adding comments to Noah bugs (same as digital-human tools/core/update_bug_comment_api.py)
    // Allow override via env vars in case the platform changes.
    private static string GetMcpUrl()
        => (Environment.GetEnvironmentVariable("SF_NOAH_MCP_URL") ?? "https://mcpmarket.myoas.com/G-Agile-DMS-USER-MCP/mcp").Trim();
    private static string GetMcpApiKey()
        => (Environment.GetEnvironmentVariable("SF_NOAH_MCP_APIKEY") ?? "pxyfkwwPanBkgpfgCn").Trim();
    private static string GetMcpEnvId()
        => (Environment.GetEnvironmentVariable("SF_NOAH_MCP_ENVID") ?? "PROD").Trim();

    public void SetToken(string token)
    {
        _token = token ?? "";
    }

    public void SetOperator(string operatorId)
    {
        _operator = operatorId ?? "";
    }

    public async Task AddCommentAsync(string bugId, string commentContent)
    {
        bugId = (bugId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(bugId)) throw new Exception("BugId 为空");
        if (!int.TryParse(bugId, out var wid)) throw new Exception("BugId 必须是纯数字");
        if (string.IsNullOrWhiteSpace(commentContent)) throw new Exception("评论内容为空");
        if (string.IsNullOrWhiteSpace(_token)) throw new Exception("Missing token (SIAMTGT). 请先 TT登录。");

        // Prefer direct endpoint (works without MCP service)
        try
        {
            var payloadDirect = new
            {
                collection = "OPPO",
                workItemId = bugId,
                text = commentContent
            };
            var jsonDirect = JsonSerializer.Serialize(payloadDirect);
            using var reqDirect = new HttpRequestMessage(HttpMethod.Post, CommentPostUrl);
            reqDirect.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            reqDirect.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            reqDirect.Headers.TryAddWithoutValidation("Cookie", $"SIAMTGT={_token}");
            reqDirect.Content = new StringContent(jsonDirect, Encoding.UTF8, "application/json");
            using var respDirect = await _http.SendAsync(reqDirect);
            var directText = await respDirect.Content.ReadAsStringAsync();
            if (respDirect.IsSuccessStatusCode) return;
            // If direct fails, fall through to MCP path for compatibility.
        }
        catch
        {
            // ignore and fallback to MCP
        }

        var commentator = (_operator ?? "").Trim();
        if (string.IsNullOrWhiteSpace(commentator))
            throw new Exception("发表评论失败（直连接口失败且未识别 operatorId）。请先 TT登录，让工具自动识别工号后再试。");

        var mcpUrl = GetMcpUrl();
        var apiKey = GetMcpApiKey();
        var envId = GetMcpEnvId();
        if (string.IsNullOrWhiteSpace(mcpUrl)) throw new Exception("MCP URL 未配置");
        if (string.IsNullOrWhiteSpace(apiKey)) throw new Exception("MCP apikey 未配置");

        // MCP JSON-RPC payload: tools/call -> DMS-API0011
        var payload = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = "DMS-API0011",
                arguments = new
                {
                    workItemId = wid,
                    collection = "OPPO",
                    commentator,
                    comment = commentContent
                }
            }
        };

        var json = JsonSerializer.Serialize(payload);
        static IEnumerable<string> CandidateMcpUrls(string url)
        {
            url ??= "";
            url = url.Trim();
            if (string.IsNullOrWhiteSpace(url)) yield break;

            // 1) original, with/without trailing slash
            yield return url;
            if (url.EndsWith("/")) yield return url.TrimEnd('/');
            else yield return url + "/";

            // 2) swap known service names (we have seen both in the knowledge base)
            var swapped = url;
            if (swapped.Contains("/G-Agile-DMS-USER-MCP/", StringComparison.OrdinalIgnoreCase))
                swapped = swapped.Replace("/G-Agile-DMS-USER-MCP/", "/G-Agile-DMS-MCP/", StringComparison.OrdinalIgnoreCase);
            else if (swapped.Contains("/G-Agile-DMS-MCP/", StringComparison.OrdinalIgnoreCase))
                swapped = swapped.Replace("/G-Agile-DMS-MCP/", "/G-Agile-DMS-USER-MCP/", StringComparison.OrdinalIgnoreCase);
            if (!string.Equals(swapped, url, StringComparison.OrdinalIgnoreCase))
            {
                yield return swapped;
                if (swapped.EndsWith("/")) yield return swapped.TrimEnd('/');
                else yield return swapped + "/";
            }
        }

        async Task<(int StatusCode, string Body)> PostOnceAsync(string url)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.TryAddWithoutValidation("x-mcp-apikey", apiKey);
            req.Headers.TryAddWithoutValidation("envid", envId);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            return ((int)resp.StatusCode, body ?? "");
        }

        int status = 0;
        string text = "";
        string usedUrl = mcpUrl;
        var tried = new List<(string Url, int Status, string Body)>();

        foreach (var cand in CandidateMcpUrls(mcpUrl).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var (s, b) = await PostOnceAsync(cand);
            tried.Add((cand, s, b));
            if (s >= 200 && s < 300)
            {
                status = s;
                text = b;
                usedUrl = cand;
                break;
            }
            // If we get a non-404 structured error, still keep trying other candidates;
            // but we'll report all attempts if nothing works.
            status = s;
            text = b;
            usedUrl = cand;
        }
        if (status < 200 || status >= 300)
        {
            var diag = string.Join("\n\n", tried.Select(t => $"URL: {t.Url}\nHTTP {t.Status}\n{t.Body}"));
            throw new Exception(
                "发表评论失败。\n\n" +
                "可能原因：MCP 地址变更/不可达，或仓库环境无法访问。\n\n" +
                $"当前 MCP URL：{mcpUrl}\n" +
                $"你可以用环境变量覆盖：SF_NOAH_MCP_URL\n\n" +
                diag
            );
        }

        // Parse MCP response: { result: { content: [ { type:'text', text:'{...}' } ] } }
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var err))
            throw new Exception($"发表评论失败：{err}");

        if (!root.TryGetProperty("result", out var result))
            throw new Exception("发表评论失败：响应缺少 result");
        if (!result.TryGetProperty("content", out var contentArr) || contentArr.ValueKind != JsonValueKind.Array)
            throw new Exception("发表评论失败：响应缺少 content");

        string? innerText = null;
        foreach (var item in contentArr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var type = item.TryGetProperty("type", out var t) ? (t.GetString() ?? "") : "";
            if (!string.Equals(type, "text", StringComparison.OrdinalIgnoreCase)) continue;
            innerText = item.TryGetProperty("text", out var tt) ? (tt.GetString() ?? "") : "";
            if (!string.IsNullOrWhiteSpace(innerText)) break;
        }

        if (string.IsNullOrWhiteSpace(innerText))
            throw new Exception($"发表评论失败：响应格式异常\n{text}");

        // innerText is usually a JSON string like {"code":0,"msg":"success",...}
        try
        {
            using var innerDoc = JsonDocument.Parse(innerText);
            var ir = innerDoc.RootElement;
            var code = ir.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
            var msg = ir.TryGetProperty("msg", out var m) ? (m.GetString() ?? "") : "";
            if (code == 0 || code == 200 || msg.Contains("success", StringComparison.OrdinalIgnoreCase))
                return;
            throw new Exception($"发表评论失败：code={code}, msg={msg}");
        }
        catch (JsonException)
        {
            // If not json, treat as success only if it contains success keyword
            if (innerText.Contains("success", StringComparison.OrdinalIgnoreCase)) return;
            throw new Exception($"发表评论失败：{innerText}");
        }
    }

    public async Task<BugLensLite.BugItem> FetchBugAsync(string bugId)
    {
        var fields = await GetBugFieldsAsync(bugId);

        string apk = GetStr(fields, "Oppo.Apk.Name");
        string apkVer = GetFirst(fields,
            "Oppo.Apk.Version",
            "Oppo.Apk.VersionName",
            "Oppo.Apk.Version.Num",
            "Oppo.Apk.VersionCode",
            "Oppo.Apk.Ver");
        string os = GetStr(fields, "Oppo.OsVersion.Num");
        string model = GetFirst(fields,
            "Oppo.Model",
            "Oppo.Device.Model",
            "Oppo.Phone.Model",
            "Oppo.Hardware.Model",
            "Oppo.Build.Model",
            "Oppo.Product.Model",
            "Oppo.Device");
        if (string.IsNullOrWhiteSpace(model))
            model = FindValueByContains(fields, "model");

        string severity = GetFirst(fields,
            "Oppo.Defect.Level",
            "Oppo.Defect.Severity",
            "Oppo.Defect.Grade",
            "Oppo.Defect.Priority");
        if (string.IsNullOrWhiteSpace(severity))
            severity = FindValueByContains(fields, "defect", "level");

        string repro = GetFirst(fields,
            "Oppo.Defect.ReproRate",
            "Oppo.Defect.ReproduceRate",
            "Oppo.Defect.Probability",
            "Oppo.Defect.ReproProbability",
            "Oppo.Defect.Repro");
        if (string.IsNullOrWhiteSpace(repro))
            repro = FindValueByContains(fields, "repro");

        string reference = GetFirst(fields,
            "Oppo.ReferenceDevice",
            "Oppo.CompareDevice",
            "Oppo.Reference.Phone",
            "Oppo.Reference.Model");

        string desc = GetStr(fields, "Oppo.Defect.Description");
        string logInfo = GetStr(fields, "Oppo.LogInfo");

        var share = ExtractPoseidonShareUrl(logInfo, desc);

        // Extra fields for a denser "basic info" panel (aligned with Noah page form fields)
        string projectCode = GetFirst(fields, "projectCode", "Oppo.Project.Code", "Oppo.ProjectCode", "Oppo.Project");
        if (string.IsNullOrWhiteSpace(projectCode)) projectCode = FindValueByContains(fields, "project", "code");
        string initialProjectCode = GetFirst(fields, "initialProjectCode", "Oppo.InitialProjectCode", "Oppo.Project.InitialCode");
        if (string.IsNullOrWhiteSpace(initialProjectCode)) initialProjectCode = FindValueByContains(fields, "initial", "project", "code");

        // Noah "OS版本名" is typically under keys like OsVersion/osVersionName; prefer exact keys first.
        string osName = GetFirst(fields, "OsVersion", "osVersion", "Oppo.OsVersion.Name", "Oppo.OsVersionName", "osVersionName", "osName");
        if (string.IsNullOrWhiteSpace(osName)) osName = FindValueByContains(fields, "coloros");

        string projectLocalId = GetFirst(fields, "projectLocalId", "ProjectLocalId", "localProjectId");
        if (string.IsNullOrWhiteSpace(projectLocalId)) projectLocalId = FindValueByContains(fields, "local", "id");

        // Noah uses "Adder" a lot for version address (you called it out explicitly)
        string projectAddr = GetFirst(fields, "Adder", "adder", "projectAddr", "projectAdder", "projectAddress", "Oppo.Project.Address", "Oppo.ProjectAdder");
        if (string.IsNullOrWhiteSpace(projectAddr)) projectAddr = FindValueByContains(fields, "artifact");

        string projectVer = GetFirst(fields, "projectVer", "Oppo.Project.Ver", "Oppo.ProjectVersion", "Oppo.Project.Ver.Num");
        if (string.IsNullOrWhiteSpace(projectVer)) projectVer = FindValueByContains(fields, "project", "ver");

        string testStage = GetFirst(fields, "testStage", "Oppo.Test.Stage", "Oppo.TestStage", "testPhase", "phase");
        if (string.IsNullOrWhiteSpace(testStage)) testStage = FindValueByContains(fields, "test", "stage");

        string isCaseFound = GetFirst(fields, "isCaseFound", "caseFound", "isUseCaseFound", "Oppo.Case.Found");
        if (string.IsNullOrWhiteSpace(isCaseFound)) isCaseFound = FindValueByContains(fields, "case", "found");

        string isNewRequirement = GetFirst(fields, "isNewRequirement", "newRequirement", "Oppo.NewRequirement");
        if (string.IsNullOrWhiteSpace(isNewRequirement)) isNewRequirement = FindValueByContains(fields, "new", "require");

        string useCaseId = GetFirst(fields, "useCaseId", "caseId", "Oppo.Case.Id", "Oppo.UseCase.Id");
        if (string.IsNullOrWhiteSpace(useCaseId)) useCaseId = FindValueByContains(fields, "case");

        string useCaseGroup = GetFirst(fields, "useCaseGroup", "caseGroup", "Oppo.Case.Group", "Oppo.UseCase.Group");
        if (string.IsNullOrWhiteSpace(useCaseGroup)) useCaseGroup = FindValueByContains(fields, "case", "group");

        string defectSource = GetFirst(fields, "defectSource", "Oppo.Defect.Source", "Oppo.Source");
        if (string.IsNullOrWhiteSpace(defectSource)) defectSource = FindValueByContains(fields, "source");

        string featureId = GetFirst(fields, "featureId", "FeatureId", "Oppo.FeatureId", "Oppo.Feature.Id");
        if (string.IsNullOrWhiteSpace(featureId)) featureId = FindValueByContains(fields, "feature");

        string previousVersionExists = GetFirst(fields, "previousVersionExists", "Oppo.PreviousVersion.Exists");
        if (string.IsNullOrWhiteSpace(previousVersionExists)) previousVersionExists = FindValueByContains(fields, "previous", "version");

        var baseInfo = $"bugId={bugId}\napk={apk}\nos={os}\n\n描述:\n{desc}";
        var baseHtml = BuildBaseHtml(
            bugId: bugId,
            osName: osName,
            severity: severity,
            repro: repro,
            apk: apk,
            apkVer: apkVer,
            reference: reference,
            projectCode: projectCode,
            initialProjectCode: initialProjectCode,
            projectLocalId: projectLocalId,
            projectAddr: projectAddr,
            projectVer: projectVer,
            testStage: testStage,
            isCaseFound: isCaseFound,
            useCaseId: useCaseId,
            useCaseGroup: useCaseGroup,
            isNewRequirement: isNewRequirement,
            defectSource: defectSource,
            featureId: featureId,
            previousVersionExists: previousVersionExists,
            desc: desc,
            shareUrl: share.ShareUrl,
            fields: fields);
        var comments = await TryGetCommentsAsync(bugId);
        var commentHtml = await TryGetCommentsHtmlAsync(bugId);
        var attach = await TryGetAttachmentsAsync(bugId);

        string logBlock = share.ShareUrl != "" ? $"shareUrl: {share.ShareUrl}\nsuffix: {share.Suffix}\nfileId: {share.FileId}" : "未找到 poseidon share/file 链接";

        string linksBlock = "-";
        var urls = new List<string>();
        if (share.FileId != "" && share.Suffix != "")
        {
            var storageKeys = await GetPoseidonStorageKeysAsync(share.Suffix, share.FileId);
            var lines = new List<string>();
            foreach (var sk in storageKeys)
            {
                var url = await GetPoseidonPreSignedUrlAsync(share.Suffix, sk);
                if (!string.IsNullOrEmpty(url)) urls.Add(url);
                lines.Add($"{sk}\n  {url}\n");
            }
            linksBlock = lines.Count > 0 ? string.Join("\n", lines) : "(no storageKeys)";
        }

        return new BugLensLite.BugItem
        {
            BugId = bugId,
            Snippet = (desc ?? "").Replace("\r", " ").Replace("\n", " ").Trim().Length > 80
                ? (desc ?? "").Replace("\r", " ").Replace("\n", " ").Trim()[..80]
                : (desc ?? "").Replace("\r", " ").Replace("\n", " ").Trim(),
            ApkName = apk,
            OsVer = os,
            BaseInfo = baseInfo,
            BaseHtml = baseHtml,
            LogInfo = logBlock,
            DownloadLinks = linksBlock,
            DownloadUrls = urls,
            ShareUrl = share.ShareUrl,
            // Minimal placeholders (we can map real Noah fields later once you specify them)
            Attachments = attach.Items,
            AttachError = attach.Error,
            CommentInfo = comments,
            CommentHtml = commentHtml
        };
    }

    private async Task<string> TryGetCommentsHtmlAsync(string bugId)
    {
        try
        {
            return await GetCommentListHtmlAsync(bugId, "缺陷管理");
        }
        catch
        {
            return "";
        }
    }

    private async Task<AttachmentFetchResult> TryGetAttachmentsAsync(string bugId)
    {
        try
        {
            // Backend requires operator(工号) in many envs. We auto-extract it from TT login cookies.
            var op = string.IsNullOrWhiteSpace(_operator) ? "" : _operator.Trim();
            if (string.IsNullOrWhiteSpace(op))
                return new AttachmentFetchResult { Error = "附件：需要工号（TT账号）。请重新点击 TT登录，让工具自动识别工号后再拉取。" };

            return await GetAttachmentsAsync(bugId, op);
        }
        catch (Exception ex)
        {
            return new AttachmentFetchResult { Error = $"附件拉取失败: {ex.Message}" };
        }
    }

    public async Task<AttachmentFetchResult> GetAttachmentsAsync(string workItemId, string operatorId)
    {
        if (string.IsNullOrWhiteSpace(_token))
            throw new Exception("Missing token (SIAMTGT).");

        // From your captured request
        const string host = "https://g-agile-api-alb.myoas.com";
        const string collectionId = "6ca09493-5cb9-479a-bd45-0cd8598de4d3";
        const string collectionName = "OPPO";
        const string env = "prod";

        var url =
            host + "/magicBox/file/getPageFileByWorkItemIdAndCollection" +
            "?collectionId=" + HttpUtility.UrlEncode(collectionId) +
            "&workItemId=" + HttpUtility.UrlEncode(workItemId ?? "") +
            "&collectionName=" + HttpUtility.UrlEncode(collectionName) +
            "&operator=" + HttpUtility.UrlEncode(operatorId ?? "") +
            "&env=" + HttpUtility.UrlEncode(env);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        req.Headers.TryAddWithoutValidation("Cookie", $"SIAMTGT={_token}");

        using var resp = await _http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"attachments failed: HTTP {(int)resp.StatusCode}: {Trim(text)}");

        return ParseAttachments(text);
    }

    private static AttachmentFetchResult ParseAttachments(string jsonText)
    {
        if (string.IsNullOrWhiteSpace(jsonText)) return new AttachmentFetchResult { Error = "(empty)" };
        using var doc = JsonDocument.Parse(jsonText);
        var root = doc.RootElement;
        var arr = FindBestAttachmentArray(root);
        if (arr.ValueKind != JsonValueKind.Array)
        {
            return new AttachmentFetchResult { Error = "附件解析失败（接口返回结构变化）" };
        }

        var items = arr.EnumerateArray().ToList();
        if (items.Count == 0) return new AttachmentFetchResult { Items = Array.Empty<AttachmentItem>() };

        var list = new List<AttachmentItem>();
        foreach (var it in items)
        {
            var name = GetStrAny(it, "fileName", "name", "filename", "originName", "originalName", "displayName");
            var sizeStr = GetStrAny(it, "fileSize", "size", "length");
            var time = GetStrAny(it, "createTime", "createdTime", "createdAt", "gmtCreate", "uploadTime");
            var url = GetStrAny(it, "downloadUrl", "url", "fileUrl", "link", "downloadLink");

            long sizeBytes = 0;
            _ = long.TryParse(new string((sizeStr ?? "").Where(char.IsDigit).ToArray()), out sizeBytes);

            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(url)) continue;
            list.Add(new AttachmentItem
            {
                Name = name ?? "",
                SizeBytes = sizeBytes,
                Time = time ?? "",
                DownloadUrl = url ?? "",
            });
        }

        return new AttachmentFetchResult { Items = list.ToArray() };
    }

    public async Task DownloadFileAsync(string url, string destPath)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new Exception("下载链接为空");
        if (string.IsNullOrWhiteSpace(destPath)) throw new Exception("目标路径为空");
        if (string.IsNullOrWhiteSpace(_token)) throw new Exception("Missing token (SIAMTGT).");

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        req.Headers.TryAddWithoutValidation("Cookie", $"SIAMTGT={_token}");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        await using var src = await resp.Content.ReadAsStreamAsync();
        await using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await src.CopyToAsync(dst);
    }

    private static string BuildBaseHtml(
        string bugId,
        string osName,
        string severity,
        string repro,
        string apk,
        string apkVer,
        string reference,
        string projectCode,
        string initialProjectCode,
        string projectLocalId,
        string projectAddr,
        string projectVer,
        string testStage,
        string isCaseFound,
        string useCaseId,
        string useCaseGroup,
        string isNewRequirement,
        string defectSource,
        string featureId,
        string previousVersionExists,
        string desc,
        string shareUrl,
        Dictionary<string, object?> fields)
    {
        string E(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
        var d = HtmlToPlainText(desc ?? "");

        var sb = new StringBuilder();
        sb.Append("""
<!doctype html><html><head><meta charset="utf-8"/>
<style>
  body{font-family:Segoe UI,Microsoft YaHei,Arial; margin:8px; color:#111; font-size:12px;}
  /* Denser layout: smaller fonts/padding so more fields fit on one screen */
  .form{display:grid; grid-template-columns:repeat(4, minmax(200px, 1fr)); gap:8px 10px; align-items:start;}
  @media (max-width: 1100px){ .form{grid-template-columns:repeat(3, minmax(200px, 1fr));} }
  @media (max-width: 860px){ .form{grid-template-columns:repeat(2, minmax(200px, 1fr));} }
  @media (max-width: 520px){ .form{grid-template-columns:1fr;} }
  .field{display:flex; flex-direction:column; gap:4px;}
  .label{color:#555; font-size:11px;}
  .box{border:1px solid #d7d7d7; border-radius:6px; padding:6px 8px; background:#fafafa; font-weight:600; min-height:28px; line-height:1.25;}
  .box.mono{font-family:Consolas,ui-monospace,monospace;}
  .box.wrap{word-break:break-all;}
  .box a{color:#0969da; text-decoration:none;}
  .box a:hover{text-decoration:underline;}
  .card{border:1px solid #cfcfcf; border-radius:8px; padding:12px; margin-bottom:12px;}
  .desc{white-space:pre-wrap; font-weight:400; line-height:1.35;}
  a{color:#0969da; text-decoration:none;}
  a:hover{text-decoration:underline;}
  details{border:1px dashed #cfcfcf; border-radius:8px; padding:10px 12px; margin-top:10px; background:#fff;}
  summary{cursor:pointer; color:#333; font-weight:600;}
  .tools{margin-top:10px; display:flex; gap:10px; align-items:center; flex-wrap:wrap;}
  .tools input{padding:6px 8px; border:1px solid #d7d7d7; border-radius:6px; min-width:260px;}
  .tools label{color:#333; font-weight:600;}
  .tools .muted{color:#666; font-weight:400;}
  table{width:100%; border-collapse:collapse; margin-top:10px; font-size:12px;}
  td{border-bottom:1px solid #eee; padding:6px 8px; vertical-align:top;}
  td.k2{color:#555; width:360px; font-family:Consolas,ui-monospace,monospace;}
  td.v2{word-break:break-all;}
</style></head><body>
""");
        sb.Append("<div class=\"card\"><div class=\"form\">");

        static string V(string s) => string.IsNullOrWhiteSpace(s) ? "—" : s.Trim();

        void Add(string label, string value, string cls = "", bool link = false)
        {
            var v = V(value);
            sb.Append("<div class=\"field\">");
            sb.Append("<div class=\"label\">").Append(E(label)).Append("</div>");
            sb.Append("<div class=\"box ").Append(E(cls)).Append("\">");
            if (link && v != "—")
                sb.Append("<a href=\"").Append(E(v)).Append("\" target=\"_blank\">").Append(E(v)).Append("</a>");
            else
                sb.Append(E(v));
            sb.Append("</div></div>");
        }

        // Align with Noah page (best-effort extraction; missing values will show as '—')
        // Use the raw Noah field values as much as possible (these keys exist in "更多字段")
        Add("项目代号", GetFirst(fields, "projectCode", "ProjectCode", "Oppo.Project.Code", "Oppo.ProjectCode", "Oppo.Project", projectCode), "mono");
        Add("初始项目代号", GetFirst(fields, "Oppo.ProjectCode.init", initialProjectCode), "mono");
        Add("OS版本名", GetFirst(fields, "Oppo.OsVersion.Num", osName), "mono");

        // 删除：版本本地ID
        Add("版本地址", GetFirst(fields, "Oppo.Project.Adder", projectAddr), "wrap", link: true);
        Add("版本编号", GetFirst(fields, "projectVer", "ProjectVer", "projectVersion", "ProjectVersion", "Oppo.Project.Ver", "Oppo.ProjectVersion", "Oppo.Project.Ver.Num", projectVer), "mono");

        // 删除：测试阶段、是否用例发现、用例编号
        // Add("测试阶段", ...)
        // Add("是否用例发现", ...)
        // Add("用例编号", ...)

        // 删除：用例归属
        Add("是否新需求", GetFirst(fields, "isNewRequirement", "IsNewRequirement", "newRequirement", "NewRequirement", "Oppo.NewRequirement", isNewRequirement));
        Add("featureId", GetFirst(fields, "featureId", "FeatureId", "Oppo.FeatureId", "Oppo.Feature.Id", featureId), "mono");

        Add("缺陷来源", GetFirst(fields, "Oppo.Bug.Source.zh", defectSource));
        Add("上个版本是否存在", GetFirst(fields, "Oppo.Bug.IsExist.zh", previousVersionExists));
        Add("缺陷等级", GetFirst(fields, "Oppo.Bug.Severity", severity));

        Add("复现概率", GetFirst(fields, "Oppo.Bug.ReprodOdds.zh", repro));

        Add("APK名称", GetFirst(fields, "Oppo.Apk.Name", apk));
        Add("APK版本", GetFirst(fields, "Oppo.Apk.Ver", apkVer), "mono");
        Add("参考机", GetFirst(fields, "Oppo.Reference.Machine", reference));

        if (!string.IsNullOrWhiteSpace(shareUrl))
            Add("日志地址", shareUrl, "wrap", link: true);

        sb.Append("</div></div>");

        // Noah page has lots of fields. To avoid "诺亚上有但工具没显示",
        // we provide a searchable full field list (default collapsed).
        try
        {
            var all = fields
                .Select(kv => new
                {
                    Key = kv.Key ?? "",
                    Val = (kv.Value?.ToString() ?? "").Replace("\r", " ").Replace("\n", " ").Trim()
                })
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (all.Count > 0)
            {
                sb.Append("<details>");
                sb.Append("<summary>全部字段（Noah返回，可搜索）</summary>");
                sb.Append("""
<div class="tools">
  <input id="q" placeholder="搜索字段名/值（例如：Project / 责任人 / createTime）"/>
  <label><input type="checkbox" id="nonempty" checked/> 只看非空</label>
  <span class="muted" id="cnt"></span>
</div>
<table id="tbl">
""");
                foreach (var it in all)
                {
                    var full = it.Val ?? "";
                    var show = full;
                    if (show.Length > 480) show = show[..480] + "…";

                    // data attributes are used for fast filtering in JS
                    sb.Append("<tr class=\"r\" data-k=\"").Append(E(it.Key)).Append("\" data-v=\"").Append(E(full)).Append("\">");
                    sb.Append("<td class=\"k2\">").Append(E(it.Key)).Append("</td>");
                    sb.Append("<td class=\"v2\" title=\"").Append(E(full)).Append("\">").Append(E(show)).Append("</td>");
                    sb.Append("</tr>");
                }
                sb.Append("</table>");
                sb.Append("""
<script>
(function(){
  const q = document.getElementById('q');
  const cb = document.getElementById('nonempty');
  const cnt = document.getElementById('cnt');
  const rows = Array.from(document.querySelectorAll('#tbl .r'));
  function norm(s){ return (s||'').toString().toLowerCase(); }
  function apply(){
    const qq = norm(q.value).trim();
    const nonempty = cb.checked;
    let shown = 0;
    for (const r of rows){
      const k = norm(r.dataset.k);
      const v = (r.dataset.v || '').toString();
      const vnorm = norm(v);
      const okEmpty = !nonempty || vnorm.trim().length > 0;
      const okQ = qq.length === 0 || k.includes(qq) || vnorm.includes(qq);
      const ok = okEmpty && okQ;
      r.style.display = ok ? '' : 'none';
      if (ok) shown++;
    }
    cnt.textContent = `显示 ${shown} / ${rows.length}`;
  }
  q.addEventListener('input', apply);
  cb.addEventListener('change', apply);
  apply();
})();
</script>
</details>
""");
            }
        }
        catch { }

        sb.Append("<div class=\"card\"><div class=\"k\" style=\"margin-bottom:8px;\">描述</div>");
        sb.Append("<div class=\"desc\">").Append(E(d)).Append("</div></div>");

        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static string GetFirst(Dictionary<string, object?> fields, params string[] keys)
    {
        foreach (var k in keys)
        {
            var v = GetStr(fields, k);
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return "";
    }

    private static string FindValueByContains(Dictionary<string, object?> fields, params string[] parts)
    {
        if (fields == null || fields.Count == 0) return "";
        bool Match(string key)
        {
            var s = key ?? "";
            foreach (var p in parts)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                if (s.IndexOf(p, StringComparison.OrdinalIgnoreCase) < 0) return false;
            }
            return true;
        }

        foreach (var kv in fields)
        {
            if (!Match(kv.Key)) continue;
            var v = kv.Value?.ToString() ?? "";
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return "";
    }

    private static string HtmlToPlainText(string html)
    {
        html ??= "";
        if (!html.Contains('<')) return html.Replace("\r\n", "\n");

        // keep line breaks
        var s = html;
        s = Regex.Replace(s, @"<\s*br\s*/?\s*>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</\s*p\s*>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<\s*p\b[^>]*>", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</\s*div\s*>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<\s*div\b[^>]*>", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<\s*li\b[^>]*>", "- ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</\s*li\s*>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</\s*ul\s*>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<\s*span\b[^>]*>", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</\s*span\s*>", "", RegexOptions.IgnoreCase);

        // strip any remaining tags
        s = Regex.Replace(s, @"<[^>]+>", "");
        s = System.Net.WebUtility.HtmlDecode(s);
        s = s.Replace("\r\n", "\n").Replace("\r", "\n");
        // collapse excessive blank lines
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }

    private static JsonElement FindBestAttachmentArray(JsonElement root)
    {
        // Try unwrapped common patterns first
        var r0 = root;
        if (r0.ValueKind == JsonValueKind.Object)
        {
            if (r0.TryGetProperty("Data", out var d)) r0 = d;
            else if (r0.TryGetProperty("data", out var d2)) r0 = d2;
        }

        var best = default(JsonElement);
        var bestScore = -1;

        void Consider(JsonElement arr)
        {
            if (arr.ValueKind != JsonValueKind.Array) return;
            var score = ScoreAttachmentArray(arr);
            if (score > bestScore)
            {
                bestScore = score;
                best = arr;
            }
        }

        // common keys
        if (r0.ValueKind == JsonValueKind.Object)
        {
            foreach (var k in new[] { "records", "Records", "list", "List", "rows", "Rows", "items", "Items", "data", "Data" })
            {
                if (r0.TryGetProperty(k, out var v))
                {
                    if (v.ValueKind == JsonValueKind.Object) v = UnwrapData(v);
                    Consider(v);
                }
            }
        }

        // recursive scan up to depth 4
        Scan(r0, 0);
        Scan(root, 0);

        return bestScore >= 0 ? best : root;

        void Scan(JsonElement el, int depth)
        {
            if (depth > 4) return;
            if (el.ValueKind == JsonValueKind.Array)
            {
                Consider(el);
                return;
            }
            if (el.ValueKind != JsonValueKind.Object) return;
            foreach (var p in el.EnumerateObject())
            {
                Scan(p.Value, depth + 1);
            }
        }
    }

    private static int ScoreAttachmentArray(JsonElement arr)
    {
        // Prefer arrays whose items look like attachment objects
        int score = 0;
        int count = 0;
        foreach (var it in arr.EnumerateArray())
        {
            if (it.ValueKind != JsonValueKind.Object) continue;
            count++;
            var hasName = !string.IsNullOrEmpty(GetStrAny(it, "fileName", "name", "originName", "originalName", "displayName"));
            var hasSize = !string.IsNullOrEmpty(GetStrAny(it, "fileSize", "size", "length"));
            var hasId = !string.IsNullOrEmpty(GetStrAny(it, "fileId", "id", "uuid", "fileUuid"));
            if (hasName) score += 10;
            if (hasSize) score += 3;
            if (hasId) score += 2;
            if (count >= 5) break;
        }
        score += Math.Min(30, count);
        return score > 0 ? score : -1;
    }

    private static string PrettySize(string s)
    {
        if (!long.TryParse(new string(s.Where(char.IsDigit).ToArray()), out var n) || n <= 0) return s;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double d = n;
        int idx = 0;
        while (d >= 1024 && idx < units.Length - 1) { d /= 1024; idx++; }
        return $"{d:0.##}{units[idx]}";
    }

    private static string TrimLong(string s, int max)
    {
        s ??= "";
        if (s.Length <= max) return s;
        return s[..max] + "\n...(truncated)";
    }

    private async Task<string> TryGetCommentsAsync(string bugId)
    {
        try
        {
            return await GetCommentListAsync(bugId, "缺陷管理");
        }
        catch (Exception ex)
        {
            return $"(评论拉取失败: {ex.Message})";
        }
    }

    public async Task<string> GetCommentListAsync(string workItemId, string project)
    {
        if (string.IsNullOrWhiteSpace(_token))
            throw new Exception("Missing token (SIAMTGT).");

        var url =
            "https://g-agile-dms.myoas.com/api/dms/api/landray/process/commentList" +
            "?collection=OPPO" +
            "&project=" + HttpUtility.UrlEncode(project ?? "") +
            "&workItemId=" + HttpUtility.UrlEncode(workItemId ?? "");

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        // Some environments require SIAMTGT cookie in addition to Authorization
        req.Headers.TryAddWithoutValidation("Cookie", $"SIAMTGT={_token}");

        using var resp = await _http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"commentList failed: HTTP {(int)resp.StatusCode}: {Trim(text)}");

        return FormatComments(text);
    }

    public async Task<string> GetCommentListHtmlAsync(string workItemId, string project)
    {
        if (string.IsNullOrWhiteSpace(_token))
            throw new Exception("Missing token (SIAMTGT).");

        var url =
            "https://g-agile-dms.myoas.com/api/dms/api/landray/process/commentList" +
            "?collection=OPPO" +
            "&project=" + HttpUtility.UrlEncode(project ?? "") +
            "&workItemId=" + HttpUtility.UrlEncode(workItemId ?? "");

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        req.Headers.TryAddWithoutValidation("Cookie", $"SIAMTGT={_token}");

        using var resp = await _http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"commentList failed: HTTP {(int)resp.StatusCode}: {Trim(text)}");

        return FormatCommentsHtml(text);
    }

    private static string FormatComments(string jsonText)
    {
        if (string.IsNullOrWhiteSpace(jsonText)) return "(empty)";
        using var doc = JsonDocument.Parse(jsonText);
        var root = UnwrapData(doc.RootElement);

        // commentList response often contains multiple arrays; pick the one that "looks like comments"
        var arr = FindBestCommentArray(root);
        if (arr.ValueKind != JsonValueKind.Array) return "(未识别到评论列表)";

        var items = arr.EnumerateArray().ToList();
        if (items.Count == 0) return "评论(0)";

        var sb = new StringBuilder();
        sb.Append("评论(").Append(items.Count).Append(")\n\n");
        foreach (var it in items)
        {
            // IMPORTANT: in Noah commentList, createdBy can be service account (e.g. alm),
            // and createdOnBehalfOf is the real author.
            var author = "";
            if (it.ValueKind == JsonValueKind.Object && it.TryGetProperty("createdOnBehalfOf", out var behalf))
            {
                if (behalf.ValueKind == JsonValueKind.String) author = behalf.GetString() ?? "";
                else author = GetStrAny(behalf, "displayName", "name", "userName", "uniqueName");
            }
            if (string.IsNullOrEmpty(author))
                author = GetStrAny(it, "createdByName", "creatorName", "userName", "author", "operatorName", "operateUserName", "createdByUserName");
            // createdBy nested (object or string) fallback
            if (string.IsNullOrEmpty(author) && it.ValueKind == JsonValueKind.Object && it.TryGetProperty("createdBy", out var createdBy))
            {
                if (createdBy.ValueKind == JsonValueKind.String) author = createdBy.GetString() ?? "";
                else author = GetStrAny(createdBy, "displayName", "name", "userName", "uniqueName");
            }
            var time = GetStrAny(it, "createTime", "createdTime", "createdAt", "time", "gmtCreate", "createDate");
            var content = GetStrAny(it, "content", "comment", "text", "body", "commentContent", "CommentContent");
            if (string.IsNullOrEmpty(content) && it.ValueKind == JsonValueKind.Object && it.TryGetProperty("commentContent", out var cc))
            {
                if (cc.ValueKind == JsonValueKind.String) content = cc.GetString() ?? "";
            }
            content = (content ?? "").Replace("\r\n", "\n").Trim();

            sb.Append("[").Append(time).Append("] ").Append(author).Append("\n");
            sb.Append(content).Append("\n\n");
        }
        return sb.ToString().TrimEnd();
    }

    private static string FormatCommentsHtml(string jsonText)
    {
        if (string.IsNullOrWhiteSpace(jsonText)) return "";
        using var doc = JsonDocument.Parse(jsonText);
        var root = UnwrapData(doc.RootElement);
        var arr = FindBestCommentArray(root);
        if (arr.ValueKind != JsonValueKind.Array) return "";

        var items = arr.EnumerateArray().ToList();
        var sb = new StringBuilder();
        sb.Append("""
<!doctype html>
<html><head><meta charset="utf-8"/>
<style>
  body{font-family:Segoe UI,Microsoft YaHei,Arial; background:#fff; color:#111; margin:12px;}
  .card{border:1px solid #cfcfcf; border-radius:6px; margin:10px 0; overflow:hidden;}
  .hdr{background:#e9e9e9; padding:8px 10px; font-weight:600; display:flex; gap:12px; align-items:center;}
  .time{font-family:Consolas,ui-monospace,monospace; font-weight:600;}
  .author{color:#333;}
  .body{padding:10px; white-space:pre-wrap; line-height:1.35;}
  a{color:#0969da; text-decoration:none;}
  a:hover{text-decoration:underline;}
  img{max-width:100%; border:1px solid #ddd; border-radius:4px; margin-top:8px;}
  .meta{color:#666; font-size:12px; margin-top:6px;}
</style>
</head><body>
""");

        sb.Append("<div class=\"meta\">评论(").Append(items.Count).Append(")</div>");

        foreach (var it in items)
        {
            var author = "";
            if (it.ValueKind == JsonValueKind.Object && it.TryGetProperty("createdOnBehalfOf", out var behalf))
            {
                if (behalf.ValueKind == JsonValueKind.String) author = behalf.GetString() ?? "";
                else author = GetStrAny(behalf, "displayName", "name", "userName", "uniqueName");
            }
            if (string.IsNullOrEmpty(author))
                author = GetStrAny(it, "createdByName", "creatorName", "userName", "author", "operatorName", "operateUserName", "createdByUserName");
            if (string.IsNullOrEmpty(author) && it.ValueKind == JsonValueKind.Object && it.TryGetProperty("createdBy", out var createdBy))
            {
                if (createdBy.ValueKind == JsonValueKind.String) author = createdBy.GetString() ?? "";
                else author = GetStrAny(createdBy, "displayName", "name", "userName", "uniqueName");
            }
            var time = GetStrAny(it, "createTime", "createdTime", "createdAt", "time", "gmtCreate", "createDate");
            var content = GetStrAny(it, "content", "comment", "text", "body", "commentContent", "CommentContent");
            if (string.IsNullOrEmpty(content) && it.ValueKind == JsonValueKind.Object && it.TryGetProperty("commentContent", out var cc) && cc.ValueKind == JsonValueKind.String)
                content = cc.GetString() ?? "";

            content ??= "";
            var looksHtml = content.Contains("<") && (content.Contains("<img") || content.Contains("<a") || content.Contains("<p") || content.Contains("<br"));
            var body = looksHtml ? content : LinkifyAndEscape(content);

            sb.Append("<div class=\"card\">");
            sb.Append("<div class=\"hdr\"><span class=\"time\">").Append(E(time)).Append("</span><span class=\"author\">").Append(E(author)).Append("</span></div>");
            sb.Append("<div class=\"body\">").Append(body).Append("</div>");
            sb.Append("</div>");
        }

        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static string E(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

    private static string LinkifyAndEscape(string s)
    {
        s ??= "";
        // escape first
        var esc = System.Net.WebUtility.HtmlEncode(s);
        // linkify basic http(s)
        var rx = new Regex(@"(https?://[^\s<]+)", RegexOptions.IgnoreCase);
        esc = rx.Replace(esc, m =>
        {
            var url = m.Value;
            return $"<a href=\"{url}\" target=\"_blank\">{url}</a>";
        });
        // simple image embedding for common extensions
        var imgRx = new Regex(@"(https?://[^\s<]+?\.(png|jpg|jpeg|gif|webp))", RegexOptions.IgnoreCase);
        esc = imgRx.Replace(esc, m =>
        {
            var url = m.Value;
            return $"<a href=\"{url}\" target=\"_blank\">{url}</a><br/><img src=\"{url}\"/>";
        });
        return esc.Replace("\r\n", "\n").Replace("\n", "<br/>");
    }

    private static JsonElement FindBestCommentArray(JsonElement root)
    {
        var best = default(JsonElement);
        var bestScore = -1;

        void Consider(JsonElement arr)
        {
            if (arr.ValueKind != JsonValueKind.Array) return;
            var score = ScoreCommentArray(arr);
            if (score > bestScore)
            {
                bestScore = score;
                best = arr;
            }
        }

        // Common keys first
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var k in new[] { "commentList", "CommentList", "comments", "Comments", "list", "List", "rows", "Rows", "records", "Records", "items", "Items" })
            {
                if (root.TryGetProperty(k, out var v))
                {
                    if (v.ValueKind == JsonValueKind.Object) v = UnwrapData(v);
                    Consider(v);
                }
            }
        }

        // Recursive scan (defensive)
        Scan(root, 0);
        return bestScore >= 0 ? best : root;

        void Scan(JsonElement el, int depth)
        {
            if (depth > 4) return;
            if (el.ValueKind == JsonValueKind.Array)
            {
                Consider(el);
                return;
            }
            if (el.ValueKind != JsonValueKind.Object) return;
            foreach (var p in el.EnumerateObject())
            {
                Scan(p.Value, depth + 1);
            }
        }
    }

    private static int ScoreCommentArray(JsonElement arr)
    {
        int score = 0;
        int count = 0;
        int almCount = 0;

        foreach (var it in arr.EnumerateArray())
        {
            if (it.ValueKind != JsonValueKind.Object) continue;
            count++;

            var hasTime = !string.IsNullOrEmpty(GetStrAny(it, "createTime", "createdTime", "createdAt", "time", "gmtCreate", "createDate"));
            var hasContent = !string.IsNullOrEmpty(GetStrAny(it, "content", "comment", "text", "body", "commentContent", "CommentContent"));
            var hasCreator = !string.IsNullOrEmpty(GetStrAny(it, "createdByName", "creatorName", "userName", "author", "operatorName", "operateUserName", "createdByUserName"));

            // Prefer createdOnBehalfOf as true author
            if (it.TryGetProperty("createdOnBehalfOf", out var behalf))
            {
                var a = behalf.ValueKind == JsonValueKind.String
                    ? (behalf.GetString() ?? "")
                    : GetStrAny(behalf, "displayName", "name", "userName", "uniqueName");
                if (!string.IsNullOrEmpty(a))
                {
                    hasCreator = true;
                    if (a.Contains("alm", StringComparison.OrdinalIgnoreCase)) almCount++;
                }
            }

            if (it.TryGetProperty("createdBy", out var createdBy))
            {
                var a = createdBy.ValueKind == JsonValueKind.String
                    ? (createdBy.GetString() ?? "")
                    : GetStrAny(createdBy, "displayName", "name", "userName", "uniqueName");
                if (!string.IsNullOrEmpty(a))
                {
                    hasCreator = true;
                    if (a.Contains("alm", StringComparison.OrdinalIgnoreCase)) almCount++;
                }
            }

            if (hasTime) score += 3;
            if (hasContent) score += 10;
            if (hasCreator) score += 5;

            if (count >= 8) break;
        }

        if (count == 0) return -1;
        score += Math.Min(30, count);

        // Penalize arrays where the sampled authors are overwhelmingly alm (likely auto/system stream)
        if (almCount >= Math.Max(3, count - 1)) score -= 20;

        return score > 0 ? score : -1;
    }

    private static JsonElement UnwrapData(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return root;
        if (root.TryGetProperty("Data", out var d)) return d;
        if (root.TryGetProperty("data", out var d2)) return d2;
        return root;
    }

    private static JsonElement FindFirstArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root;
        if (root.ValueKind != JsonValueKind.Object) return root;

        // Common keys first
        foreach (var k in new[] { "list", "List", "rows", "Rows", "records", "Records", "commentList", "CommentList", "items", "Items" })
        {
            if (root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array) return v;
        }

        // Fallback: first array property
        foreach (var p in root.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.Array) return p.Value;
        }
        return root;
    }

    private static string GetStrAny(JsonElement obj, params string[] keys)
    {
        if (obj.ValueKind != JsonValueKind.Object) return "";
        foreach (var k in keys)
        {
            if (obj.TryGetProperty(k, out var v))
            {
                if (v.ValueKind == JsonValueKind.String) return v.GetString() ?? "";
                if (v.ValueKind == JsonValueKind.Number) return v.ToString();
            }
        }
        return "";
    }

    public async Task<List<string>> FetchDownloadUrlsAsync(string bugId)
    {
        var fields = await GetBugFieldsAsync(bugId);
        string desc = GetStr(fields, "Oppo.Defect.Description");
        string logInfo = GetStr(fields, "Oppo.LogInfo");
        var share = ExtractPoseidonShareUrl(logInfo, desc);
        if (share.FileId == "" || share.Suffix == "") return new List<string>();
        var storageKeys = await GetPoseidonStorageKeysAsync(share.Suffix, share.FileId);
        var urls = new List<string>();
        foreach (var sk in storageKeys)
        {
            var url = await GetPoseidonPreSignedUrlAsync(share.Suffix, sk);
            if (!string.IsNullOrEmpty(url)) urls.Add(url);
        }
        return urls;
    }

    private async Task<Dictionary<string, object?>> GetBugFieldsAsync(string bugId)
    {
        if (string.IsNullOrWhiteSpace(_token))
            throw new Exception("Missing token (SIAMTGT).");

        var url = "https://g-agile-dms.myoas.com/api/dms/api/landray/process/workItem?collection=OPPO&workItem=" + HttpUtility.UrlEncode(bugId);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        using var resp = await _http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Noah getBugDetail failed: HTTP {(int)resp.StatusCode}: {Trim(text)}");

        // The actual response is a wrapper: { Data: { Fields: {...}}}
        // We parse minimally without strong DTOs to keep prototype small.
        var json = System.Text.Json.JsonDocument.Parse(text);
        var fields = json.RootElement;
        if (fields.TryGetProperty("Data", out var data) && data.TryGetProperty("Fields", out var f))
            fields = f;
        else if (fields.TryGetProperty("data", out var data2) && data2.TryGetProperty("fields", out var f2))
            fields = f2;

        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in fields.EnumerateObject())
        {
            dict[p.Name] = p.Value.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => p.Value.GetString(),
                System.Text.Json.JsonValueKind.Number => p.Value.ToString(),
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                _ => p.Value.ToString()
            };
        }
        return dict;
    }

    private static string GetStr(Dictionary<string, object?> fields, string key)
    {
        return fields.TryGetValue(key, out var v) ? (v?.ToString() ?? "") : "";
    }

    private static (string ShareUrl, string Suffix, string FileId) ExtractPoseidonShareUrl(string logInfo, string desc)
    {
        // Combine both fields (logInfo often contains html).
        var hay = (logInfo ?? "") + "\n" + (desc ?? "");

        // Align with main.cs idea: find full share url, then extract fileId and suffix.
        // Example: http://poseidon.adc.com/share/file/a9284942ea264af89f242a2959c7f1a1
        var m = Regex.Match(hay, @"(https?://poseidon[^\s""']*?/share/file/)([A-Za-z0-9]+)", RegexOptions.IgnoreCase);
        if (!m.Success) return ("", "", "");

        var shareUrl = m.Value.Trim();
        var fileId = m.Groups[2].Value.Trim();

        // More robust than "*.com/": allow ports and any poseidon host
        var m2 = Regex.Match(shareUrl, @"^(https?://poseidon[^/]+/)", RegexOptions.IgnoreCase);
        var suffix = m2.Success ? m2.Groups[1].Value.Trim() : "";
        return (shareUrl, suffix, fileId);
    }

    private async Task<List<string>> GetPoseidonStorageKeysAsync(string suffix, string fileId)
    {
        var url = new Uri(new Uri(suffix), "api/poseidon-service/shareFile/" + HttpUtility.UrlEncode(fileId)).ToString();
        var text = await _http.GetStringAsync(url);
        var json = System.Text.Json.JsonDocument.Parse(text);
        var root = json.RootElement;
        if (root.TryGetProperty("Data", out var d)) root = d;
        else if (root.TryGetProperty("data", out var d2)) root = d2;
        if (root.TryGetProperty("FileStorageKeys", out var keys) || root.TryGetProperty("fileStorageKeys", out keys))
        {
            var list = new List<string>();
            foreach (var k in keys.EnumerateArray())
            {
                if (k.TryGetProperty("StorageKey", out var sk) || k.TryGetProperty("storageKey", out sk))
                {
                    var s = sk.GetString();
                    if (!string.IsNullOrEmpty(s)) list.Add(s);
                }
            }
            return list;
        }
        return new List<string>();
    }

    private async Task<string> GetPoseidonPreSignedUrlAsync(string suffix, string storageKey)
    {
        var url = new Uri(new Uri(suffix), "api/poseidon-service/shareFile/download/preSignedUrl/" + HttpUtility.UrlEncode(storageKey)).ToString();
        var text = await _http.GetStringAsync(url);
        var json = System.Text.Json.JsonDocument.Parse(text);
        var root = json.RootElement;
        if (root.TryGetProperty("Data", out var d)) root = d;
        else if (root.TryGetProperty("data", out var d2)) root = d2;
        return root.ValueKind == System.Text.Json.JsonValueKind.String ? (root.GetString() ?? "") : root.ToString();
    }

    private static string Trim(string s)
    {
        s ??= "";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length > 300 ? s[..300] : s;
    }
}


