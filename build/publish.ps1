<#
.SYNOPSIS
  Publishes VU Studio into a staging folder ready for installer\VuStudio.iss.
.DESCRIPTION
  OutputDir\stage        Server (win-x64) + Tray (win-x64): the Inno Setup /DStageDir
  OutputDir\agent\<rid>  Agent single-file binaries (linux-x64, linux-arm64, win-x64)
.PARAMETER Version
  Product version, for example 0.2.0 or 0.2.0-rc.1.
.PARAMETER OutputDir
  Output folder. Default: <repo>\artifacts
.PARAMETER SkipWeb
  Do not build the web UI (wwwroot must already exist).
.PARAMETER SkipAgent
  Do not publish the Agent.
#>
[CmdletBinding()]
param(
    [string]$Version = '0.0.0-dev',
    [string]$OutputDir = '',
    [switch]$SkipWeb,
    [switch]$SkipAgent
)

$ErrorActionPreference = 'Stop'

$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutputDir) { $OutputDir = Join-Path $Root 'artifacts' }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$Out = (Resolve-Path $OutputDir).Path

$env:DOTNET_NOLOGO = 'true'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = 'true'

$Common = @('-c', 'Release', "-p:Version=$Version", '-p:ContinuousIntegrationBuild=true', '-nologo', '-v', 'minimal')

# Runs a native command and fails on a non-zero exit code.
function Invoke-Native {
    param([string]$File, [string[]]$Arguments)
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File failed with exit code $LASTEXITCODE" }
}

$Stage = Join-Path $Out 'stage'
$Obj = Join-Path $Out 'obj'
foreach ($p in @($Stage, (Join-Path $Out 'agent'), $Obj)) {
    if (Test-Path $p) { Remove-Item -Recurse -Force $p }
}
New-Item -ItemType Directory -Force -Path $Stage, $Obj | Out-Null

Write-Host "== VU Studio $Version -> $Out"

if (-not $SkipWeb) {
    Write-Host '== Web UI'
    Push-Location (Join-Path $Root 'web')
    try {
        Invoke-Native 'npm' @('ci')
        Invoke-Native 'npm' @('run', 'build')
    } finally { Pop-Location }
} else {
    Write-Host '== Web UI skipped'
}
if (-not (Test-Path (Join-Path $Root 'src\VuStudio.Server\wwwroot\index.html'))) {
    throw 'src\VuStudio.Server\wwwroot\index.html is missing (web UI not built).'
}

Write-Host '== Server (win-x64)'
Invoke-Native 'dotnet' (@('publish', (Join-Path $Root 'src\VuStudio.Server\VuStudio.Server.csproj')) + $Common +
    @('-r', 'win-x64', '--self-contained', 'true', '-p:IsTransformWebConfigDisabled=true', '-o', (Join-Path $Obj 'server')))

Write-Host '== Tray (win-x64)'
Invoke-Native 'dotnet' (@('publish', (Join-Path $Root 'src\VuStudio.Tray\VuStudio.Tray.csproj')) + $Common +
    @('-r', 'win-x64', '--self-contained', 'true', '-o', (Join-Path $Obj 'tray')))

# Server first, Tray second: same-named files differ (System.Drawing.dll and others)
# and the Tray's copies must win.
Write-Host "== Staging -> $Stage"
Copy-Item -Path (Join-Path $Obj 'server\*') -Destination $Stage -Recurse -Force
Copy-Item -Path (Join-Path $Obj 'tray\*') -Destination $Stage -Recurse -Force

if ((Test-Path (Join-Path $Root 'src\VuStudio.Agent')) -and -not $SkipAgent) {
    foreach ($rid in @('linux-x64', 'linux-arm64', 'win-x64')) {
        Write-Host "== Agent ($rid)"
        Invoke-Native 'dotnet' (@('publish', (Join-Path $Root 'src\VuStudio.Agent\VuStudio.Agent.csproj')) + $Common +
            @('-r', $rid, '--self-contained', 'true', '-p:PublishSingleFile=true',
              '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=none',
              '-o', (Join-Path $Out "agent\$rid")))
    }
}

Write-Host '== Done'
Write-Host "Stage: $Stage"
