namespace Jellyfin.Plugin.MediaIntegrity.Models;

/// <summary>
/// Represents the result of a lossless FFmpeg remux operation.
/// </summary>
public sealed class MediaRemuxResult
{
    public bool Success { get; init; }

    public int ExitCode { get; init; }

    public string SourcePath { get; init; } = string.Empty;

    public string OutputPath { get; init; } = string.Empty;

    public string StandardOutput { get; init; } = string.Empty;

    public string StandardError { get; init; } = string.Empty;

    public TimeSpan Duration { get; init; }

    public long OutputSizeBytes { get; init; }
}
