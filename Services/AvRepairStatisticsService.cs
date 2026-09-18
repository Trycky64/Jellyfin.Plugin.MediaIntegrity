using System.Text.Json;
using Jellyfin.Plugin.MediaIntegrity.Models;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.MediaIntegrity.Services;

/// <summary>
/// Atomically persists statistics for the most recent A/V repair task run.
/// </summary>
public sealed class AvRepairStatisticsService
{
    private readonly string _path;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public AvRepairStatisticsService(IApplicationPaths applicationPaths)
    {
        _path = Path.Combine(applicationPaths.DataPath, "media-integrity", "last-av-repair.json");
    }

    public async Task SaveAsync(LastAvRepairRunStats stats, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, stats, Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public async Task<LastAvRepairRunStats?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return await JsonSerializer.DeserializeAsync<LastAvRepairRunStats>(stream, Options, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }
}
