namespace Jellyfin.Plugin.MediaIntegrity.Models;

/// <summary>
/// Represents the result of validating a remuxed media file.
/// </summary>
public sealed class MediaValidationResult
{
    public bool Success { get; init; }

    public string SourcePath { get; init; } = string.Empty;

    public string OutputPath { get; init; } = string.Empty;

    public List<string> Errors { get; init; } = [];

    public MediaScanResult? SourceScan { get; init; }

    public MediaScanResult? OutputScan { get; init; }

    public int PacketValidationExitCode { get; init; }

    public string PacketValidationError { get; init; } = string.Empty;
}
