using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.MediaIntegrity.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrity.Services;

/// <summary>
/// Loads and persists the media integrity repair queue.
/// </summary>
public sealed class RepairQueueService
{
    public const int CurrentSchemaVersion = 1;

    private const string DirectoryName = "media-integrity";
    private const string QueueFileName = "repair-queue.json";

    private static readonly JsonSerializerOptions SerializerOptions =
        CreateSerializerOptions();

    private readonly ILogger<RepairQueueService> _logger;
    private readonly SemaphoreSlim _queueLock = new(1, 1);
    private readonly SemaphoreSlim _executionLock = new(1, 1);

    /// <summary>
    /// Serializes complete scan/repair executions, not just individual JSON writes.
    /// A waiting scheduled task remains cancellable.
    /// </summary>
    public async Task<IDisposable> AcquireExecutionAsync(CancellationToken cancellationToken)
    {
        await _executionLock.WaitAsync(cancellationToken);
        return new ExecutionLease(_executionLock);
    }

    private sealed class ExecutionLease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }


    private readonly string _directoryPath;
    private readonly string _queuePath;

    public RepairQueueService(
        IApplicationPaths applicationPaths,
        ILogger<RepairQueueService> logger)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);

        _logger = logger;

        _directoryPath = Path.Combine(
            applicationPaths.DataPath,
            DirectoryName);

        _queuePath = Path.Combine(
            _directoryPath,
            QueueFileName);
    }

    /// <summary>
    /// Gets the absolute path of the repair queue.
    /// </summary>
    public string QueuePath => _queuePath;

    /// <summary>
    /// Gets whether the repair queue currently exists.
    /// </summary>
    public bool Exists => File.Exists(_queuePath);

    /// <summary>
    /// Creates an empty repair queue using the current schema.
    /// </summary>
    public static RepairQueue CreateEmpty()
    {
        return new RepairQueue
        {
            Version = CurrentSchemaVersion,
            GeneratedAt = DateTimeOffset.UtcNow,
            Summary = new RepairQueueSummary(),
            Files = []
        };
    }

    /// <summary>
    /// Loads the repair queue.
    /// Returns an empty queue when no persisted queue exists.
    /// </summary>
    public async Task<RepairQueue> LoadAsync(
        CancellationToken cancellationToken)
    {
        await _queueLock.WaitAsync(cancellationToken);

        try
        {
            if (!File.Exists(_queuePath))
            {
                _logger.LogInformation(
                    "No repair queue exists at {QueuePath}.",
                    _queuePath);

                return CreateEmpty();
            }

            try
            {
                await using var stream = new FileStream(
                    _queuePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    useAsync: true);

                var queue =
                    await JsonSerializer.DeserializeAsync<RepairQueue>(
                        stream,
                        SerializerOptions,
                        cancellationToken);

                if (queue is null)
                {
                    throw new InvalidDataException(
                        "Repair queue JSON contains no queue object.");
                }

                ValidateQueue(queue);
                NormalizeQueue(queue);

                _logger.LogInformation(
                    "Repair queue loaded from {QueuePath} with {Count} item(s).",
                    _queuePath,
                    queue.Files.Count);

                return queue;
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (JsonException ex)
            {
                return HandleCorruptedQueue(
                    "Repair queue contains invalid JSON.",
                    ex);
            }
            catch (InvalidDataException ex)
            {
                return HandleCorruptedQueue(
                    ex.Message,
                    ex);
            }
        }
        finally
        {
            _queueLock.Release();
        }
    }

    /// <summary>
    /// Atomically writes the repair queue to disk.
    /// </summary>
    public async Task SaveAsync(
        RepairQueue queue,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queue);

        ValidateQueueForSave(queue);

        await _queueLock.WaitAsync(cancellationToken);

        try
        {
            Directory.CreateDirectory(_directoryPath);

            queue.Version = CurrentSchemaVersion;

            var temporaryPath = Path.Combine(
                _directoryPath,
                $"{QueueFileName}.{Guid.NewGuid():N}.tmp");

            try
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        queue,
                        SerializerOptions,
                        cancellationToken);

                    await stream.FlushAsync(
                        cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();

                File.Move(
                    temporaryPath,
                    _queuePath,
                    overwrite: true);

                _logger.LogInformation(
                    "Repair queue written to {QueuePath} with {Count} item(s).",
                    _queuePath,
                    queue.Files.Count);
            }
            finally
            {
                TryDeleteTemporaryFile(
                    temporaryPath);
            }
        }
        finally
        {
            _queueLock.Release();
        }
    }

    private RepairQueue HandleCorruptedQueue(
        string reason,
        Exception exception)
    {
        var timestamp =
            DateTimeOffset.UtcNow.ToString(
                "yyyyMMdd-HHmmss");

        var corruptedPath = Path.Combine(
            _directoryPath,
            $"repair-queue.corrupt-{timestamp}.json");

        try
        {
            File.Move(
                _queuePath,
                corruptedPath,
                overwrite: false);

            _logger.LogError(
                exception,
                "{Reason} The invalid queue was moved to {CorruptedPath}.",
                reason,
                corruptedPath);
        }
        catch (Exception moveException)
            when (moveException is IOException
                or UnauthorizedAccessException)
        {
            _logger.LogError(
                moveException,
                "{Reason} The invalid queue could not be moved away from {QueuePath}.",
                reason,
                _queuePath);
        }

        return CreateEmpty();
    }

    private static void ValidateQueue(
        RepairQueue queue)
    {
        if (queue.Version <= 0)
        {
            throw new InvalidDataException(
                $"Invalid repair queue schema version: {queue.Version}.");
        }

        if (queue.Version > CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Repair queue schema version {queue.Version} "
                + $"is newer than supported version {CurrentSchemaVersion}.");
        }
    }

    private static void ValidateQueueForSave(
        RepairQueue queue)
    {
        if (queue.Summary is null)
        {
            throw new InvalidDataException(
                "Repair queue summary cannot be null.");
        }

        if (queue.Files is null)
        {
            throw new InvalidDataException(
                "Repair queue files collection cannot be null.");
        }

        foreach (var item in queue.Files)
        {
            if (string.IsNullOrWhiteSpace(item.Path))
            {
                throw new InvalidDataException(
                    "Repair queue contains an item with an empty path.");
            }
        }
    }

    private static void NormalizeQueue(
        RepairQueue queue)
    {
        queue.Summary ??=
            new RepairQueueSummary();

        queue.Files ??=
            [];

        foreach (var item in queue.Files)
        {
            item.Issues ??=
                [];

            item.LastError ??=
                string.Empty;

            item.Path ??=
                string.Empty;

            item.Extension ??=
                string.Empty;

            item.Container ??=
                string.Empty;
        }
    }

    private static void TryDeleteTemporaryFile(
        string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort cleanup only.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup only.
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy =
                JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        options.Converters.Add(
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: true));

        return options;
    }
}
