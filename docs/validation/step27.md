# Step 27 — 2026-09-15

- Release build: 0 warnings, 0 errors; 43/43 tests passed.
- Target Docker container: two real MP4 fixture repairs via scheduled-task API.
- Relative backup path: series/Media Integrity Test/release-f88e983a407f48aebb4bb242d93a93b9.
- Original SHA-256: 24EDCEA9A0B10EBC5AF3F11D7B5FC88CFFF281B36FC340DDB945AF38F398C9AC.
- Repaired SHA-256: D3FD2CE3C504A6F8636E766F9F07C64B2878868A30A9D4F49390772EA307342D.
- Both backups and manifests retained with KeepBackups=false; collision has timestamp suffix.
- Manifest source/backup/repaired hashes verified against files; ffprobe stream signatures identical.
- Queue, backups and manifests survived Docker restart.
- Test directories removed; original queue restored byte-for-byte from /tmp/release-f88e983a407f48aebb4bb242d93a93b9-queue.json.
- DryRun=true, MaxRepairsPerRun=1, RepairRoot=/repair-media restored by API.
- Both /media mounts remain read-only.
- BACK-001 through BACK-008 satisfied: no automatic cleanup; versioned BackupMetadata is the future cleanup contract. Clean Media Repair Backups is deferred.
