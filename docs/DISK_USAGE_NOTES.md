# Disk Usage Troubleshooting — "100% disk" during dev

**Purpose:** a reusable reference for when the PC hits 100% disk again. First recorded 2026-07-18/19
during the autonomous dedup arc, when repeated CLI builds pushed the machine to 100% disk.

---

## ⚠️ First: is it SPACE or ACTIVITY? (almost always ACTIVITY)

Windows Task Manager's **Disk = 100%** is **active time (I/O busy), NOT the drive being full.**
When this was first hit, `C:` had **260 GB free (73% used)** — space was a non-issue. Confirm which:

```powershell
# Free space (if this shows plenty free, it's an I/O problem, not space)
Get-PSDrive C | Select-Object Used,Free
# or in Git Bash:  df -h /c

# Live disk ACTIVE time (the Task-Manager "100%" number)
(Get-Counter '\PhysicalDisk(_Total)\% Disk Time' -SampleInterval 1 -MaxSamples 3).CounterSamples |
  Select-Object -Last 1 | ForEach-Object { "Disk active: {0}%" -f [math]::Round($_.CookedValue) }

# Who is running / eating memory+CPU (I/O culprits)
Get-Process | Where-Object { $_.Name -match 'MsMpEng|ServiceHub|MSBuild|devenv|dotnet|docker|com\.docker|vmmem|wsl|testhost|vbcscompiler' } |
  Sort-Object WorkingSet64 -Descending |
  Select-Object Name, Id, @{N='Mem(MB)';E={[math]::Round($_.WorkingSet64/1MB)}}, @{N='CPU(s)';E={[math]::Round($_.CPU)}} |
  Format-Table -AutoSize
```

## The three culprits identified (with rough split)

1. **Windows Defender (`MsMpEng`) — ~50%.** Real-time-scans every file `dotnet` writes into `bin`/`obj`
   on every build → turns each build into a disk storm. **Biggest single lever.**
2. **Visual Studio (`devenv` + `ServiceHub.RoslynCodeAnalysisService`) — ~50%.** Background design-time /
   IntelliSense builds + Roslyn analysis of the MAUI solution, re-fired whenever files change (incl. when
   Claude edits). **NOTE: Claude Code runs *inside* VS here — closing VS disconnects it, so VS can't be
   closed; it must be *quieted* instead.**
3. **Docker Desktop (`com.docker.backend` + WSL2 `vmmem`/`ext4.vhdx`).** Its virtual disk churns even when
   idle. Only used for soaks — safe to quit when not soaking.
4. **(self-inflicted) Over-building** — running full client+server builds + a separate test run per change.

---

## Fixes / levers (in priority order)

### 1. Windows Defender exclusion — do first, biggest win
Open **PowerShell as Administrator** (Win+X → Terminal (Admin)):
```powershell
Add-MpPreference -ExclusionPath "C:\Users\kjden\source\repos\Kieshdh\KieshStockExchange"
Add-MpPreference -ExclusionPath "C:\Users\kjden\.nuget\packages"
Add-MpPreference -ExclusionProcess "dotnet.exe","MSBuild.exe","VBCSCompiler.exe"
# verify:
Get-MpPreference | Select-Object -ExpandProperty ExclusionPath
# undo later if wanted:
# Remove-MpPreference -ExclusionPath "C:\Users\kjden\source\repos\Kieshdh\KieshStockExchange"
```
Standard, Microsoft-recommended dev tradeoff: real-time scanning is disabled *inside that repo folder only*.
(Claude Code's safety classifier BLOCKS running these from the agent — they must be run by Kiesh manually.)

### 2. Quiet Visual Studio (can't close it — Claude runs in it)
- **Tools → Options → Text Editor → C# → Advanced → "Run background code analysis for" → Current document**
  (was "Entire solution"). Biggest VS-side reducer.
- **Unload projects you're not actively viewing** (Solution Explorer → right-click project → Unload Project),
  especially the MAUI client `KieshStockExchange`. This does NOT affect CLI builds (they build from the
  `.csproj` directly); VS just stops running design-time builds for it.
- Optional: disable **CodeLens** (Tools → Options → Text Editor → All Languages → CodeLens).

### 3. Docker
Quit Docker Desktop from the tray. To release its virtual disk immediately (normal PowerShell, no admin):
```powershell
wsl --shutdown
```

### 4. Claude / build-side frugal gating (applied automatically — see memory `feedback_disk_frugal_gating`)
- Gate via **`dotnet test` alone** for server/shared/test changes (it builds the deps); add a **client**
  build only when a candidate touches client-only code.
- **Never `dotnet clean`** (forces a full rebuild = max I/O); rely on incremental.
- Never run a build **concurrently** with an executor/review agent; **batch** candidates; keep cadence low.

### 5. Dynamic disk gate — "slow down above a limit to prevent spikes" (Kiesh's request 2026-07-19)
Before every build/test, pre-flight the disk; wait if it's already busy; then build at **Idle priority +
single-threaded** so the build can never spike. Reference implementation:
```powershell
param([int]$Limit = 70, [int]$MaxWaitSec = 300)  # Limit = % disk active time
function Get-DiskActive {
  ((Get-Counter '\PhysicalDisk(_Total)\% Disk Time' -SampleInterval 1 -MaxSamples 3).CounterSamples |
    Measure-Object CookedValue -Average).Average
}
$waited = 0
while ((Get-DiskActive) -ge $Limit -and $waited -lt $MaxWaitSec) { Start-Sleep 5; $waited += 8 }
if ((Get-DiskActive) -ge $Limit) { Write-Error "Disk still >= $Limit% after ${MaxWaitSec}s — not building."; exit 2 }
# Idle CPU priority (yields disk to foreground) + single-threaded (no parallel write burst):
$p = Start-Process dotnet -ArgumentList 'build','KieshStockExchange.Server/KieshStockExchange.Server.csproj','-maxcpucount:1' -PassThru -NoNewWindow
$p.PriorityClass = 'Idle'
$p.WaitForExit()
exit $p.ExitCode
```
Default limit **70%** (retune to taste). If the disk never clears within the cap, it PAUSES instead of forcing a build.

### 6. No built-in hard cap
Windows has **no per-process disk I/O cap** for normal apps (Job Objects / Storage QoS are container-only,
not practical for VS/Defender). The dynamic gate (#5) is the practical equivalent. A hard enforced cap needs
third-party software (e.g. **Process Lasso** I/O throttling) — optional, install only if wanted.

---

## Conversation summary (2026-07-18/19)

- During the autonomous dedup arc, repeated CLI `dotnet build`/`dotnet test` runs (to gate each dedup
  candidate) pushed the PC to 100% disk. Kiesh asked to limit disk usage.
- Diagnosis: **NOT space** (260 GB free) — it's **disk I/O activity**. Culprits: Defender (~50%) + VS
  Roslyn/design-time builds (~50%) + Docker Desktop's WSL2 disk, amplified by over-building.
- Actions taken: **Docker Desktop quit** (confirmed — only an idle `wslservice` stub remained); arc **fully
  paused**, both auto-resume cron timers cancelled; 3 fully-validated textual-identity dedups held
  **uncommitted** in the tree (build-green, 661/661, adversarial-review PRESERVED ×3) pending Kiesh's "go".
- Constraint discovered: **VS can't be closed** — Claude Code runs inside it. So VS must be *quieted*, not closed.
- Agreed ongoing mitigations: Defender exclusion (Kiesh runs, admin) + VS background-analysis→Current document
  + unload MAUI client + Claude-side frugal gating + the dynamic disk gate (#5).
- Related memory notes: `feedback_disk_frugal_gating`, `project_autonomous_resume_timer` (pause state).
