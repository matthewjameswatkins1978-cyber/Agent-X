# Beautiful Feed PR2 evidence capture: real window screenshots plus timings
# from the genuine running application, driven through UI Automation.
#
# Why PowerShell 5.1: System.Windows.Automation ships with .NET Framework and
# pwsh 7 has no UIA assembly without extra packages. Why PrintWindow: screen
# capture (Graphics.CopyFromScreen) is unavailable in this session, so each
# shot uses PrintWindow(PW_RENDERFULLCONTENT) on the real window handle - a
# genuine capture of the running app, not a rendered mock.
#
# The app runs with AGENTX_MANAGED_ROOT pointed at an isolated profile, so the
# real %LocalAppData%/AgentX profile (Matthew's sessions and preferences) is
# never touched. The transcript comes from a real Pi session that is already
# on disk and gets restored on Connect; this script never fabricates or types
# conversation content (input injection is unavailable in this session).
#
# Usage:
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Capture-FeedEvidence.ps1 `
#     -AppPath src\AgentX.App\bin\Release\net10.0\AgentX.App.exe `
#     -ManagedRoot ..\..\profile -OutDir docs\feed -Tag pr2
param(
    [Parameter(Mandatory = $true)][string]$AppPath,
    [Parameter(Mandatory = $true)][string]$ManagedRoot,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [Parameter(Mandatory = $true)][string]$Tag,
    [int]$Width = 1200,
    [int]$Height = 800,
    [int]$NarrowWidth = 720,
    [int]$TallHeight = 0,
    [string]$Sizes = '13,14,15,16',
    [string]$NarrowSizes = '13,16',
    [switch]$NoCopyCheck
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$AppPath = (Resolve-Path $AppPath).Path
$OutDir = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $repo $OutDir }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$ManagedRoot = (Resolve-Path $ManagedRoot).Path

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AxWin {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOMOVE = 0x0002;
}
'@

$env:AGENTX_MANAGED_ROOT = $ManagedRoot
$proc = Start-Process -FilePath $AppPath -PassThru
$sw = [Diagnostics.Stopwatch]::StartNew()
while ($sw.ElapsedMilliseconds -lt 20000) {
    Start-Sleep -Milliseconds 250
    $proc.Refresh()
    if ($proc.MainWindowHandle -ne 0) { break }
}
if ($proc.MainWindowHandle -eq 0) { throw 'no main window after 20s' }
$hwnd = $proc.MainWindowHandle

$evidence = [ordered]@{
    tag           = $Tag
    app           = $AppPath
    managedRoot   = $ManagedRoot
    started       = (Get-Date).ToString('o')
    pid           = $proc.Id
    windowHandle  = $hwnd
    captureMethod = 'PrintWindow PW_RENDERFULLCONTENT (screen CopyFromScreen unavailable in this session)'
}
$shots = @()

function Find-ById([string]$id) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($script:hwnd)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Wait-For([string]$what, [int]$timeoutSec, [scriptblock]$probe) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
        $r = & $probe
        if ($r) { return $r }
        Start-Sleep -Milliseconds 300
    }
    throw "timeout waiting for $what"
}

function Save-WindowShot([string]$name) {
    $rect = New-Object AxWin+RECT
    [AxWin]::GetWindowRect($script:hwnd, [ref]$rect) | Out-Null
    $w = $rect.Right - $rect.Left
    $ht = $rect.Bottom - $rect.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $ht)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [AxWin]::PrintWindow($script:hwnd, $hdc, 2) | Out-Null
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    $path = Join-Path $script:OutDir "$name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $script:shots += [ordered]@{ name = "$name.png"; w = $w; h = $ht }
    return $path
}

function Set-Nav([string]$id) {
    # The rail buttons are ToggleButtons: UIA exposes Toggle (not Invoke),
    # and toggling an unchecked destination switches the page (verified).
    $btn = Wait-For "nav $id" 10 { Find-ById $id }
    $toggle = $btn.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
        $toggle.Toggle()
    }
    Start-Sleep -Milliseconds 500
}

function Set-Feed([string]$appearance, [double]$size) {
    Set-Nav 'NavSettings'
    $measure = @()
    foreach ($pair in @(@('FeedCombo', $appearance), @('FeedSizeCombo', "$size"))) {
        $combo = Wait-For "combo $($pair[0])" 10 { Find-ById $pair[0] }
        $expand = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        try { $expand.Expand() } catch { }
        Start-Sleep -Milliseconds 300
        $want = [string]$pair[1]
        $nameCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $want)
        $typeCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::ListItem)
        $itemCond = New-Object System.Windows.Automation.AndCondition($nameCond, $typeCond)
        $localRoot = [System.Windows.Automation.AutomationElement]::FromHandle($script:hwnd)
        $item = $localRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $itemCond)
        if ($null -eq $item) {
            # Avalonia keeps dropdown items in a popup window: search the
            # desktop for our process's list item with that name instead.
            $pidCond = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $script:proc.Id)
            $both = New-Object System.Windows.Automation.AndCondition($itemCond, $pidCond)
            $item = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
                [System.Windows.Automation.TreeScope]::Descendants, $both)
        }
        if ($null -eq $item) { throw "combo option '$want' not found" }
        # Time only the selection itself: the UIA Select call runs the app's
        # SelectionChanged handler (ApplyFeedAppearance -> full list rebuild)
        # synchronously on the UI thread, so this is the switch cost. The
        # popup expand/find above is automation overhead and stays untimed.
        $t = [Diagnostics.Stopwatch]::StartNew()
        $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $t.Stop()
        try { $expand.Collapse() } catch { }
        Start-Sleep -Milliseconds 300
        $measure += [pscustomobject]@{
            control = $pair[0]; value = $want
            handlerMs = [math]::Round($t.Elapsed.TotalMilliseconds, 1)
        }
    }
    Set-Nav 'NavLive'
    Start-Sleep -Milliseconds 400
    return $measure
}

function Set-WindowSize([int]$w, [int]$h) {
    [AxWin]::SetWindowPos($script:hwnd, [IntPtr]::Zero, 0, 0, $w, $h,
        [AxWin]::SWP_NOZORDER -bor [AxWin]::SWP_NOMOVE) | Out-Null
    Start-Sleep -Milliseconds 600
}

# --- Connect and restore the real Pi transcript ---------------------------
[AxWin]::SetWindowPos($hwnd, [IntPtr]::Zero, 0, 0, $Width, $Height,
    [AxWin]::SWP_NOZORDER -bor [AxWin]::SWP_NOMOVE) | Out-Null
Start-Sleep -Milliseconds 500

$connectSw = [Diagnostics.Stopwatch]::StartNew()
$connect = Wait-For 'ConnectButton' 15 { Find-ById 'ConnectButton' }
$connect.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
$state = Wait-For 'connection state' 60 {
    $s = (Find-ById 'PiConnectionState')
    if ($s -and $s.Current.Name -eq 'Connected') { $s.Current.Name } else { $null }
}
$restore = Wait-For 'restored conversation rows' 60 {
    $list = Find-ById 'ConversationList'
    if ($null -eq $list) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $items = $list.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($items.Count -gt 0) { $items.Count } else { $null }
}
$connectSw.Stop()
$evidence.connectMs = [math]::Round($connectSw.Elapsed.TotalMilliseconds, 1)
$evidence.connectionState = $state
$evidence.restoredRows = $restore
$evidence.statusAfterConnect = (Find-ById 'StatusText').Current.Name

# --- Screenshots: Studio/Operator at the requested sizes ------------------
$sizeList = @($Sizes.Split(',') | ForEach-Object { [double]$_ })
$narrowList = @($NarrowSizes.Split(',') | Where-Object { $_ } | ForEach-Object { [double]$_ })
$switches = @()
foreach ($appearance in 'Studio', 'Operator') {
    foreach ($size in $sizeList) {
        $switches += Set-Feed $appearance $size
        Start-Sleep -Milliseconds 400
        Save-WindowShot ("$Tag-$($appearance.ToLowerInvariant())-$size") | Out-Null
    }
}

# --- Narrow (720px) window at the size extremes ---------------------------
foreach ($appearance in 'Studio', 'Operator') {
    foreach ($size in $narrowList) {
        $switches += Set-Feed $appearance $size
        Set-WindowSize $NarrowWidth $Height
        Save-WindowShot ("$Tag-$($appearance.ToLowerInvariant())-$size-$NarrowWidth") | Out-Null
        Set-WindowSize $Width $Height
    }
}
$evidence.switches = $switches
$evidence.switchHandlerMs = @($switches | ForEach-Object { $_.handlerMs })

# --- Tall viewport: real assistant corpus + tool rows ----------------------
# UIA ScrollPattern is a no-op on this virtualized ListBox and the huge first
# row fills an 800px window, so framing is done with window height instead:
# a tall window realizes the assistant corpus row and the two git tool rows.
if ($TallHeight -gt 0) {
    foreach ($appearance in 'Studio', 'Operator') {
        $switches += Set-Feed $appearance 14
        Set-WindowSize $Width $TallHeight
        Start-Sleep -Milliseconds 500
        Save-WindowShot ("$Tag-$($appearance.ToLowerInvariant())-14-tall") | Out-Null
        Set-WindowSize $Width $Height
    }
}

# --- Settings page shot ----------------------------------------------------
Set-Nav 'NavSettings'
Start-Sleep -Milliseconds 300
Save-WindowShot "$Tag-settings" | Out-Null
Set-Nav 'NavLive'

# --- Copy check: clipboard must equal a transcript message byte-for-byte ----
if (-not $NoCopyCheck) {
    $sessionFile = Get-ChildItem (Join-Path $ManagedRoot 'sessions') -Filter *.jsonl |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $expected = @()
    foreach ($line in Get-Content $sessionFile.FullName) {
        try { $o = $line | ConvertFrom-Json } catch { continue }
        if ($o.type -ne 'message' -or $o.message.role -notin @('user', 'assistant')) { continue }
        $c = $o.message.content
        if ($c -is [string]) { $expected += $c }
        elseif ($c) { foreach ($part in $c) { if ($part.text) { $expected += $part.text } } }
    }
    $list = Find-ById 'ConversationList'
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $items = $list.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    $copyResult = $null
    $copySw = [Diagnostics.Stopwatch]::StartNew()
    for ($i = 0; $i -lt $items.Count -and $null -eq $copyResult; $i++) {
        $sel = $items[$i].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $sel.Select()
        Start-Sleep -Milliseconds 250
        $copy = Find-ById 'CopySelectedButton'
        $copy.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 400
        $clip = [System.Windows.Forms.Clipboard]::GetText()
        foreach ($candidate in $expected) {
            if ($clip -ceq $candidate) {
                $copyResult = [ordered]@{
                    matched = $true
                    listItemIndex = $i
                    chars = $clip.Length
                    byteExact = $true
                    normalizedNewlines = $false
                    statusText = (Find-ById 'StatusText').Current.Name
                }
                break
            }
            # The Windows clipboard stores CRLF; if that is the only
            # difference, record it explicitly instead of calling it exact.
            if (($clip -replace "`r`n", "`n") -ceq ($candidate -replace "`r`n", "`n")) {
                $copyResult = [ordered]@{
                    matched = $true
                    listItemIndex = $i
                    chars = $clip.Length
                    byteExact = $false
                    normalizedNewlines = $true
                    statusText = (Find-ById 'StatusText').Current.Name
                }
                break
            }
        }
    }
    $copySw.Stop()
    if ($null -eq $copyResult) {
        $copyResult = [ordered]@{ matched = $false; statusText = (Find-ById 'StatusText').Current.Name }
        # Diagnostics for review: keep the exact bytes that were compared.
        $diagDir = [System.IO.Path]::GetTempPath()
        [System.IO.File]::WriteAllText((Join-Path $diagDir "$Tag-clipboard.txt"), ([string]$clip))
        [System.IO.File]::WriteAllText((Join-Path $diagDir "$Tag-expected.txt"), ($expected -join "`n<<<MESSAGE>>>`n"))
        $copyResult.diagnostics = @((Join-Path $diagDir "$Tag-clipboard.txt"), (Join-Path $diagDir "$Tag-expected.txt"))
        $copyResult.expectedCount = $expected.Count
        $copyResult.clipboardChars = $clip.Length
    }
    $copyResult.elapsedMs = [math]::Round($copySw.Elapsed.TotalMilliseconds, 1)
    $evidence.copyCheck = $copyResult
    Save-WindowShot "$Tag-copy-footer" | Out-Null
}

$evidence.shots = $shots
$evidence.finished = (Get-Date).ToString('o')
$evidencePath = Join-Path $OutDir "$Tag-evidence.json"
$evidence | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 $evidencePath
Write-Host "evidence: $evidencePath"
$shots | ForEach-Object { Write-Host ("shot {0} {1}x{2}" -f $_.name, $_.w, $_.h) }

$proc.CloseMainWindow() | Out-Null
if (-not $proc.WaitForExit(5000)) { $proc.Kill() }
Write-Host 'app closed'
