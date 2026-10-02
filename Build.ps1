param([string]$Version = '0.4.0-rc.1', [string]$Python = 'python', [switch]$Check)
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$cache = Join-Path $env:LOCALAPPDATA 'QFTPlus\build'
& $Python -B (Join-Path $repo 'private\dev\check-source.py')
if ($LASTEXITCODE) { throw 'Source checks failed.' }
& $Python -B (Join-Path $repo 'private\dev\prepare-assets.py') --output $cache
if ($LASTEXITCODE) { throw 'Preparing release components failed.' }
& (Join-Path $repo 'private\dev\setup-runtime.ps1') -Root $cache
$pythonRuntime = Join-Path $cache 'runtime\python.exe'
Push-Location $cache
try {
    if ($Check) { & (Join-Path $repo 'private\dev\test.ps1') -RuntimeRoot $cache }
    & $pythonRuntime -B -X utf8 (Join-Path $repo 'private\dev\audit-dependencies.py')
    if ($LASTEXITCODE) { throw 'Dependency audit failed.' }
    & (Join-Path $repo 'private\dev\package.ps1') -RuntimeRoot $cache -Version $Version
} finally { Pop-Location }
