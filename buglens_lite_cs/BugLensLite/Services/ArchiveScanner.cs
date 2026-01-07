using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Archives.SevenZip;

namespace BugLensLite.Services;

public sealed class ArchiveScanner
{
    public ArchiveCandidates Scan(string archivePath)
    {
        if (!File.Exists(archivePath)) throw new FileNotFoundException("archive not found", archivePath);

        var ext = Path.GetExtension(archivePath).ToLowerInvariant();
        if (ext == ".zip")
        {
            using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
            var names = zip.Entries.Select(e => e.FullName).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            return Classify(names);
        }

        // Prefer explicit handlers for 7z/rar (autodetect may fail on some streams)
        if (ext == ".7z")
        {
            using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var arch = SevenZipArchive.Open(fs);
            var names = arch.Entries
                .Where(e => !e.IsDirectory)
                .Select(e => e.Key ?? "")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
            return Classify(names);
        }

        if (ext == ".rar")
        {
            using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var arch = RarArchive.Open(fs);
            var names = arch.Entries
                .Where(e => !e.IsDirectory)
                .Select(e => e.Key ?? "")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
            return Classify(names);
        }

        using (var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var arch = ArchiveFactory.Open(fs))
        {
            var names = arch.Entries
                .Where(e => !e.IsDirectory)
                .Select(e => e.Key ?? "")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
            return Classify(names);
        }
    }

    private static ArchiveCandidates Classify(List<string> entryNames)
    {
        string Norm(string p) => (p ?? "").Replace('/', '\\');

        bool IsSfLog(string p)
        {
            p = Norm(p).ToLowerInvariant();
            return p.EndsWith("\\sf_logs.txt") || p.EndsWith("sf_logs.txt");
        }

        bool IsAndroidLog(string p)
        {
            p = Norm(p).ToLowerInvariant();
            // Some packages don't keep the same folder structure; match by filename.
            var name = Path.GetFileName(p);
            return name.StartsWith("android_log_", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
        }

        bool IsVideo(string p)
        {
            // Recording naming varies; treat any common video suffix as a recording candidate.
            p = Norm(p);
            var ext = Path.GetExtension(p).ToLowerInvariant();
            return ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm" or ".3gp" or ".m4v" or ".flv";
        }

        int ScoreSf(string p)
        {
            p = Norm(p).ToLowerInvariant();
            int s = 0;
            if (p.Contains("\\display\\sf\\raw\\")) s += 1000;
            if (p.Contains("\\display\\sf\\")) s += 200;
            s += Math.Min(80, p.Count(ch => ch == '\\'));
            return s;
        }

        var sf = entryNames.Where(IsSfLog).Distinct().OrderByDescending(ScoreSf).ToList();
        var android = entryNames.Where(IsAndroidLog).Distinct().ToList();
        int ScoreVideo(string p)
        {
            p = Norm(p).ToLowerInvariant();
            int s = 0;
            if (p.Contains("\\screen_record\\")) s += 500;
            s += Math.Min(60, p.Count(ch => ch == '\\'));
            return s;
        }

        var videos = entryNames.Where(IsVideo).Distinct().OrderByDescending(ScoreVideo).ToList();

        return new ArchiveCandidates
        {
            SfLogsEntries = sf,
            AndroidLogEntries = android,
            ScreenRecordEntries = videos
        };
    }
}

public sealed class ArchiveCandidates
{
    public List<string> SfLogsEntries { get; init; } = new();
    public List<string> AndroidLogEntries { get; init; } = new();
    public List<string> ScreenRecordEntries { get; init; } = new();
}


