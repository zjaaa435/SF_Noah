using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BugLensLite.Services;

public sealed class LogProjectDiscovery
{
    public LogProjectInfo Discover(string root)
    {
        root = root ?? "";
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            throw new DirectoryNotFoundException($"log root not found: {root}");

        var all = DiscoverAll(root);
        var sfLogs = all.SfLogsPaths.FirstOrDefault() ?? "";
        var mp4 = all.ScreenRecordPaths.FirstOrDefault() ?? "";
        var androidLogs = all.AndroidLogPaths;

        return new LogProjectInfo
        {
            Root = root,
            SfLogsPath = sfLogs,
            ScreenRecordPath = mp4,
            AndroidLogPaths = androidLogs
        };
    }

    public LogProjectCandidates DiscoverAll(string root)
    {
        var sf = FindSfLogsAll(root);
        var mp4 = FindVideoAll(root);
        var android = FindAndroidLogsAll(root);
        return new LogProjectCandidates
        {
            Root = root,
            SfLogsPaths = sf,
            ScreenRecordPaths = mp4,
            AndroidLogPaths = android
        };
    }

    private static List<string> FindSfLogsAll(string root)
    {
        // Real packages vary:
        // - display/sf/raw/<ts>/sf_logs.txt (preferred)
        // - display/sf/<...>/sf_logs.txt
        // - sometimes just sf_logs.txt somewhere under extracted root
        var all = Directory.GetFiles(root, "sf_logs.txt", SearchOption.AllDirectories)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (all.Count == 0) return new List<string>();

        static int Score(string path)
        {
            var p = path.Replace('/', '\\').ToLowerInvariant();
            int s = 0;
            if (p.Contains("\\display\\sf\\raw\\")) s += 1000;
            if (p.Contains("\\display\\sf\\")) s += 200;
            // prefer deeper (often contains timestamp folder) but not too deep
            s += Math.Min(50, p.Count(ch => ch == '\\'));
            return s;
        }

        return all
            .OrderByDescending(Score)
            .ThenByDescending(File.GetLastWriteTimeUtc)
            .ToList();
    }

    private static List<string> FindVideoAll(string root)
    {
        static bool IsVideo(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm" or ".3gp" or ".m4v" or ".flv";
        }

        static int Score(string path)
        {
            var p = path.Replace('/', '\\').ToLowerInvariant();
            int s = 0;
            if (p.Contains("\\screen_record\\")) s += 500;
            // prefer deeper paths a bit (usually timestamp folders), but cap
            s += Math.Min(40, p.Count(ch => ch == '\\'));
            return s;
        }

        var files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(IsVideo)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(Score)
            .ThenByDescending(File.GetLastWriteTimeUtc)
            .ToList();

        return files;
    }

    private static List<string> FindAndroidLogsAll(string root)
    {
        var list = new List<string>();
        try
        {
            list.AddRange(
                Directory.GetFiles(root, "android_log_*.txt", SearchOption.AllDirectories)
                    .Where(p => p.Replace('/', '\\').IndexOf("\\common\\minilog\\", StringComparison.OrdinalIgnoreCase) >= 0)
            );
        }
        catch { }
        return list
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
    }
}

public sealed class LogProjectInfo
{
    public string Root { get; init; } = "";
    public string SfLogsPath { get; init; } = "";
    public string ScreenRecordPath { get; init; } = "";
    public List<string> AndroidLogPaths { get; init; } = new();
}

public sealed class LogProjectCandidates
{
    public string Root { get; init; } = "";
    public List<string> SfLogsPaths { get; init; } = new();
    public List<string> ScreenRecordPaths { get; init; } = new();
    public List<string> AndroidLogPaths { get; init; } = new();
}


