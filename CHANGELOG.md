# Changelog

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
