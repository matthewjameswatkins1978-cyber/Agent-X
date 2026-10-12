<#
.SYNOPSIS
  Reproducible Windows x64 portable distribution for Agent X (RC1 D1 groundwork).

.DESCRIPTION
  Publishes src/AgentX.App self-contained for win-x64, stamps provenance,
  assembles a portable directory, zips it, and writes a SHA-256 manifest.
  Verifies the archive by re-extracting and re-hashing. No installers,
  no signing, no uploads, no changes to Pi/Node/credentials/sessions.

  Outputs (default artifacts/dist):
    AgentX-<stamp>-win-x64/        portable directory
    AgentX-<stamp>-win-x64.zip      portable archive
    SHA256SUMS.txt                 sha256 of the zip

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/New-PortableDist.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Rid = "win-x64",
    [string]$OutDir = ""
)

$ErrorActionPreference = "Stop"
function Write-Utf8NoBom([string]$Path, [string]$Text) {
    [System.IO.File]::WriteAllText($Path, $Text, [System.Text.UTF8Encoding]::new($false))
}
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $repo
if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path (Join-Path $repo "artifacts") "dist"
}

$commit = (git rev-parse HEAD).Trim()
$short = (git rev-parse --short HEAD).Trim()
$base = (git merge-base HEAD origin/bootstrap/agent-x-foundation 2>$null)
if (-not $base) { $base = $commit }
$base = $base.Trim()
$dirty = (git status --porcelain)
if ($dirty) { throw "Working tree is dirty -- commit or stash before packaging. Refusing to stamp a dirty build." }
$stamp = "rc1-d1-" + (Get-Date -Format "yyyyMMdd") + "-" + $short
$portableDir = Join-Path $OutDir "AgentX-$stamp-win-x64"
$zipPath = "$portableDir.zip"
$manifestPath = Join-Path $OutDir "SHA256SUMS.txt"

Write-Host "Packaging Agent X $stamp from $commit (base $base)"

# 1. Clean, reproducible publish: self-contained, framework bundled,
#    NOT single-file (transparent contents, faster cold start), NOT trimmed
#    (Avalonia reflection safety), deterministic file versions from source.
$publishDir = Join-Path $portableDir "app"
if (Test-Path $portableDir) { Remove-Item -Recurse -Force $portableDir }
New-Item -ItemType Directory $publishDir | Out-Null
dotnet publish src/AgentX.App/AgentX.App.csproj -c $Configuration -r $Rid `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishTrimmed=false `
    -p:IncludeNativeLibrariesForSelfExtract=false `
    -p:Version="0.1.0" `
    -p:InformationalVersion="$stamp+$short" `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# 2. Provenance + notices (static sources live in packaging/).
Copy-Item (Join-Path (Join-Path $repo "packaging") "NOTICE.txt") (Join-Path $portableDir "NOTICE.txt")
Copy-Item (Join-Path (Join-Path $repo "packaging") "THIRD-PARTY-NOTICES.txt") (Join-Path $portableDir "THIRD-PARTY-NOTICES.txt")
Copy-Item (Join-Path (Join-Path $repo "packaging") "README-portable.txt") (Join-Path $portableDir "README.txt")

$sdk = (dotnet --version).Trim()
$nodeVer = ""
try { $nodeVer = (node --version 2>$null).Trim() } catch { $nodeVer = "(node not on PATH)" }
$piVer = ""
try { $piVer = (pi --version 2>$null | Select-Object -First 1).Trim() } catch { $piVer = "(pi not on PATH)" }
$versionText = @"
Agent X portable distribution
Stamp:            $stamp
Commit:           $commit
Base branch head: $base
Built:            $(Get-Date -Format "yyyy-MM-ddTHH:mm:ssK")
Build machine:    $([System.Environment]::MachineName) ($([System.Environment]::OSVersion.VersionString))
.NET SDK:         $sdk
Target:           net10.0 $Rid self-contained (PublishSingleFile=false, PublishTrimmed=false)
Avalonia:         12.1.4 (see THIRD-PARTY-NOTICES.txt)
Build-time node:  $nodeVer   (NOT bundled -- external prerequisite, see README.txt)
Build-time pi:    $piVer   (NOT bundled -- external prerequisite, see README.txt)
Reproduce:        scripts/New-PortableDist.ps1 from commit $commit
"@
Write-Utf8NoBom (Join-Path $portableDir "version.txt") ($versionText + "`r`n")

# 3. Archive + manifest.
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
Compress-Archive -Path $portableDir -DestinationPath $zipPath -CompressionLevel Optimal
$zipHash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$zipName = Split-Path $zipPath -Leaf
Write-Utf8NoBom $manifestPath "$zipHash  $zipName"

# 4. Verify: re-extract to temp, confirm the exe + notices exist, re-hash the zip.
$verifyDir = Join-Path ([System.IO.Path]::GetTempPath()) ("ax-dist-verify-" + [Guid]::NewGuid().ToString("N"))
Expand-Archive -Path $zipPath -DestinationPath $verifyDir
$extracted = Get-ChildItem $verifyDir -Recurse -File
$exe = $extracted | Where-Object { $_.Name -eq "AgentX.App.exe" }
if (-not $exe) { throw "Verification failed: AgentX.App.exe missing from archive." }
foreach ($required in @("NOTICE.txt", "THIRD-PARTY-NOTICES.txt", "README.txt", "version.txt")) {
    if (-not ($extracted | Where-Object { $_.Name -eq $required })) { throw "Verification failed: $required missing from archive." }
}
$reHash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($reHash -ne $zipHash) { throw "Verification failed: zip hash changed after extraction." }
$fileCount = $extracted.Count
$totalBytes = ($extracted | Measure-Object Length -Sum).Sum
Remove-Item -Recurse -Force $verifyDir

Write-Host "OK: $zipName"
Write-Host "  files=$fileCount extractedBytes=$totalBytes zipBytes=$((Get-Item $zipPath).Length) sha256=$zipHash"
Write-Host "  manifest=$manifestPath"
