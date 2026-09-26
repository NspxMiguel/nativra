# Brawlhalla (Adobe AIR captive runtime) — where it stands

Symptom: Adobe AIR shows "Application descriptor could not be found for this
application" (error id `ERROR__NO_DESC`, distinct from `ERROR__INVALID_DESC`).

Measured on the console (build 292, FILEWATCH=on):
- `META-INF\AIR\application.xml` is found (`FindFirstFileW`, attributes) and
  `CreateFileW` on it succeeds.
- No empty `ReadFile`, no zero `GetFileSize(Ex)` was logged for any handle.
- `META-INF\AIR\debug` is looked up and missing, as expected: it only sets
  AIR's debug flag (`Adobe AIR.dll+0xACE730`, from `GetModuleFileNameW(NULL)`).

Static analysis of `Adobe AIR.dll` (x64):
- The descriptor paths live in an ActionScript ABC constant pool in `.rdata`
  (`0xE4C5EE`–`0xE4C6F0`), next to the classes `RuntimeInstallerEntry`,
  `AppInstallEntry`, `AppEntry`, `ExtendedAppEntry`. The check that decides
  "no descriptor" most likely runs as bytecode in AIR's AVM, calling native
  file methods; it is not a plain x86 branch.
- Native file helper that fits the symptom: `+0xAB0C00` opens with
  `FILE_FLAG_BACKUP_SEMANTICS`, then `+0xAB0DC0` requires `GetFileType` =
  `FILE_TYPE_DISK` and two calls through `+0x7B3650` (likely
  `GetFileInformationByHandle`) with a real size; on failure it resets its
  result and reports an error although the open worked.
- Whole-file reader `+0x45E45C` (CreateFileW + ReadFile loop) returns whatever
  it read with no error; used for `.ane` descriptors, representative of the idiom.

Next: log, for handles opened on `.xml` files, the results of `GetFileType`,
`GetFileInformationByHandle(Ex)` and every `ReadFile` byte count (not only the
failures), and the `GetModuleFileNameW` answers AIR receives.

## Build 327 (FILEWATCH=on, every call on .xml handles logged)
- `GetModuleFileNameW(NULL)` answers `...\LocalState\games\291550\Brawlhalla.exe`
  (the game root), and the working directory is the game root.
- `application.xml` is found and opened, and then no `ReadFile`, `GetFileSize(Ex)`,
  `GetFileType` or `GetFileInformationByHandle` is made on that handle; no file
  mapping fails; no C runtime open fails; the only stubs reached are USER32 ones.
- So the descriptor is read through a route that works and is not logged (the C
  runtime's `_read` on a descriptor from `_open_osfhandle`, or a mapping view), and
  rejected by a later check. Next: log `_open_osfhandle`/`_read`/`_fstat`, the first
  bytes AIR gets, and the AVM-side error path around `Adobe AIR.dll+0x2F3CC8`.
