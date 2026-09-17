namespace Jellyfin.Plugin.MediaIntegrity.Models;

/// <summary>A packet presentation timestamp measured in seconds.</summary>
public readonly record struct PacketTimestamp(int StreamIndex, double PtsSeconds, double DurationSeconds);
