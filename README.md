# Jellyfin Media Integrity

## Audio/video timeline diagnostics (v1.1.1)

During scans, the plugin compares each temporal video stream with each audio stream. Attached pictures and video streams without a finite positive stream duration are excluded; codec, dimensions and file names are not used to make that decision. It reports significant start offsets, duration mismatches, end mismatches and suspected progressive drift. Conservative diagnostic thresholds are 250 ms for start offsets and the larger of 500 ms or 0.05% of stream duration for duration/end differences. Small codec delay and container rounding are ignored. Optional packet sampling can provide an additional drift warning; it is disabled by default because it needs a second ffprobe pass. The last-scan page shows bounded per-track diagnostics and the ratio `audio duration / video duration`.

These diagnostics describe the source media only. They never trigger an automatic repair, retiming, stream removal, audio/video encoding, `atempo`, `asetpts` or resampling. The v1.0.1 remux validator remains responsible for rejecting drift introduced by a candidate repair.

Detect container and stream issues in a Jellyfin library and repair eligible files with lossless FFmpeg stream copy.

Current release line: v1.2.0. Real remux and, as an explicit opt-in, real A/V repair are both fully supported; safe defaults remain unchanged.

## Audio/video repair (v1.2.0)

Starting in v1.2.0, every A/V timeline anomaly reported by the diagnostics above is also **classified** into one of: `ConstantOffset`, `DurationMismatch`, `ProgressiveDrift`, `AudioEndsEarly`, `AudioEndsLate`, `TimelineMetadataOnly`, `Ambiguous`, or `UnsafeToAutoRepair`. Classification is always computed and shown in scan statistics; it never modifies a file by itself.

A classification only becomes an actual repair when **all** of the following are true: `EnableAudioVideoRepair=true`, the classification's confidence meets `MinRepairConfidence`, and the anomaly's magnitude is within the configured `MaxAutoRepairOffsetSeconds` / `MaxAutoRepairDurationDeltaSeconds` / `MaxAutoRepairDriftRatio` limits. Anything else — including every anomaly larger than a hard, non-configurable 30-second safety ceiling, and every anomaly with a discontinuous packet timeline — is `UnsafeToAutoRepair` or `Ambiguous` and always requires manual review. **A large duration mismatch is never assumed to be damage**: a long silent intro/outro or a deliberately shorter track is left for a human to decide.

Repair strategies, run by the independent **A/V Repair** scheduled task:

| Classification | Strategy | Re-encodes | Requires |
| --- | --- | --- | --- |
| ConstantOffset | TimestampShift | Nothing (pure stream copy) | `EnableAudioVideoRepair` |
| ProgressiveDrift | AudioTimeStretch (`atempo`) | Only the targeted audio track | `EnableAudioVideoRepair` + `AllowAudioReencode` |
| AudioEndsEarly | AudioPad (`apad`) | Only the targeted audio track | `EnableAudioVideoRepair` + `AllowAudioReencode` |
| AudioEndsLate | AudioTrim (`atrim`) | Only the targeted audio track | `EnableAudioVideoRepair` + `AllowAudioReencode` |
| everything else | ManualOnly | Nothing (no repair is attempted) | — |

**Video is never re-encoded by any strategy, under any configuration.** `AudioPad`/`AudioTrim` re-encode the one targeted audio track even though the edit is a clean silence pad or edge trim, because FFmpeg's `apad`/`atrim` filters require decoding that track; this is why they are gated by `AllowAudioReencode` exactly like time-stretch, not treated as "free" like a stream copy. Unlike `AudioTimeStretch`, they always preserve the source track's own codec (from a small, explicit, known-safe set) rather than transcoding to `AudioReencodeCodec`; a source codec with no known safe encoder is left `ManualOnly`.

Every A/V repair reuses the same transactional pipeline as remux repair: the candidate is built outside the source tree, probed and compared against the source (stream identity, timeline tolerance, chapters, full packet-copy pass), re-classified to confirm the targeted anomaly is actually gone, backed up, replaced, and validated again after the swap — with automatic rollback to the verified backup on any failure.

Every audio track on a file is classified and planned independently, and a track's classification is always visible even when it isn't auto-repaired. **Automatic repair only executes when exactly one track on a file needs it.** When two or more tracks on the same file would each need an automatic repair, every one of them falls back to `ManualOnly`: end-to-end testing found that chaining a stream-copy retime with a following per-stream re-encode on the same file can introduce a small (tens-of-milliseconds) collateral timestamp shift on completely untouched streams, including video, which this release cannot yet prove safe. This is a deliberate, tested scope decision, not an oversight; multi-track automatic repair may be revisited in a future release.

An opt-in `DeleteBackupAfterSuccessfulValidation` removes the per-file backup, but strictly only after full post-replacement validation has passed — never after a failed repair, and never after a rollback.

All of this is disabled by default: `EnableAudioVideoRepair=false` means classification-only, exactly like v1.1.x.

## Features

- **Media Integrity Scan**: inspect supported library files with ffprobe, classify Healthy / Warning / RemuxRecommended / Corrupted / Unreadable, classify any A/V timeline anomaly, and persist a repair queue.
- **Media Remux Repair**: remux every stream with `-map 0 -c copy`, validate stream identity, per-stream timeline, chapters and a full packet-copy pass, then optionally replace through the writable mirror.
- **A/V Repair** (v1.2.0, opt-in): repair the safest, most-bounded A/V anomalies (constant offset, bounded end mismatch, small progressive drift) with `TimestampShift`, `AudioPad`, `AudioTrim` or `AudioTimeStretch`. Video is never re-encoded by any strategy.
- Dry-run enabled by default; at most one real repair per task run.
- Transactional backups, SHA-256 recovery manifests, collision-safe names and rollback on detected replacement failure.
- Playback checks before remux, before replacement preparation and immediately before the final swap.
- Existing Jellyfin configuration page with persisted last-scan statistics.

## Compatibility and prerequisites

Target: **Jellyfin Server 10.11.11**, **.NET 9**. The plugin uses the Jellyfin-provided FFmpeg and ffprobe at `/usr/lib/jellyfin-ffmpeg/` when available, falling back to `ffmpeg`/`ffprobe` on `PATH`. Keep Jellyfin pinned to a tested version; `latest` may change compatibility.

Tested on: Linux ARM64 (Raspberry Pi 4, Docker). The plugin itself is not specific to any architecture, operating system, or container runtime.

Provide sufficient free disk space for the original backup, remux and replacement staging copy. Normal library paths must live under `/media`; map the same host directories under `/repair-media`. The Jellyfin process needs write permissions on repair, backup, cache and config directories. The plugin never invokes sudo.

## Installation

1. Download the ZIP from [GitHub Releases](https://github.com/Trycky64/Jellyfin.Plugin.MediaIntegrity/releases).
2. Stop Jellyfin. Extract `Jellyfin.Plugin.MediaIntegrity.dll` and `meta.json` into `/config/plugins/Media Integrity/`. Replace both files when upgrading. Do not keep duplicate plugin DLLs in other plugin directories.
3. Start Jellyfin and confirm Media Integrity appears in Dashboard -> Plugins and both tasks appear under Scheduled Tasks.
4. Open the plugin configuration, check paths, leave **Dry run enabled**, and save.

## Recommended Docker mounts

```yaml
services:
  jellyfin:
    image: jellyfin/jellyfin:10.11.11
    volumes:
      - <your-media>/Movies:/media/Movies:ro
      - <your-media>/series:/media/series:ro
      - <your-media>/Movies:/repair-media/Movies:rw
      - <your-media>/series:/repair-media/series:rw
      - <your-backups>:/repair-backups:rw
      - ./cache:/cache:rw
      - ./config:/config:rw
```

Replace `<your-media>` with the host path to your media library and `<your-backups>` with the host path where repair backups should be stored.

Never make `/media` writable. Create the backup and cache directories before starting a task. Replacement staging files are created beside the writable destination so the final rename stays on the same filesystem; remux temporary files live under `/cache/media-integrity`.

## Configuration

| Setting | Default | Meaning |
| --- | --- | --- |
| DryRun | true | Remux and validate in cache without backing up or replacing media |
| MaxRepairsPerRun | 1 | Maximum real repair candidates per run |
| EnableVideoFiles / EnableAudioFiles | true / true | Categories included in scans |
| SourceRoot | /media | Read-only library root |
| RepairRoot | /repair-media | Writable mirror of the same source tree |
| BackupRoot | /repair-backups | Permanent original backups |
| TempRoot | /cache/media-integrity | Remux workspace |
| ProbeTimeoutSeconds | 120 | Per-probe timeout |
| MaxParallelProbes | 1 | Reserved concurrency ceiling; current scanner runs sequentially |
| PreserveOriginalContainer | true | Current remux always preserves the extension/container |
| AllowRepairOfCorrupted | false | Opt-in for eligible corruption; unreadable sources still fail validation |
| ValidateFullPacketPass | true | Full packet-copy validation; keep enabled |
| KeepBackups | true | Legacy setting; backups are always retained, including when false |
| MaxRepairAttempts | 3 | Automatic retry cap; see retry rules below |
| RemuxTimeoutSeconds / ValidationTimeoutSeconds | 3600 / 3600 | Process timeouts |
| DurationToleranceSeconds | 2 | Maximum source/output duration difference |
| StreamDurationToleranceSeconds | 0.05 | Maximum source-to-candidate duration change for each audio/video stream |
| StreamStartTimeToleranceSeconds | 0.01 | Maximum source-to-candidate start-time change for each audio/video stream |
| EnableAudioVideoRepair | false | Master switch for A/V repair; classification always runs regardless of this setting |
| AllowAudioReencode | false | Required for AudioTimeStretch, AudioPad and AudioTrim; video is never re-encoded regardless |
| MaxAudioVideoRepairsPerRun | 1 | Maximum real A/V repair candidates per run |
| MaxAutoRepairOffsetSeconds | 5.0 | Largest constant offset eligible for automatic TimestampShift |
| MaxAutoRepairDurationDeltaSeconds | 2.0 | Largest bounded duration delta eligible for automatic AudioPad/AudioTrim |
| MaxAutoRepairDriftRatio | 0.02 | Largest packet-confirmed drift, as a fraction of duration, eligible for automatic AudioTimeStretch |
| MinRepairConfidence | 0.75 | Minimum classification confidence required for auto-repair eligibility |
| AudioReencodeCodec | aac | FFmpeg encoder used only for AudioTimeStretch; AudioPad/AudioTrim always preserve the source track's own codec instead |
| DeleteBackupAfterSuccessfulValidation | false | Deletes the per-file backup only after full post-replacement validation succeeds |

Supported video extensions: MP4, M4V, MKV, WebM, MOV, AVI, TS, M2TS, MTS, MPG, MPEG.
Supported audio extensions: M4A, MP3, FLAC, OGG, OPUS, AAC. Extension support does not guarantee that every codec/container combination can be remuxed.

## Scan and queue

Run **Media Integrity Scan** from Scheduled Tasks. It scans supported files known to Jellyfin, rather than arbitrary unindexed files. A completed scan writes `/config/data/media-integrity/repair-queue.json` and `/config/data/media-integrity/last-scan.json`. A cancelled scan leaves the previous completed queue/statistics intact. The UI shows the last completed scan, not live progress or current repair totals.

Warnings alone do not enter the repair queue. Unreadable files never enter it. Some corrupted files are only queued when a repairable issue exists without a critical issue. Scan and repair executions are serialized. A waiting task can be cancelled without changing the active task or queue. A completed scan creates a new queue snapshot. Queue browsing in the GUI is deferred.

## Repair and DryRun

Start with a scan, inspect logs and run **Media Remux Repair** with DryRun=true. Dry-run still performs remux and validation in cache, but never replaces originals and does not consume attempts. Pending items remain pending.

Real repair is opt-in: disable DryRun deliberately, keep MaxRepairsPerRun=1, run the task, inspect its result, then restore DryRun=true. The plugin does not expose encoder arguments or codec conversion. All FFmpeg invocations use `-c copy`. Media reported as playing is deferred. A final playback check after backup/staging prevents replacement if a session starts during preparation. The writable mirror must exist and its SHA-256 must still match the backed-up source; a wrong mount or changed file aborts the swap.

### Timeline safety in v1.0.1

Stream copy can rebuild timestamps in a damaged container without changing any codec. Before a candidate can replace media, Media Integrity now compares source and candidate by stream index and order. For every video and audio stream it verifies codec identity, `time_base`, `start_time`, and `duration`; it also compares every video/audio duration relationship. A candidate is rejected when it changes a stream duration by more than 0.05 seconds, changes a stream start time by more than 0.01 seconds, introduces material A/V duration drift, or has unknown audio/video timing that cannot be verified. These conservative checks are separate from the legacy 2-second container-duration tolerance.

The plugin does not use `atempo`, `asetpts`, resampling, or any encoder to work around a rejected candidate. A rejected timeline is preserved in the queue error with a reason such as `StreamDurationChanged`, `StreamStartTimeChanged`, or `AudioVideoDriftIntroduced`; deterministic timing failures do not consume repeated automatic retries.

Since v0.2, failed entries with a consumed attempt are retried on later task runs until MaxRepairAttempts. Rejected paths with zero attempts are excluded. Entries left Processing after an abrupt crash require manual inspection of the backup manifest and current media before retry; the outcome may be ambiguous. A new scan generates a fresh queue, so do not use repeated scans to bypass the attempt cap.

## Backups and recovery

`/media/series/Show/episode.mp4` maps to `/repair-backups/series/Show/episode.mp4`. Collisions add `.original-<UTC timestamp>-<unique id>` before the extension. Every successful backup transaction writes `<backup>.metadata.json` with schema version, source/backup/repaired paths, timestamp, SHA-256 hashes, algorithm and plugin version **before replacement**.

The repaired hash describes the validated replacement candidate. A manifest is a recovery record, not proof that the swap completed: cancellation or failure can leave a backup/manifest without an installed repair. Compare hashes against the current file. Backups are never automatically deleted, including legacy KeepBackups=false. A future **Clean Media Repair Backups** task can use these versioned manifests; it is not shipped in v0.1.

To restore manually:

1. Enable DryRun, stop repair tasks and stop playback/Jellyfin.
2. Read the manifest and verify the backup's SHA-256 against backupHash. Keep the backup and manifest.
3. Copy the backup to a new staging file beside the destination under `/repair-media`; verify the staged SHA-256 again, then rename over the destination. Do not copy through `/media`.
4. Restart Jellyfin, refresh the affected item and run a new integrity scan.

The plugin attempts rollback from the verified backup after a detected post-swap failure. An abrupt power loss cannot execute in-process rollback; use the manifest and retained backup for recovery. Keep a separate independent backup of valuable media.

## Security

- Source, cache, repair and backup paths are checked against configured roots.
- Path traversal and symbolic links/reparse points are rejected; destinations are revalidated before the swap.
- Source library mounts remain read-only; media changes use only the repair mirror.
- All streams are mapped; codec, order, stream count, resolution/audio parameters, chapters, stream timeline and A/V duration relationships are compared before replacement and again after the final swap.
- Statistics API requires Jellyfin administrator authorization.
- Never edit the queue with untrusted paths, relax directory permissions, or allow untrusted users to mutate repair/cache roots while tasks run.

## Limitations

- Checks are structural and packet-based, not full video/audio decoding or proof that content is visually correct.
- Filesystem path checks narrow races but cannot guarantee safety against a concurrent privileged filesystem attacker.
- Playback detection uses Jellyfin sessions, including a final check after the backup/staging copies. External players and playback starting after that final check cannot be locked out by this plugin.
- No automatic backup cleanup; monitor free disk space.
- Current scan is sequential; changing MaxParallelProbes does not speed it up.
- Preservation of all metadata/attachments depends on FFmpeg container support; validation fails closed on detected incompatibility.
- Some valid files lack stream-level timing. The repair pipeline refuses their automatic replacement rather than assuming the timing is preserved.
- UI JavaScript and live API were tested; visual browser inspection was unavailable in the validation session.
- A/V repair classification uses only two packet samples (start and end windows); it cannot distinguish a truly uniform drift from a bounded change concentrated at one edge except by magnitude, so classification thresholds (not audio content analysis) decide between ProgressiveDrift and AudioEndsEarly/AudioEndsLate.
- `AudioPad`/`AudioTrim` re-encode the targeted audio track (FFmpeg filter-graph requirement); they are not literally lossless like stream-copy remux, even though the edit itself only adds silence or trims a bounded edge.
- A/V repair intentionally never runs on more than `MaxAudioVideoRepairsPerRun` files per real task execution; do not raise it to bypass a staged rollout.

## Troubleshooting

Read `docker logs <container>` and search for MediaIntegrity, MediaReplacementService or MediaValidationService. A task can finish while individual queue entries have errors; inspect LastError and status. For permission/path failures, inspect `docker inspect <container>`, ensure every root exists, and verify read-only and writable aliases refer to the same host directories. For timeouts, lower workload or deliberately adjust the relevant limit. For failed validation, retain the original and investigate FFmpeg stderr; do not bypass validation.

If a task is interrupted, cancel it in Scheduled Tasks and confirm it becomes idle. Inspect cache and repair destinations for `.repairing.*` or `.replacing` files before restarting. Only remove known orphaned temporary files after confirming no repair is active. Never remove a user backup as cleanup.

## Uninstallation

Enable DryRun, stop tasks and Jellyfin, then remove the plugin installation directory. Keep `/repair-backups` and the queue/statistics/configuration until recovery is no longer needed. Restart Jellyfin. Removing the plugin does not restore already repaired media.

## Build and test from source

```powershell
dotnet restore Jellyfin.Plugin.MediaIntegrity.sln
dotnet format Jellyfin.Plugin.MediaIntegrity.sln --verify-no-changes --no-restore
dotnet build Jellyfin.Plugin.MediaIntegrity.sln -c Release --no-restore
dotnet test Jellyfin.Plugin.MediaIntegrity.sln -c Release --no-build
node scripts/test-ui.cjs
./scripts/package.ps1
```

The ZIP contains only the plugin DLL and version-matched meta.json; Jellyfin dependencies are supplied by the server. Stable ZIP entry order/timestamps make packaging the same DLL reproducible. CI runs on ubuntu-latest and uploads ZIP and test results.

Integration scripts in `scripts/` target a Docker-based Jellyfin deployment configured via environment variables (`JELLYFIN_URL`, `JELLYFIN_API_KEY`, `JELLYFIN_CONTAINER`, `JELLYFIN_CONFIG_ROOT`, `JELLYFIN_MEDIA_ROOT`, `JELLYFIN_BACKUP_ROOT`). They use an existing API key without printing credentials, save the queue before injection and restore safe configuration in a finally block. They require pre-existing synthetic fixtures under `/cache/media-integrity-fixtures/sources`. They never generate encoded media or alter user originals. Evidence is recorded under [docs/validation](docs/validation).
