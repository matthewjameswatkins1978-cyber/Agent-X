#requires -Version 7.0
<#
.SYNOPSIS
  Safe Windows GUI smoke test for Agent X: launch, observe, clean up.

.DESCRIPTION
  Builds the solution, starts the built AgentX.App.exe directly (never via
  `dotnet run`, so the recorded PID *is* the application), waits for its
  main window, lets it run, then terminates ONLY that recorded process.

  Ownership rule (hard): cleanup addresses the launched process by Id AND
  verifies Name + StartTime before signalling, so PID reuse can never
  redirect the kill. The script NEVER enumerates processes by name to kill
  them. Other Agent X instances, Pi sessions and workers are left alone.

  Background: a 2026-10-17 ad-hoc smoke test cleaned up with
  `Get-Process -Name "AgentX.App" | Stop-Process -Force` and terminated a
  pre-existing Agent X window alongside the test instance. An external kill
  leaves no Application-Error fault in the event log, which is how the two
  were told apart. This script exists so that mistake cannot recur.

  Exit codes: 0 healthy smoke (window appeared, stayed up, owned cleanup
  done) · 1 build/start/window failure or the app exited non-zero on its own.
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Debug",
    [int]$TimeoutSeconds = 25,
    [int]$WindowGraceSeconds = 15,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Stop-OwnedProcess {
    param([int]$Id, [datetime]$Started, [string]$Name)
    $live = Get-Process -Id $Id -ErrorAction SilentlyContinue
    if ($null -eq $live) { Write-Host "cleanup: owned PID $Id already exited."; return $true }
    if ($live.Name -ne $Name -or $live.StartTime -ne $Started) {
        Write-Warning "cleanup: PID $Id no longer matches owned process (name/start mismatch) — touching nothing."
        return $false
    }
    Write-Host "cleanup: stopping owned PID $Id [$Name started $Started]..."
    $live.CloseMainWindow() | Out-Null
    if (-not $live.WaitForExit(5000)) {
        Write-Host "cleanup: close request ignored; forcing owned PID $Id..."
        Stop-Process -Id $Id -Force
        $live.WaitForExit(5000) | Out-Null
    }
    $gone = $null -eq (Get-Process -Id $Id -ErrorAction SilentlyContinue)
    if (-not $gone) { Write-Warning "cleanup: owned PID $Id still present." }
    return $gone
}

if (-not $NoBuild) {
    Write-Host "smoke: building AgentX.sln ($Configuration)..."
    dotnet build AgentX.sln --configuration $Configuration -p:TreatWarningsAsErrors=true
    if ($LASTEXITCODE -ne 0) { throw "build failed." }
}

$exe = Join-Path $root "src/AgentX.App/bin/$Configuration/net10.0/AgentX.App.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "built exe not found: $exe" }

Write-Host "smoke: launching $exe ..."
$proc = Start-Process -FilePath $exe -WorkingDirectory $root -PassThru
$ownedId = $proc.Id
$ownedStart = $proc.StartTime
Write-Host "smoke: owned PID $ownedId (started $ownedStart). Nothing else will be signalled."

$deadline = (Get-Date).AddSeconds($WindowGraceSeconds)
$sawWindow = $false
while ((Get-Date) -lt $deadline) {
    $proc.Refresh()
    if ($proc.HasExited) { break }
    if ($proc.MainWindowHandle -ne 0) { $sawWindow = $true; break }
    Start-Sleep -Milliseconds 500
}
$proc.Refresh()
if ($proc.HasExited) {
    Write-Warning "smoke: app exited on its own with code $($proc.ExitCode) before the window appeared."
    Write-Host "smoke: if this was a crash, see Application log (.NET Runtime / Application Error). Left all other processes alone."
    exit 1
}
if (-not $sawWindow) { throw "smoke: no main window within ${WindowGraceSeconds}s." }
Write-Host "smoke: main window '$($proc.MainWindowTitle)' up on owned PID $ownedId; observing ${TimeoutSeconds}s..."

$end = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $end) {
    $proc.Refresh()
    if ($proc.HasExited) { break }
    Start-Sleep -Seconds 2
}
$proc.Refresh()
if ($proc.HasExited -and $proc.ExitCode -ne 0) {
    Write-Warning "smoke: app exited mid-observation with code $($proc.ExitCode); check the Application event log for a fault. Cleaning up owned PID only."
}

$others = @(Get-Process -Name "AgentX.App" -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $ownedId })
Write-Host "smoke: unrelated AgentX.App instances observed (left alone): $($others.Count)"

$clean = Stop-OwnedProcess -Id $ownedId -Started $ownedStart -Name "AgentX.App"
if (-not $clean) { throw "smoke: owned cleanup failed." }
Write-Host "smoke: healthy — window appeared, process stayed up, only the owned PID was stopped."
exit 0
