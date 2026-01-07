using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace BugLensLite.Services;

public sealed class ZipLogDownloader
{
    private readonly HttpClient _http = new();

    public string GetBugCacheDir(string bugId)
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BugLensLite",
            "bugs",
            bugId
        );
        Directory.CreateDirectory(baseDir);
        return baseDir;
    }

    private enum ArchiveKind
    {
        Unknown = 0,
        Zip,
        SevenZ,
        Rar
    }

    private static ArchiveKind DetectArchiveKind(string path)
    {
        try
        {
            if (!File.Exists(path)) return ArchiveKind.Unknown;
            var len = new FileInfo(path).Length;
            if (len < 64) return ArchiveKind.Unknown;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> hdr6 = stackalloc byte[6];
            var n = fs.Read(hdr6);
            if (n < 4) return ArchiveKind.Unknown;

            // ZIP: PK 03 04 / PK 05 06 / PK 07 08
            if (hdr6[0] == (byte)'P' && hdr6[1] == (byte)'K' &&
                (hdr6[2] == 3 || hdr6[2] == 5 || hdr6[2] == 7) &&
                (hdr6[3] == 4 || hdr6[3] == 6 || hdr6[3] == 8))
                return ArchiveKind.Zip;

            // 7z: 37 7A BC AF 27 1C
            if (n >= 6 &&
                hdr6[0] == 0x37 && hdr6[1] == 0x7A && hdr6[2] == 0xBC &&
                hdr6[3] == 0xAF && hdr6[4] == 0x27 && hdr6[5] == 0x1C)
                return ArchiveKind.SevenZ;

            // RAR: 52 61 72 21 1A 07 (00/01)
            if (n >= 6 &&
                hdr6[0] == 0x52 && hdr6[1] == 0x61 && hdr6[2] == 0x72 &&
                hdr6[3] == 0x21 && hdr6[4] == 0x1A && hdr6[5] == 0x07)
                return ArchiveKind.Rar;

            return ArchiveKind.Unknown;
        }
        catch { return ArchiveKind.Unknown; }
    }

    private static string ReadFileHeadAsText(string path, int maxChars = 600)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var take = Math.Min(bytes.Length, 2048);
            var text = System.Text.Encoding.UTF8.GetString(bytes, 0, take);
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length > maxChars ? text[..maxChars] : text;
        }
        catch { return ""; }
    }

    public async Task<string> DownloadArchiveAsync(string url, string destPathNoExt, Action<string>? onStatus = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPathNoExt)!);
        onStatus?.Invoke("downloading archive...");

        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        var expectedLen = resp.Content.Headers.ContentLength;
        var ct = resp.Content.Headers.ContentType?.ToString() ?? "";
        if (!string.IsNullOrEmpty(ct)) onStatus?.Invoke($"content-type: {ct}");

        await using var inStream = await resp.Content.ReadAsStreamAsync();
        var tmpPath = destPathNoExt + ".download";
        await using var outStream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await inStream.CopyToAsync(outStream);
        await outStream.FlushAsync();

        var actualLen = new FileInfo(tmpPath).Length;
        if (expectedLen.HasValue && expectedLen.Value > 0 && actualLen != expectedLen.Value)
        {
            // Likely truncated download
            try { File.Delete(tmpPath); } catch { }
            throw new Exception($"下载不完整：Content-Length={expectedLen.Value} 实际={actualLen}");
        }

        var kind = DetectArchiveKind(tmpPath);
        if (kind == ArchiveKind.Unknown)
        {
            var head = ReadFileHeadAsText(tmpPath);
            // clean up
            try { File.Delete(tmpPath); } catch { }
            throw new Exception($"下载内容不是已支持的压缩包(zip/7z/rar)。前缀内容：{head}");
        }

        var ext = kind switch
        {
            ArchiveKind.Zip => ".zip",
            ArchiveKind.SevenZ => ".7z",
            ArchiveKind.Rar => ".rar",
            _ => ".bin"
        };
        var finalPath = destPathNoExt + ext;
        try { if (File.Exists(finalPath)) File.Delete(finalPath); } catch { }
        File.Move(tmpPath, finalPath);

        onStatus?.Invoke($"downloaded({ext.Trim('.')})");
        return finalPath;
    }

    public bool ArchiveContainsSfLogs(string archivePath)
    {
        var kind = DetectArchiveKind(archivePath);
        if (kind == ArchiveKind.Zip)
        {
            using var zip = ZipFile.OpenRead(archivePath);
            foreach (var e in zip.Entries)
            {
                var p = (e.FullName ?? "").Replace('/', '\\');
                if (p.EndsWith("sf_logs.txt", StringComparison.OrdinalIgnoreCase) &&
                    p.IndexOf("\\display\\sf\\raw\\", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        using var arch = ArchiveFactory.Open(archivePath);
        foreach (var e in arch.Entries.Where(x => !x.IsDirectory))
        {
            var p = (e.Key ?? "").Replace('/', '\\');
            if (p.EndsWith("sf_logs.txt", StringComparison.OrdinalIgnoreCase) &&
                p.IndexOf("\\display\\sf\\raw\\", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    public void ExtractArchive(string archivePath, string extractDir, Action<string>? onStatus = null, Action<double>? onProgress = null)
    {
        onStatus?.Invoke("extracting...");
        if (Directory.Exists(extractDir))
        {
            try { Directory.Delete(extractDir, recursive: true); } catch { }
        }
        Directory.CreateDirectory(extractDir);

        var kind = DetectArchiveKind(archivePath);
        if (kind == ArchiveKind.Zip)
        {
            // Use stream to tolerate other processes holding the file.
            using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
            var total = Math.Max(1, zip.Entries.Count);
            var idx = 0;
            foreach (var e in zip.Entries)
            {
                if (string.IsNullOrEmpty(e.FullName)) continue;
                // directory entry
                if (e.FullName.EndsWith("/", StringComparison.Ordinal) || e.FullName.EndsWith("\\", StringComparison.Ordinal)) continue;
                var outPath = Path.Combine(extractDir, e.FullName.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                using var inS = e.Open();
                using var outS = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                inS.CopyTo(outS);
                idx++;
                if (idx % 8 == 0) onProgress?.Invoke(idx / (double)total);
            }
            onProgress?.Invoke(1.0);
        }
        else
        {
            using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var arch = ArchiveFactory.Open(fs);
            var entries = arch.Entries.Where(x => !x.IsDirectory).ToList();
            var total = Math.Max(1, entries.Count);
            var idx = 0;
            foreach (var e in entries)
            {
                try
                {
                    e.WriteToDirectory(extractDir, new ExtractionOptions
                    {
                        ExtractFullPath = true,
                        Overwrite = true
                    });
                }
                catch
                {
                    // Some entries may not have streams (e.g., metadata); skip and continue.
                }
                idx++;
                if (idx % 8 == 0) onProgress?.Invoke(idx / (double)total);
            }
            onProgress?.Invoke(1.0);
        }

        onStatus?.Invoke("extracted");
    }

    public void ExtractSelectedEntries(
        string archivePath,
        string extractDir,
        IEnumerable<string> selectedEntries,
        Action<string>? onStatus = null,
        Action<double>? onProgress = null)
    {
        var want = new HashSet<string>(selectedEntries.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Replace('/', '\\')),
            StringComparer.OrdinalIgnoreCase);
        if (want.Count == 0) throw new Exception("No selected entries to extract.");

        Directory.CreateDirectory(extractDir);
        onStatus?.Invoke("extracting (selected only)...");

        var ext = Path.GetExtension(archivePath).ToLowerInvariant();
        if (ext == ".zip")
        {
            using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
            var entries = zip.Entries.Where(e => !string.IsNullOrWhiteSpace(e.FullName)).ToList();
            var total = Math.Max(1, want.Count);
            var done = 0;
            foreach (var e in entries)
            {
                var key = (e.FullName ?? "").Replace('/', '\\');
                if (!want.Contains(key)) continue;
                if (key.EndsWith("\\") || key.EndsWith("/")) continue;
                var outPath = Path.Combine(extractDir, key);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                using var inS = e.Open();
                using var outS = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                inS.CopyTo(outS);
                done++;
                onProgress?.Invoke(done / (double)total);
            }
            onProgress?.Invoke(1.0);
            onStatus?.Invoke("extracted (selected)");
            return;
        }

        // Prefer explicit 7z/rar handlers to avoid autodetect failures.
        if (ext == ".7z")
        {
            using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var arch = SevenZipArchive.Open(fs);
            var entries = arch.Entries.Where(e => !e.IsDirectory).ToList();
            var total = Math.Max(1, want.Count);
            var done = 0;
            foreach (var e in entries)
            {
                var key = (e.Key ?? "").Replace('/', '\\');
                if (!want.Contains(key)) continue;
                e.WriteToDirectory(extractDir, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
                done++;
                onProgress?.Invoke(done / (double)total);
                if (done >= want.Count) break;
            }
            onProgress?.Invoke(1.0);
            onStatus?.Invoke("extracted (selected)");
            return;
        }

        if (ext == ".rar")
        {
            using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var arch = RarArchive.Open(fs);
            var entries = arch.Entries.Where(e => !e.IsDirectory).ToList();
            var total = Math.Max(1, want.Count);
            var done = 0;
            foreach (var e in entries)
            {
                var key = (e.Key ?? "").Replace('/', '\\');
                if (!want.Contains(key)) continue;
                e.WriteToDirectory(extractDir, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
                done++;
                onProgress?.Invoke(done / (double)total);
                if (done >= want.Count) break;
            }
            onProgress?.Invoke(1.0);
            onStatus?.Invoke("extracted (selected)");
            return;
        }

        // Fallback: reader
        using (var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = ReaderFactory.Open(fs))
        {
            var total = Math.Max(1, want.Count);
            var done = 0;
            while (reader.MoveToNextEntry())
            {
                var entry = reader.Entry;
                if (entry.IsDirectory) continue;
                var key = (entry.Key ?? "").Replace('/', '\\');
                if (!want.Contains(key)) continue;
                var outPath = Path.Combine(extractDir, key);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                reader.WriteEntryToFile(outPath, new ExtractionOptions { ExtractFullPath = false, Overwrite = true });
                done++;
                onProgress?.Invoke(done / (double)total);
                if (done >= want.Count) break;
            }
            onProgress?.Invoke(1.0);
            onStatus?.Invoke("extracted (selected)");
        }
    }

    public async Task<(string ExtractDir, LogProjectInfo Info)> DownloadExtractAndDiscoverAsync(
        List<string> candidateUrls,
        string bugId,
        LogProjectDiscovery discovery,
        Action<string>? onStatus = null)
    {
        if (candidateUrls.Count == 0) throw new Exception("No download URLs.");

        var cacheDir = GetBugCacheDir(bugId);
        for (int i = 0; i < candidateUrls.Count; i++)
        {
            var url = candidateUrls[i];
            var baseName = Path.Combine(cacheDir, $"poseidon_{i + 1}");
            onStatus?.Invoke($"尝试下载包 {i + 1}/{candidateUrls.Count} ...");
            // one retry for flaky network
            string archivePath;
            try
            {
                archivePath = await DownloadArchiveAsync(url, baseName, onStatus);
            }
            catch (Exception ex1)
            {
                onStatus?.Invoke($"下载失败：{ex1.Message}；重试一次…");
                await Task.Delay(600);
                try
                {
                    foreach (var f in Directory.GetFiles(cacheDir, $"poseidon_{i + 1}.*"))
                        try { File.Delete(f); } catch { }
                }
                catch { }
                archivePath = await DownloadArchiveAsync(url, baseName, onStatus);
            }

            onStatus?.Invoke("检查包内容...");
            if (!ArchiveContainsSfLogs(archivePath))
            {
                onStatus?.Invoke("该包不包含 display/sf/raw/**/sf_logs.txt，尝试下一个");
                continue;
            }

            var extractDir = Path.Combine(cacheDir, $"extracted_{i + 1}");
            ExtractArchive(archivePath, extractDir, onStatus);

            onStatus?.Invoke("定位 sf_logs.txt ...");
            var info = discovery.Discover(extractDir);
            if (!string.IsNullOrEmpty(info.SfLogsPath))
            {
                onStatus?.Invoke("找到 sf_logs.txt");
                return (extractDir, info);
            }

            onStatus?.Invoke("解压后仍未找到 sf_logs.txt，尝试下一个");
        }

        throw new Exception("所有下载包都未找到 sf_logs.txt（display/sf/raw/**/sf_logs.txt）");
    }
}


