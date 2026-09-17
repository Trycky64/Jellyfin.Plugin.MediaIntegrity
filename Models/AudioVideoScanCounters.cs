namespace Jellyfin.Plugin.MediaIntegrity.Models;

/// <summary>Counts source A/V diagnostics per scanned media file.</summary>
public sealed class AudioVideoScanCounters
{
    public const int MaxStoredDiagnostics = 100;

    private readonly List<AudioVideoScanDiagnostic> _diagnostics = [];

    public IReadOnlyList<AudioVideoScanDiagnostic> Diagnostics => _diagnostics;
    public int AffectedMedia { get; private set; }
    public int StartOffsets { get; private set; }
    public int DurationMismatches { get; private set; }
    public int EndMismatches { get; private set; }
    public int SuspectedDrifts { get; private set; }
    public int IncompleteTimelineData { get; private set; }

    public void Add(MediaScanResult result)
    {
        var avIssues = result.Issues.Where(static issue => issue.Code.StartsWith("AudioVideo", StringComparison.Ordinal) || issue.Code == "TimelineDataIncomplete").ToList();
        if (avIssues.Count > 0) AffectedMedia++;
        StartOffsets += avIssues.Count(static issue => issue.Code == "AudioVideoStartOffset");
        DurationMismatches += avIssues.Count(static issue => issue.Code == "AudioVideoDurationMismatch");
        EndMismatches += avIssues.Count(static issue => issue.Code == "AudioVideoEndMismatch");
        SuspectedDrifts += avIssues.Count(static issue => issue.Code is "AudioVideoDriftSuspected" or "AudioVideoPacketDriftSuspected");
        IncompleteTimelineData += avIssues.Count(static issue => issue.Code == "TimelineDataIncomplete");
        foreach (var issue in avIssues)
        {
            if (_diagnostics.Count >= MaxStoredDiagnostics)
            {
                break;
            }

            _diagnostics.Add(new AudioVideoScanDiagnostic
            {
                FileName = Path.GetFileName(result.Path),
                Code = issue.Code,
                Severity = issue.Severity,
                VideoStreamIndex = issue.VideoStreamIndex,
                AudioStreamIndex = issue.AudioStreamIndex,
                AudioLanguage = issue.AudioLanguage,
                AudioTitle = issue.AudioTitle,
                AudioIsDefault = issue.AudioIsDefault,
                StartDeltaSeconds = issue.StartDeltaSeconds,
                DurationDeltaSeconds = issue.DurationDeltaSeconds,
                EndDeltaSeconds = issue.EndDeltaSeconds,
                AudioVideoDurationRatio = issue.AudioVideoDurationRatio
            });
        }
    }
}
