using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.MediaIntegrity;
using Jellyfin.Plugin.MediaIntegrity.Services;
using Microsoft.Extensions.Logging.Abstractions;

// Runs inside the target container. Does not encode media or touch user originals.
var detector = new MediaIssueDetector();
var probe = new MediaProbeService(NullLogger<MediaProbeService>.Instance, detector);
var report = new Dictionary<string, object>();
if (args.Length == 3 && args[0] == "--compare")
{
    var source = await probe.ProbeAsync(args[1], 120, CancellationToken.None);
    var candidate = await probe.ProbeAsync(args[2], 120, CancellationToken.None);
    var issues = StreamTimelineValidator.Validate(
        source.ScanResult,
        candidate.ScanResult,
        new PluginConfiguration());

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        Source = source.ScanResult,
        Candidate = candidate.ScanResult,
        TimelineIssues = issues,
        Accepted = issues.Count == 0
    }, new JsonSerializerOptions { WriteIndented = true }));
    return;
}

if (args.Length > 0)
{
    foreach (var path in args)
    {
        var result = await probe.ProbeAsync(path, 120, CancellationToken.None);
        report[path] = new
        {
            Status = result.ScanResult.Status.ToString(),
            result.ExitCode,
            result.ScanResult.Streams,
            result.ScanResult.Issues
        };
    }

    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    return;
}

var run = "process-" + Guid.NewGuid().ToString("N");
var writable = "/repair-media/series/Media Integrity Test/" + run;
var sourceRoot = "/media/series/Media Integrity Test/" + run;
var temporary = "/cache/media-integrity/" + run;
Directory.CreateDirectory(writable);
Directory.CreateDirectory(temporary);
var configuration = new PluginConfiguration
{
    DryRun = true,
    RemuxTimeoutSeconds = 1,
    ProbeTimeoutSeconds = 1,
    TempRoot = temporary
};
var config = new PluginConfigurationService(NullLogger<PluginConfigurationService>.Instance, configuration);
var security = new PathSecurityService(NullLogger<PathSecurityService>.Instance);
var mapper = new PathMapper(config, security, NullLogger<PathMapper>.Instance);
var remux = new MediaRemuxService(config, mapper, NullLogger<MediaRemuxService>.Instance);
var validation = new MediaValidationService(config, probe, NullLogger<MediaValidationService>.Instance);
try
{
    var fifo = writable + "/blocked.mp4";
    var source = sourceRoot + "/blocked.mp4";
    using (var mkfifo = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { fifo } })!)
    {
        await mkfifo.WaitForExitAsync();
        if (mkfifo.ExitCode != 0) throw new Exception("mkfifo failed");
    }

    await Expect<TimeoutException>("probeTimeout", () => probe.ProbeAsync(source, 1, CancellationToken.None));
    using (var cancel = new CancellationTokenSource(250))
    {
        await Expect<OperationCanceledException>("probeCancellation", () => probe.ProbeAsync(source, 60, cancel.Token));
    }

    await Expect<TimeoutException>("remuxTimeout", () => remux.RemuxAsync(source, CancellationToken.None));
    using (var cancel = new CancellationTokenSource(250))
    {
        await Expect<OperationCanceledException>("remuxCancellation", () => remux.RemuxAsync(source, cancel.Token));
    }

    // Exercise the full packet pass directly: normal ValidateAsync first probes,
    // which would otherwise time out on this intentionally blocked input.
    var packetPass = typeof(MediaValidationService).GetMethod("ValidatePacketsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    await Expect<TimeoutException>("packetValidationTimeout", () =>
        (Task)packetPass.Invoke(validation, [source, 1, CancellationToken.None])!);
    using (var cancel = new CancellationTokenSource(250))
    {
        await Expect<OperationCanceledException>("packetValidationCancellation", () =>
            (Task)packetPass.Invoke(validation, [source, 60, cancel.Token])!);
    }

    if (Directory.GetFiles(temporary, "*", SearchOption.AllDirectories).Length != 0)
    {
        throw new Exception("Orphaned remux temporary files");
    }

    report["noOrphanedTemporaryFiles"] = true;
}
finally
{
    Directory.Delete(writable, recursive: true);
    Directory.Delete(temporary, recursive: true);
}

Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

async Task Expect<T>(string name, Func<Task> action) where T : Exception
{
    var timer = Stopwatch.StartNew();
    try
    {
        await action();
    }
    catch (T)
    {
        if (timer.Elapsed.TotalSeconds > 10) throw new Exception(name + " took too long");
        report[name] = new { Passed = true, Seconds = timer.Elapsed.TotalSeconds };
        return;
    }

    throw new Exception(name + " did not throw " + typeof(T).Name);
}
