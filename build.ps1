param([switch]$WithLocalWhisper)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'scripts/restore-dependencies.ps1') -WithLocalWhisper:$WithLocalWhisper
& (Join-Path $PSScriptRoot 'source/build.ps1')
