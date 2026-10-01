param(
    [ValidateRange(1, 120)]
    [int]$MaxFps = 30,
    [ValidateSet("auto", "cpu", "cuda", "cuda:0")]
    [string]$Device = "auto",
    [string]$AdbTarget = "",
    [switch]$Wireless,
    [ValidateRange(0, 999)]
    [int]$Version = 0
)

$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
    if ($Wireless -and [string]::IsNullOrWhiteSpace($AdbTarget)) {
        $wirelessConfig = ".\config\wireless-headset.json"
        if (-not (Test-Path -LiteralPath $wirelessConfig)) {
            throw "No saved wireless headset exists. Run enable-quest-wireless.ps1 with USB connected first."
        }
        $AdbTarget = (Get-Content -LiteralPath $wirelessConfig -Raw | ConvertFrom-Json).adbTarget
    }
    $candidates = @()
    foreach ($direction in Get-ChildItem .\models -Filter "qpro-stereo-tongue-v*-direction.pt" -File) {
        if ($direction.Name -match '^qpro-stereo-tongue-v(?<version>\d+)-direction\.pt$') {
            $candidates += [pscustomobject]@{ Version = [int]$Matches.version; Direction = $direction.FullName }
        }
    }
    $latest = if ($Version -gt 0) {
        $candidates | Where-Object Version -eq $Version | Select-Object -First 1
    } else {
        $candidates | Sort-Object Version -Descending | Select-Object -First 1
    }
    if ($null -eq $latest) {
        $requested = if ($Version -gt 0) { "v$Version" } else { "any version" }
        throw "No tongue direction checkpoint was found for $requested in .\models."
    }
    Write-Host "Previewing tongue model v$($latest.Version)"
    $launcherArguments = @{
        TonguePreview = $true
        MaxFps = $MaxFps
        TongueModelPath = $latest.Direction
        TongueModelDevice = $Device
    }
    if (-not [string]::IsNullOrWhiteSpace($AdbTarget)) {
        $launcherArguments.AdbTarget = $AdbTarget
    }
    & .\build-and-run.ps1 @launcherArguments
    if ($LASTEXITCODE -ne 0) {
        throw "The tongue preview exited with code $LASTEXITCODE."
    }
} finally {
    Pop-Location
}
