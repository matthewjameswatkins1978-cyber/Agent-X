<#
.SYNOPSIS
  Provision an ISOLATED copy of a specific Pi version for compatibility
  testing. Never touches the global Pi install, PATH, or any config.

.DESCRIPTION
  npm-installs @earendil-works/pi-coding-agent at the requested version into
  a private prefix (<Destination>\<version>\node_modules). The isolated
  pi.cmd shim lives at <Destination>\<version>\node_modules\.bin\pi.cmd.
  Point the compatibility contract at it with:

    $env:AX_PI_UNDER_TEST_EXES = "<Destination>\<version>\node_modules\.bin\pi.cmd"

  Rollback: delete <Destination>\<version>. The globally installed Pi and
  every running worker are never affected.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Provision-PiUnderTest.ps1 -Version 1.2.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Destination = (Join-Path $env:TEMP "ax-pi-under-test")
)

$ErrorActionPreference = "Stop"
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must look like 1.2.0 (got '$Version')."
}

$prefix = Join-Path $Destination $Version
New-Item -ItemType Directory $prefix -Force | Out-Null
Write-Host "Provisioning @earendil-works/pi-coding-agent@$Version into $prefix (isolated; global Pi untouched)..."
npm install --prefix $prefix "@earendil-works/pi-coding-agent@$Version"
if ($LASTEXITCODE -ne 0) { throw "npm install failed." }

$shim = Join-Path $prefix "node_modules\.bin\pi.cmd"
if (-not (Test-Path -LiteralPath $shim)) { throw "Expected shim missing: $shim" }
$reported = (& $shim --version 2>&1 | Select-Object -First 1)
Write-Host "Provisioned. $shim reports version: $reported"
Write-Host "Use: `$env:AX_PI_UNDER_TEST_EXES = '$shim'"
