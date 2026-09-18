# Changelog

## 1.2.1

- Fix: a partial packet-level sample could hide a large audio/video
  divergence and make it look repairable. The optional packet timeline probe
  samples only two small windows, and its tail window is anchored on a seek
  target rather than on each stream's real end. When one stream ends before
  the container duration (or keyframes are sparse), the other stream's real
  end is not in the window, so the packet-measured drift could be a few
  seconds while the stream-level duration delta was much larger. v1.2.0 used
  the packet drift alone, so a stream-level delta of about +10.8 s with a
  sampled drift of about +1.9 s (limit `MaxAutoRepairDurationDeltaSeconds`
  2.0) was classified `AudioEndsLate` and planned as an `AudioTrim` of the
  smaller value. The candidate was then rejected by post-repair validation
  (nothing was modified), but the plan should never have been created.
- v1.2.1 treats packet evidence as a confirmation of stream metadata, never as
  an authority allowed to reduce it. A single shared rule
  (`AvEvidenceConsistency`) compares the metadata start offset, end offset and
  drift with the packet evidence. Tolerances are the classifier's own signal
  floors: 0.25 s for start offsets, 0.5 s for end offset / drift. Beyond them
  (including opposite directions) the pair is `Ambiguous` and always
  `ManualOnly`. Evidence that agrees within tolerance but falls on both sides
  of `MaxAutoRepairDurationDeltaSeconds`, or a packet drift the metadata does
  not corroborate, is also `Ambiguous`. The smaller of the two magnitudes is
  never used, and the planner re-applies the same rule as a second guard.
- `AudioPad`/`AudioTrim` now use the stream-level metadata delta as the amount
  (the value post-repair validation re-measures) instead of the sampled packet
  drift, and check the configured limit against the larger of the two.
- The metadata "duration" of a stream is a length for some muxers and an end
  timestamp for others (for example an ffmpeg-written Matroska `DURATION` tag on
  a stream with a start offset). When the start offset is meaningful, either
  reading may agree with the packets, so `ConstantOffset` files keep working; a
  file that starts in sync has a single reading.
- `AudioTimeStretch` now also requires the absolute divergence (the larger of
  the metadata and packet drifts) to be within
  `MaxAutoRepairDurationDeltaSeconds`, in addition to `AllowAudioReencode` and
  `MaxAutoRepairDriftRatio`. A relative limit never bypasses the absolute one:
  a consistent +10.8 s divergence on a long film has a tiny ratio but is
  `ManualOnly`. In practice `AudioTimeStretch` stays available only for small
  drifts that compound a meaningful start offset.
- No change to `TimestampShift`, multi-track `ManualOnly`,
  the never-re-encode-video rule, source-codec preservation for
  `AudioPad`/`AudioTrim`, post-repair validation, rollback or `DryRun`.
- Tests: new classifier/planner regression tests, plus a synthetic end-to-end
  case that reproduces the partial tail window with real FFmpeg/ffprobe.

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
