# WP03 installation recovery

Date: 2026-10-24. Scope: installation only. Status: **awaiting hardware evidence**.
No console access, owner account sign-in, library download or hardware game execution was performed.

## Input evidence and stage record

| App | Evidence class | Install | PE map/import/TLS | Engine/device/frame/input/gameplay/audio/save |
| --- | --- | --- | --- | --- |
| Duck Game (312530) | Reported failure near 50%; no first-error log supplied | Unknown cause; awaiting hardware evidence | Not reached in supplied evidence; architecture, selected executable and last API unknown | Untested; CLR/XNA/FNA is a separate WP07 gate |
| Little Nightmares (424840) | Measured historical build 502 storage-move screenshot after 67% | Installation blocked; awaiting hardware evidence | Not reached | Untested |
| LEGO Jurassic World (352400) | Measured historical build 502 storage-move screenshot after 48% | Installation blocked; awaiting hardware evidence | Not reached | Untested |
| Amazing Frog ? 2 (1559680) | Measured build 502: move of 6.4 GB failed with 0x80070027 at 35% | Installation blocked; awaiting hardware evidence | Not reached | Untested |

For the three historical failures the selected executable, actual runtime
architecture, last resolved API/interface and first guest fault remain unknown.
The first discriminating evidence for Duck is a categorized installation record,
not another percentage or a managed-runtime guess. Leafless's historical
`_Mac.app` path is retained as a depot-selection warning, not diagnosed here.

## Implemented contracts

- The actual C# downloader resolves Windows/shared-platform depots and licensed
  manifests before planning space. Only explicit access-denied depot-key replies
  can omit an optional depot; a job timeout cannot silently omit game content.
- Each current app/depot/manifest/file context verifies length and SHA-1 of every
  existing chunk. The payload itself is the persistent checkpoint: no sidecar
  percentage, full file length or previous completion marker is trusted. A new
  manifest changes the authoritative hashes; only matching ranges are reused.
  Partial writes and holes are reverified after reopening the stream.
- The planner sums remaining file growth from the resolved manifests, includes
  `DownloadCheckpoint.ReserveBytes` (default 64 MiB), and checks free space before
  any CDN chunk and before extending a file. Unknown capacity fails explicitly.
  Moving to another destination rechecks the complete source staging size plus
  reserve; the resumed download then replans remaining growth at that destination.
  Required and available byte counts are reported. Physical undercapacity remains
  a blocker; 15.3 GB cannot fit in 11.2 GB through retries.
- Chunk processing is sequential and bounded: decoded chunks at most 4 MiB,
  encrypted/compressed responses at most 8 MiB, manifest responses at most 32 MiB,
  and move buffers 64 KiB. Untrusted decompression size declarations are bounded.
  Manifest metadata still scales with file/chunk count, not downloaded payload size.
- CDN network attempts and verified chunk retries are bounded, as are CM/job
  restarts in DownloadManager. Short AES payloads, padding/decryption, compression,
  length/hash mismatch, network exhaustion, cancellation and both observed Windows
  disk-full HRESULTs receive distinct categories. Error records contain app,
  depot, manifest, relative file, chunk offset, byte counts and recoverability;
  raw exception bodies, endpoint URLs, tokens, keys and session payloads are omitted.
- Moves copy into a hidden tree, hash/readback each file, then rename on the same
  destination volume before deleting the intact source. Failure rolls back only
  owned staging. Existing destination app trees are refused rather than merged
  over installations or saves. Abandoned hidden staging can be rebuilt from the
  retained source. Concurrent external writers and real filesystem durability
  remain device/storage validation gates.
- A single `verified-v1` completion marker is published after every selected file
  passes readback and truncation; `.downloading` blocks readiness until commit.
  Legacy empty/stale markers and executable presence alone are no longer enough:
  older installations need a successful verification/download pass. Executable
  discovery also excludes macOS `.app` bundles. No runtime rating is upgraded.

## Verification boundary

`DownloadCheckpointTests` exercises stream/hash/capacity contracts.
The dedicated `tools/download-fixtures/Nativra.Download.Tests.csproj` links the production `SteamDownload.cs`,
`SteamDepot.cs`, `SteamVdf.cs`, `Proto.cs` and `GameStorage.cs` into the portable
suite; only the platform-independent helper is linked into the x86 test project.
Fixtures replace only WinRT storage adapters and Steam CM transport, use
local temporary files and an in-memory HTTP handler, and generate owned AES/ZIP/
protobuf depot content. Tests exercise actual RunAsync/repair/fetch/decrypt/
decompress/ready/move orchestration, including restart at 49/50/51%, changed
manifest, corruption, retry exhaustion, short encrypted payloads, disk-full
writes, move preflight/copy/commit rollback, and destination/save preservation.
These adapters do not establish WinRT broker or removable-drive behavior.
TypeScript tooling tests separately preserve installation/runtime classification.

Release .NET Native package validation uses Windows CI. Hardware acceptance is
not run: Duck must complete installation or produce a fresh actionable categorized
error; all four titles still require installation, repeatable launch, scene
progression, physical controller, audio and save/reload evidence, with build/date/
API/adapter/memory recorded. See `.codex-report.md` for the final check results.
