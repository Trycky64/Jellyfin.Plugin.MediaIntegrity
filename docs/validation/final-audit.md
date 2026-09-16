# Final code audit before v1.0.0

## Scope and results

Audit baseline: v0.2.0 plus the final writable-mirror/playback guards. Final Release build passes without warnings; 69 xUnit cases and the configuration JavaScript contract test pass. No encoding path or user-supplied encoder arguments exist. Both remux and full packet validation explicitly use `-c copy`; arguments use ProcessStartInfo.ArgumentList without a shell.

### Scanner and classification

The sequential library scanner remains responsive on the Pi. A real media fixture backup reports MP4_WRONG_SAMPLE_COUNT / RemuxRecommended; its previously repaired source reports Ok with H.264/AAC streams preserved. This inspection only read existing media and backups. Healthy MKV/M4A fixture queue entries remain unchanged and consume no attempt.

### Queue, retries and cancellation

The queue retains schema version 1 at `/config/data/media-integrity/repair-queue.json`. Each write uses a unique file and rename. A separate execution lock serializes complete scans and repairs. Failed entries with 1–2 attempts are retryable; the configured cap is 3 by default. Zero-attempt path failures and Processing entries left after a crash are excluded. A new full scan deliberately generates a fresh snapshot; it is not a mechanism for preserving retry history indefinitely.

Pi API tests exercise a cancelled scan and a cancelled repair waiting on the execution lock. The previous queue/stats remain unchanged in those tests. Actual blocked FIFO inputs exercise timeout and cancellation in ffprobe, remux FFmpeg and full packet-pass FFmpeg: timeouts around 1.0–1.15 seconds and cancellations around 0.26 seconds. The harness verifies no orphan remux output.

### Replacement, backups and recovery

Backups preserve relative paths and retain all collision variants. The SHA-256 manifest is flushed before replacement. Source and backup hashes must match; copied staging bytes must match the validated remux hash. The final writable destination must exist and match the backed-up source hash, preventing replacement through an incorrectly mapped mirror or over a changed file. A final Jellyfin playback check runs after hashing/staging and before the swap.

Regression tests inject a failure after the swap and prove successful rollback, retained backup/manifest and staging cleanup. Other tests prove that failed manifest writing, a mismatched mirror, late playback and pre-start cancellation never replace the original. Backups are retained even with KeepBackups=false. No cleanup scheduled task is registered.

### Security and defaults

Path traversal and symbolic-link protections remain in place. The Pi rejects `/etc/passwd` and a symlink source before a remux attempt. Existing destination protection is unchanged and reinforced by the final mirror validation. Defaults remain DryRun=true, MaxRepairsPerRun=1, AllowRepairOfCorrupted=false and ValidateFullPacketPass=true. Duration tolerance rejects NaN/infinite/negative values. The configuration XML's legacy KeepBackups flag is documented as non-operative for deletion.

### Playback

A real Jellyfin playback session for a disposable indexed fixture prevents repair, preserving bytes and attempt count. Jellyfin serves the same 261245 bytes through its direct-play endpoint during a full-library scan. Unit tests also exercise session detection and late playback after backup/staging. There is no atomic lock with Jellyfin's playback subsystem: a session starting after the final check, or an external player, remains a documented limitation.

### UI, logging and packaging

The existing plugin page is extended rather than duplicated. The administrator-only LastScan API returns data stored separately from configuration. Live API/config save and the served HTML JavaScript are tested; the JavaScript test checks stats text, paths, safe settings and save behavior. No connected browser exists in this session, so visual browser inspection is **not verified**.

Logs for successful repairs and scans show completed operations. Deliberately rejected fixtures produce expected warnings/errors; they are not treated as unexplained production failures. The container also emits unrelated Jellyfin/plugin warnings. No claim is made that every container log line is warning-free.

The ZIP contains only the DLL and version-matched meta.json. It is reproducible for the same compiled DLL using fixed entry timestamps/order, and was installed on the Pi. CI verifies formatting, build, 69 tests, JavaScript, harness compilation and packaging on Ubuntu.

## Explicitly deferred or limited

- Queue GUI (UI-011) and Clean Media Repair Backups remain deferred by specification.
- Visual browser verification is unavailable; functional UI/API verification passes.
- MaxParallelProbes is exposed but scanning remains sequential; PreserveOriginalContainer always preserves the extension.
- Packet-copy validation is structural, not decoded audio/video quality analysis.
- Trusted filesystem roots are required; path checks do not eliminate races against privileged concurrent filesystem changes.
- Abrupt power loss requires inspecting manifests/backups; in-process rollback cannot run without power.
- v1 stability refers to the documented automated and Pi validation, not an indefinite soak test.
