using System;

namespace BugLensLite.Services;

public sealed class AttachmentItem
{
    public string Name { get; init; } = "";
    public long SizeBytes { get; init; }
    public string Time { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
}

public sealed class AttachmentFetchResult
{
    public string Error { get; init; } = "";
    public AttachmentItem[] Items { get; init; } = Array.Empty<AttachmentItem>();
}






