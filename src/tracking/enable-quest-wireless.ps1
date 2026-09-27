param(
    [string]$UsbSerial = "",
    [ValidateRange(1024, 65535)]
    [int]$Port = 5555,
    [switch]$AutoStart
)

$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
    $adbCommand = Get-Command adb -ErrorAction SilentlyContinue
    if ($env:QPRO_ADB -and (Test-Path -LiteralPath $env:QPRO_ADB)) {
        $adb = $env:QPRO_ADB
    } elseif (Test-Path -LiteralPath "$PSScriptRoot\platform-tools\adb.exe") {
        $adb = "$PSScriptRoot\platform-tools\adb.exe"
    } elseif ($null -eq $adbCommand) {
        $sdkAdb = Join-Path $env:LOCALAPPDATA "Android\Sdk\platform-tools\adb.exe"
        if (-not (Test-Path -LiteralPath $sdkAdb)) {
            throw "adb.exe was not found. Install Android platform-tools or add adb to PATH."
        }
        $adb = $sdkAdb
    } else {
        $adb = $adbCommand.Source
    }

    if ([string]::IsNullOrWhiteSpace($UsbSerial)) {
        $devices = @(
            @(& $adb devices) | ForEach-Object {
                if ($_ -match '^([^\s:]+)\s+device$') { $Matches[1] }
            }
        )
        if ($devices.Count -ne 1) {
            throw "Connect exactly one Quest by USB, or pass -UsbSerial. Found $($devices.Count) USB devices."
        }
        $UsbSerial = [string]$devices[0]
    }

    $rootProbe = (& $adb -s $UsbSerial shell su -c id 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0 -or $rootProbe -notmatch 'uid=0\(root\)') {
        throw @"
Magisk has not granted superuser access to Android Shell.
On the headset, open Magisk > Superuser and enable the entry named Shell (or ADB Shell).
If no entry is visible, rerun this command while watching the headset and approve the prompt.
No wireless setting was changed. Do not install questcam-magisk.zip for this project.
"@
    }

    $route = (& $adb -s $UsbSerial shell ip -4 route get 1.1.1.1 2>&1) -join " "
    if ($LASTEXITCODE -ne 0 -or $route -notmatch '\bsrc\s+(?<ip>\d+\.\d+\.\d+\.\d+)') {
        throw "Could not determine the headset Wi-Fi address from $UsbSerial."
    }
    $ipAddress = $Matches.ip
    $target = "${ipAddress}:$Port"

    Write-Host "Switching the USB-authorized headset ADB daemon to TCP port $Port"
    & $adb -s $UsbSerial tcpip $Port
    if ($LASTEXITCODE -ne 0) { throw "Enabling ADB-over-Wi-Fi failed." }
    Start-Sleep -Seconds 2
    & $adb connect $target
    if ($LASTEXITCODE -ne 0) { throw "Connecting to $target failed." }
    $state = (& $adb -s $target get-state 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0 -or $state.Trim() -ne "device") {
        throw "The wireless headset did not reach the ADB device state: $target"
    }
    $wirelessSerial = (& $adb -s $target shell getprop ro.serialno) -join ""
    if ($LASTEXITCODE -ne 0 -or $wirelessSerial.Trim() -ne $UsbSerial) {
        throw "Wireless address $target does not identify the USB headset $UsbSerial."
    }
    $wirelessRoot = (& $adb -s $target shell su -c id 2>&1) -join " "
    if ($LASTEXITCODE -ne 0 -or $wirelessRoot -notmatch 'uid=0\(root\)') {
        throw "Root access is unavailable over Wi-Fi. Automatic startup was not changed."
    }

    New-Item -ItemType Directory -Force .\config | Out-Null
    [ordered]@{
        adbTarget = $target
        usbSerial = $UsbSerial
        configuredUtc = [DateTime]::UtcNow.ToString("o")
        transport = "adb-tcp"
    } | ConvertTo-Json | Set-Content -LiteralPath .\config\wireless-headset.json -Encoding utf8

    if ($AutoStart) {
        $autoPath = Join-Path $env:APPDATA "VRCFaceTracking\QproAutoStart.json"
        $auto = if (Test-Path -LiteralPath $autoPath) {
            Get-Content -LiteralPath $autoPath -Raw | ConvertFrom-Json
        } else { [pscustomobject]@{} }
        $auto | Add-Member -NotePropertyName root -NotePropertyValue $PSScriptRoot -Force
        $auto | Add-Member -NotePropertyName adbTarget -NotePropertyValue $target -Force
        New-Item -ItemType Directory -Force (Split-Path $autoPath) | Out-Null
        [System.IO.File]::WriteAllText($autoPath, ($auto | ConvertTo-Json -Depth 20), [System.Text.UTF8Encoding]::new($false))
        Write-Host "Automatic startup now uses Wi-Fi. Restart VRCFaceTracking to apply."
    }

    Write-Host "WIRELESS_ADB_READY $target"
    Write-Host "Tongue preview: .\preview-latest-tongue.ps1 -Wireless -Version 8"
    Write-Warning "ADB is reachable on the local network until headset reboot or disable-quest-wireless.ps1. Use only a trusted private network."
} finally {
    Pop-Location
}
