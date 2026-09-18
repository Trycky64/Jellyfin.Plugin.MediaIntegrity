# Changelog

## 1.2.0

- Classify audio/video timeline anomalies (`ConstantOffset`, `DurationMismatch`,
  `ProgressiveDrift`, `AudioEndsEarly`, `AudioEndsLate`, `TimelineMetadataOnly`,
  `Ambiguous`, `UnsafeToAutoRepair`) with an explicit confidence score and reason,
  independently confirmed by packet-level evidence whenever available.
- Add a deterministic repair planner that turns a classification into one of
  `TimestampShift`, `AudioTimeStretch`, `AudioPad`, `AudioTrim`, `StreamCopyRemux`
  or `ManualOnly`, gated by every configured safety limit.
- Add a bounded FFmpeg execution engine: video is never re-encoded by any
  strategy; a constant offset is corrected with a pure stream-copy timestamp
  shift; progressive drift, padding and trimming re-encode only the one
  targeted audio track and require the new opt-in `AllowAudioReencode`.
- Any anomaly whose magnitude exceeds a hard 30-second safety ceiling, or whose
  packet timeline is discontinuous, is always `UnsafeToAutoRepair` and is never
  auto-repaired, regardless of configuration.
- Reuse the existing transactional backup/replace/rollback pipeline for every
  A/V repair; add an opt-in `DeleteBackupAfterSuccessfulValidation` that only
  removes a backup after full post-replacement validation succeeds, never
  after a failure or a rollback.
- Add A/V repair configuration: `EnableAudioVideoRepair` (default `false`),
  `AllowAudioReencode` (default `false`), `MaxAudioVideoRepairsPerRun`,
  `MaxAutoRepairOffsetSeconds`, `MaxAutoRepairDurationDeltaSeconds`,
  `MaxAutoRepairDriftRatio`, `MinRepairConfidence`, `AudioReencodeCodec`.
- Add an `A/V Repair` scheduled task, independent from `Media Remux Repair`,
  and expose classification/repair counters via the existing configuration
  page and a new `MediaIntegrity/LastAvRepairRun` endpoint.
- `AudioPad`/`AudioTrim` preserve the source audio track's own codec (a small,
  explicit set of known-safe encoders) instead of forcing a fixed re-encode
  codec; a source codec with no known safe encoder is left `ManualOnly`
  rather than silently transcoded. `AudioTimeStretch` is unaffected and
  still uses the configurable `AudioReencodeCodec`.
- Support multiple audio tracks per file: each track is classified and planned
  independently. Automatic execution is limited to files where exactly one
  track needs a repair; a file where two or more tracks would each need one
  falls back to `ManualOnly` for all of them (tested to avoid a small
  collateral timestamp shift from chaining repair passes; see
  `docs/validation/v1.2.0-av-repair.md`).

All defaults remain safe: `EnableAudioVideoRepair=false` means A/V repair never
runs even when anomalies are detected; `DryRun=true` remains the default; a
large duration mismatch (for example a long silent intro/outro, or an
intentionally shorter track) is always classified `UnsafeToAutoRepair` and is
never auto-repaired.

## 1.1.1

- Exclude attached pictures and video streams without a finite positive
  stream-local duration from source A/V timeline diagnostics.
- Keep true temporal MJPEG and all other eligible video streams in analysis;
  no A/V diagnostic performs an automatic repair or destructive change.

## 1.1.0

- Detect and report source audio/video start, duration and end timeline differences per video/audio stream pair.
- Report structured, diagnostic-only A/V reason codes. No automatic synchronization, stream removal or transcoding is performed.
- Add per-track language, title, disposition and A/V ratio context, bounded last-scan diagnostics, and an optional packet timestamp check.
- Validate source diagnostics, multi-audio, controlled false-positive cases, packet timeout/cancellation and performance on Raspberry Pi 4.

## 1.0.1

- Reject a stream-copy remux candidate when it changes the duration or start
  time of an audio/video stream beyond a narrow explicit tolerance.
- Reject candidates that introduce material A/V duration drift, even when the
  overall container duration remains within the legacy tolerance.
- Capture `time_base`, `start_time`, and `duration` for every ffprobe stream;
  preserve stream ordering and reject missing or additional streams.
- Fail closed when an audio/video stream lacks timing required to verify the
  candidate, and record a precise deterministic reason in the repair queue.
- Validate the installed writable file with the same rules after the final swap
  and roll back if it no longer matches the validated candidate.

This release does not add encoding, `atempo`, `asetpts`, resampling, or codec
conversion. `DryRun=true`, `MaxRepairsPerRun=1`, and all other safety defaults
remain unchanged.
