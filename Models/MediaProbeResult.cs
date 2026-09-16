namespace Jellyfin.Plugin.MediaIntegrity.Models;

/// <summary>
/// Represents the raw and parsed result of an ffprobe execution.
/// </summary>
public sealed class MediaProbeResult
{
    public int ExitCode { get; set; }

    public string StandardOutput { get; set; } = string.Empty;

    public string StandardError { get; set; } = string.Empty;

    public MediaScanResult ScanResult { get; set; } = new();
}
