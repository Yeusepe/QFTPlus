param(
    [string]$AdbTarget = "",
    [switch]$AutoStart
)

$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
    $configPath = ".\config\wireless-headset.json"
    $saved = if (Test-Path -LiteralPath $configPath) {
        Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    } else { $null }
    if ([string]::IsNullOrWhiteSpace($AdbTarget)) {
        if ($null -eq $saved) {
            throw "Pass -AdbTarget or run enable-quest-wireless.ps1 first."
        }
        $AdbTarget = $saved.adbTarget
    }
    if ($AutoStart -and (-not $saved.usbSerial -or $saved.adbTarget -ne $AdbTarget)) {
        throw "No matching USB serial saved. Run enable-quest-wireless.ps1 with USB connected first."
    }
    $adbCommand = Get-Command adb -ErrorAction SilentlyContinue
    $adb = if ($env:QPRO_ADB -and (Test-Path -LiteralPath $env:QPRO_ADB)) {
        $env:QPRO_ADB
    } elseif (Test-Path -LiteralPath "$PSScriptRoot\platform-tools\adb.exe") {
        "$PSScriptRoot\platform-tools\adb.exe"
    } elseif ($null -ne $adbCommand) {
        $adbCommand.Source
    } else {
        Join-Path $env:LOCALAPPDATA "Android\Sdk\platform-tools\adb.exe"
    }
    if (-not (Test-Path -LiteralPath $adb)) { throw "adb.exe was not found." }

    $null = & $adb connect $AdbTarget 2>&1
    & $adb -s $AdbTarget usb
    $usbExit = $LASTEXITCODE
    $null = & $adb disconnect $AdbTarget 2>&1
    if ($usbExit -ne 0) {
        throw "Could not switch $AdbTarget back to USB mode. Rebooting the headset also disables this ADB TCP session."
    }
    if ($AutoStart) {
        $autoPath = Join-Path $env:APPDATA "VRCFaceTracking\QproAutoStart.json"
        if (Test-Path -LiteralPath $autoPath) {
            $auto = Get-Content -LiteralPath $autoPath -Raw | ConvertFrom-Json
            if ($auto.adbTarget -eq $AdbTarget) {
                $auto.adbTarget = $saved.usbSerial
                [System.IO.File]::WriteAllText($autoPath, ($auto | ConvertTo-Json -Depth 20), [System.Text.UTF8Encoding]::new($false))
            }
        }
    }
    Write-Host "WIRELESS_ADB_DISABLED $AdbTarget"
} finally {
    Pop-Location
}
