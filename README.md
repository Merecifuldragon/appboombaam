# BFCrewSync

Lightweight WPF (.NET 8, `net8.0-windows`) companion app for the crew-tools
sync system: system optimizer, IST precision clock, and the JSON/legacy
sync-file writer the registered Roblox routines poll for.

## Project layout

```
BFCrewSync/
├─ BFCrewSync.csproj        # net8.0-windows, WPF, trimmed self-contained publish
├─ app.manifest              # asInvoker (no UAC prompt), per-monitor DPI aware
├─ App.xaml / App.xaml.cs    # app resources/theme, global exception handler
├─ MainWindow.xaml / .cs     # UI + timers + button handlers
├─ Models/
│  └─ SyncPayload.cs         # JSON schema (v4) + source-generated (de)serializer
└─ Services/
   ├─ NativeMethods.cs           # Win32 P/Invoke (working set, priority, mem status)
   ├─ MemoryOptimizerService.cs  # SetProcessWorkingSetSize / EmptyWorkingSet, priority
   ├─ PerformanceMonitorService.cs # CPU%/RAM% sampling for the overlay
   ├─ IstClockService.cs         # UTC+5:30 clock, target/epoch resolution, countdown
   └─ SyncFileService.cs         # atomic tmp→move writes, mirrors, syncId, legacy fallback
```

## Why it stays under ~30 MB idle

- **Workstation, non-concurrent GC** (`ServerGarbageCollection=false`,
  `ConcurrentGarbageCollection=false`) — server GC alone can add tens of MB
  of per-core heap segments that a single-window utility doesn't need.
- **`SustainedLowLatency` GC mode** at startup + a background timer calling
  `SetProcessWorkingSetSize(-1,-1)` / `EmptyWorkingSet` on itself every 2
  minutes, so pages the GC no longer needs actually get handed back to the
  OS instead of sitting in the working set.
- **Trimmed, single-file, self-contained publish** (`PublishTrimmed`,
  `TrimMode=link`) strips unused framework code from the shipped binary.
- **`InvariantGlobalization`** skips loading full ICU globalization data,
  which is one of the larger fixed costs for a small WPF app.
- No background WMI queries — CPU% comes from one `PerformanceCounter`
  instance polled once a second; RAM% comes from a single
  `GlobalMemoryStatusEx` call, both essentially free.

Expect idle RSS in the low-20s MB right after the passive trim runs; exact
numbers vary by Windows build and .NET servicing version.

## Getting a ready-to-run .exe with zero local setup (GitHub Actions)

This repo includes `.github/workflows/build.yml`, which builds the
self-contained `BFCrewSync.exe` on a hosted Windows runner in the cloud —
you don't need .NET, NuGet, or even Windows on your own machine.

1. Create a new (can be private) GitHub repo and push this folder's
   contents to it:
   ```bash
   git init
   git add .
   git commit -m "BFCrewSync"
   git branch -M main
   git remote add origin https://github.com/<you>/BFCrewSync.git
   git push -u origin main
   ```
2. On GitHub, open the **Actions** tab — the workflow runs automatically
   on push (takes 2-4 minutes). If it doesn't auto-start, click
   **"Build BFCrewSync.exe" → "Run workflow"**.
3. When it finishes, open the completed run and download the
   **`BFCrewSync-win-x64`** artifact (a zip containing `BFCrewSync.exe`).
   Unzip it and run — no installer, no .NET runtime needed, it's fully
   self-contained.

**Permanent download link instead of an artifact zip:** tag a commit
(`git tag v1.0 && git push origin v1.0`) and the same workflow attaches
`BFCrewSync.exe` directly to a GitHub Release, so you get a stable URL you
can bookmark or share.

Artifacts from a normal push expire after 90 days (GitHub's default) —
use the tag/Release route if you want it to stick around indefinitely.

## Building locally instead

Prerequisites: .NET 8 SDK, Windows (WPF only builds on Windows).

```powershell
# Debug run
dotnet run

# Framework-dependent build
dotnet build -c Release

# Standalone single-file release EXE (recommended distribution form)
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:PublishTrimmed=true
```

The published `BFCrewSync.exe` lands in
`bin\Release\net8.0-windows\win-x64\publish\`. It's self-contained, so it
runs on a machine without the .NET runtime installed.

## Sync file contract (unchanged from the existing Lua-side reader)

All files are written atomically: content goes to a `.tmp` file first, then
`File.Move(..., overwrite: true)` swaps it into place, so a poller never
observes a half-written JSON file.

| Action     | Files written                                                         |
|------------|------------------------------------------------------------------------|
| SET SYNC   | `bf_crew_sync.tmp` → `bf_crew_sync.txt`, mirrored to `bf_crew_sync_mirror.txt` (`action: "START"`) |
| REGISTER   | `bf_crew_register.tmp` → `bf_crew_register.txt`, mirrored to `bf_crew_register_mirror.txt` |
| CANCEL     | `bf_crew_sync.txt` overwritten with `action: "CANCEL"` (same atomic path) |

`syncId` = `<unix-ms>-<6-digit-random>`, e.g. `1735599123456-041207`.

When **"Also write legacy string format"** is checked, a companion
`bf_crew_sync_legacy.txt` / `bf_crew_register_legacy.txt` is written with
the plain `<crewId>@@<targetEpoch>` string, alongside the JSON — for any
older reader that hasn't moved to the v4 schema yet.

## Crew ID scan (request/response round trip)

Clicking **"Scan Crew ID"** doesn't scan anything itself — it asks the
in-game executor script to do the scan (using whatever crew-detection logic
your Lua-side "Crew Tools" script already has) and listens for the answer.

1. App writes `bf_crew_scan_request.tmp` → atomic move →
   `bf_crew_scan_request.txt` (+ mirror), containing:
   ```json
   { "version": 4, "action": "SCAN_CREW_ID", "requestId": "<ms>-<rand6>", "requestedAt": <unix-seconds> }
   ```
2. Your executor-side listener watches for a `requestId` it hasn't handled
   yet, runs its existing crew-scan logic, then atomic-writes
   `bf_crew_scan_result.txt`:
   ```json
   { "version": 4, "requestId": "<same id echoed back>", "respondedAt": <unix-seconds>,
     "found": true, "crewId": "3569141797", "crewOwner": "SomePlayer" }
   ```
   (`found: false` with `crewId`/`crewOwner` omitted if nothing was detected.)
3. `ScanListenerService` on the app side watches `bf_crew_scan_result.txt`
   with a `FileSystemWatcher` (event-driven, not a polling loop) and only
   accepts a result whose `requestId` matches the one it just sent — so a
   leftover result file from a previous session, or a slow duplicate
   response, is ignored rather than overwriting the fields.
4. On a match, `Crew ID` (and `Crew owner`, if present) auto-fill in the UI.
   If nothing arrives within 10 seconds, the button resets and the status
   line says so — most likely means the listener isn't running in-game.

This is the same request → file → response pattern your other scripts
already use for file-based IPC; the only new part is the `requestId`
round trip so the app can tell a fresh answer from a stale one.

## Target time resolution

- Enter a wall-clock `HH:mm:ss` IST time — if that time has already passed
  today, it resolves to tomorrow.
- Or enter a whole number of minutes in **"+N minutes"** — this takes
  priority over the wall-clock field if both are filled in.

Both resolve to a true UTC Unix epoch (`targetEpoch`), so the countdown and
the written payload are correct regardless of client clock skew, as long as
the machine's system clock itself is accurate.

## Notes / things you may want to adjust

- The optimizer's "lower background priority" targets
  `RobloxPlayerBeta.exe` by name — if you multi-box via a launcher that
  renames the process, update `targetProcessName` in
  `MainWindow.xaml.cs` → `OptimizeNow_Click`.
- No admin elevation is requested (`app.manifest` → `asInvoker`); trimming
  and priority changes on other processes only need same-user access
  rights, which Windows grants without UAC as long as BFCrewSync and the
  Roblox clients run under the same account.
- The precision trigger at `00:00:00.000` only logs the moment locally —
  the actual "Join Crew" action happens in the registered Roblox routines,
  which are already polling `bf_crew_sync.txt` for this `syncId`/`targetEpoch`.
  If you want BFCrewSync itself to do something else at zero (play a sound,
  hit a webhook), that's a good place to hook in inside
  `FireExecutionTrigger()`.
