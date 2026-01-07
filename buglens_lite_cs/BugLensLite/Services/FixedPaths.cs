using System;
using System.IO;

namespace BugLensLite.Services;

public static class FixedPaths
{
    // Users download archives manually into this folder.
    // We pick Downloads to match common "no install/no config" expectations.
    public static string GetBaseDownloadDir()
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(user, "Downloads", "BugLensLiteLogs");
    }

    public static string GetBugDownloadDir(string bugId)
    {
        return Path.Combine(GetBaseDownloadDir(), bugId);
    }

    // Tool extracts archives into LocalAppData cache.
    public static string GetBugImportCacheDir(string bugId)
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BugLensLite",
            "imports",
            bugId
        );
        return baseDir;
    }
}






