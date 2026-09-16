using System.Text.Json;
using Jellyfin.Plugin.MediaIntegrity.Models;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.MediaIntegrity.Services;

/// <summary>
/// Atomically persists statistics only after a completed scan.
/// </summary>
public sealed class ScanStatisticsService
{
    private readonly string _path;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public ScanStatisticsService(IApplicationPaths applicationPaths)
    {
        _path = Path.Combine(applicationPaths.DataPath, "media-integrity", "last-scan.json");
    }

    public async Task SaveAsync(LastScanStats stats, CancellationToken cancellationToken)
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

    public async Task<LastScanStats?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return await JsonSerializer.DeserializeAsync<LastScanStats>(stream, Options, cancellationToken);
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
