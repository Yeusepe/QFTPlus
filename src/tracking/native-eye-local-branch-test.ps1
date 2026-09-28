param(
    [switch]$RestoreOnly,
    [switch]$Personalized,
    [switch]$Calibrate,
    [switch]$RuntimePreview,
    [switch]$NoWindow,
    [switch]$VrcftOutput,
    [string]$AdbTarget = "",
    [switch]$Wireless,
    [string]$CalibrationOutput = ".\calibration\qpro-independent-visual-axis-v2.json",
    [string]$OverlayPath = ".\third_party\BabbleCalibration-Windows-1.0.8\BabbleCalibration.x86_64.exe",
    [ValidateRange(20, 300)]
    [int]$GazeSeconds = 60,
    [ValidateRange(20, 300)]
    [int]$ConvergenceSeconds = 80,
    [ValidateRange(0, 30)]
    [int]$HeadlessSeconds = 0,
    [string]$StopFile = "",
    [double]$VergenceGain = 1.0
)

trap { [Console]::Out.WriteLine("QFT_ERROR: " + ($_.Exception.Message -replace '\s+', ' ')); break }
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$adb = if (-not [string]::IsNullOrWhiteSpace($env:QPRO_ADB) -and (Test-Path -LiteralPath $env:QPRO_ADB)) {
    [System.IO.Path]::GetFullPath($env:QPRO_ADB)
} elseif (Test-Path -LiteralPath (Join-Path $root "platform-tools\adb.exe")) {
    Join-Path $root "platform-tools\adb.exe"
} else {
    Join-Path $env:LOCALAPPDATA "Android\Sdk\platform-tools\adb.exe"
}
$hadAndroidSerial = Test-Path Env:ANDROID_SERIAL
$previousAndroidSerial = $env:ANDROID_SERIAL

function Resolve-WorkspacePath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return "" }
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath((Join-Path $root $Path))
}

if ($Wireless -and [string]::IsNullOrWhiteSpace($AdbTarget)) {
    $wirelessConfig = Join-Path $root "config\wireless-headset.json"
    if (-not (Test-Path -LiteralPath $wirelessConfig)) {
        throw "No saved wireless headset exists. Run enable-quest-wireless.ps1 with USB connected first."
    }
    $AdbTarget = (Get-Content -LiteralPath $wirelessConfig -Raw | ConvertFrom-Json).adbTarget
}
$python = if (-not [string]::IsNullOrWhiteSpace($env:QPRO_PYTHON)) { $env:QPRO_PYTHON } else { Join-Path $root ".venv\Scripts\python.exe" }
$pythonFallback = Join-Path $root ".venv\Scripts\qpro-python-console.exe"
if (-not (Test-Path -LiteralPath $python) -and (Test-Path -LiteralPath $pythonFallback)) {
    $python = $pythonFallback
}
$localModel = Join-Path $root "models\eye\bolt-independent-axes.ptl"
$remoteModel = "/data/local/tmp/qpro-seacliff-independent-axes.ptl"
$targetModel = "/odm/etc/eyetracking/runtime/models/Seacliff_V1_5/fbnet/int8/experimental/bolt/bolt.ptl"
$modelProperty = "persist.device_config.oculus_shared_vision.oculus_eyetracking_enable_experimental_model"
$overlay = Resolve-WorkspacePath $OverlayPath
$calibrationOutputPath = Resolve-WorkspacePath $CalibrationOutput

if (-not (Test-Path -LiteralPath $adb)) { throw "A required file is missing (platform-tools\adb.exe). Reinstall QFT+." }
if (-not (Test-Path -LiteralPath $python)) { throw "Project Python environment not found under .venv\Scripts." }
if ($Calibrate) {
    if (-not (Test-Path -LiteralPath $overlay)) { throw "BabbleCalibration not found: $overlay" }
    if (-not (Get-Process -Name "vrserver" -ErrorAction SilentlyContinue)) {
        throw "Start SteamVR before visual-axis calibration."
    }
}
if (($RuntimePreview -or $VrcftOutput) -and -not (Test-Path -LiteralPath $calibrationOutputPath)) {
    throw "Independent eye calibration is missing. To use standard eye tracking, turn off Independent eye gaze in Settings."
}
if ($VrcftOutput -and -not (Get-Process -Name "VRCFaceTracking" -ErrorAction SilentlyContinue)) {
    throw "Open VRCFaceTracking, then start tracking."
}

if (-not [string]::IsNullOrWhiteSpace($AdbTarget)) {
    $env:ANDROID_SERIAL = $AdbTarget.Trim()
}

function Invoke-Root([string]$Command, [switch]$AllowFailure) {
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = & $adb shell su -c $Command 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "The headset didn't accept a command. Make sure it's awake and connected, then try again. (${Command}: $output)"
    }
    return ($output | Out-String).Trim()
}

function Wait-TrackingService {
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ((Invoke-Root "getprop init.svc.trackingservice" -AllowFailure) -eq "running") {
            Start-Sleep -Milliseconds 1500
            return
        }
        Start-Sleep -Milliseconds 250
    }
    throw "The headset's tracking service didn't restart. Restart the headset, then try again."
}

function Restore-StockModel([string]$PropertyValue = "false") {
    Invoke-Root "stop trackingservice" -AllowFailure | Out-Null
    Invoke-Root "setprop $modelProperty $PropertyValue" -AllowFailure | Out-Null
    Invoke-Root "umount '$targetModel'" -AllowFailure | Out-Null
    Invoke-Root "start trackingservice" -AllowFailure | Out-Null
    Wait-TrackingService
    Invoke-Root "rm -f '$remoteModel'" -AllowFailure | Out-Null
}

try {
    & $adb get-state | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Quest Pro isn't connected. Connect it, and in the headset choose 'Always allow from this computer.'" }
    $rootProbe = & $adb shell su -c id 2>&1
    if ($LASTEXITCODE -ne 0 -or ($rootProbe -join "`n") -notmatch 'uid=0\(root\)') {
        throw "Shell doesn't have root access yet. In the headset, open Magisk > Superuser, allow Shell, then try again."
    }
    if (-not [string]::IsNullOrWhiteSpace($AdbTarget)) {
        Write-Host "Independent-eye ADB target: $AdbTarget"
    }

    if ($RestoreOnly) {
        Restore-StockModel
        Write-Host "Stock Meta eye model restored."
        exit 0
    }
    $overrideOwner = [System.Threading.Mutex]::new($false, "Local\QFTPlus.EyeModelOverride")
    try { $ownsOverride = $overrideOwner.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $ownsOverride = $true }
    if (-not $ownsOverride) {
        throw "Independent eye tracking is already running in another QFT+ window. Stop it there, then try again."
    }
    $existingMount = Invoke-Root "grep -F '$targetModel' /proc/mounts" -AllowFailure
    if (-not [string]::IsNullOrWhiteSpace($existingMount)) {
        Write-Host "Restoring the stock eye model left active by an earlier run."
        Restore-StockModel
    }
    if (-not (Test-Path -LiteralPath $localModel)) {
        throw "The independent eye model is missing. Run setup again to rebuild it."
    }

    $originalProperty = (Invoke-Root "getprop $modelProperty" -AllowFailure)
    if ([string]::IsNullOrWhiteSpace($originalProperty)) { $originalProperty = "false" }
    $cleanupNeeded = $false
    try {
    Invoke-Root "umount '$targetModel'" -AllowFailure | Out-Null
    & $adb push $localModel $remoteModel | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Couldn't copy the eye model to the headset. Check the connection, then try again." }
    $cleanupNeeded = $true
    Invoke-Root "chown root:root '$remoteModel'" | Out-Null
    Invoke-Root "chmod 0644 '$remoteModel'" | Out-Null
    Invoke-Root "chcon u:object_r:vendor_configs_file:s0 '$remoteModel'" | Out-Null
    Invoke-Root "mount --bind '$remoteModel' '$targetModel'" | Out-Null

    $localHash = (Get-FileHash -LiteralPath $localModel -Algorithm SHA256).Hash.ToLowerInvariant()
    $remoteHash = ((Invoke-Root "sha256sum '$targetModel'") -split "\s+")[0].ToLowerInvariant()
    if ($localHash -ne $remoteHash) { throw "The eye model on the headset didn't match the one on this PC. Try again." }

    Invoke-Root "setprop $modelProperty true" | Out-Null
    Invoke-Root "stop trackingservice" | Out-Null
    Invoke-Root "start trackingservice" | Out-Null
    Wait-TrackingService
    Write-Host "Temporary independent Meta gaze branch active. Q restores the stock model."

    if ($RuntimePreview -or $VrcftOutput) {
        $runtimeArguments = @(
            (Join-Path $root "independent_visual_axis_runtime.py"),
            "--adb", $adb,
            "--calibration", $calibrationOutputPath
        )
        if ($VrcftOutput) { $runtimeArguments += "--output-vrcft" }
        if ($NoWindow) { $runtimeArguments += "--no-window" }
        if ($HeadlessSeconds -gt 0) {
            $runtimeArguments += @("--headless-seconds", $HeadlessSeconds)
        }
        if (-not [string]::IsNullOrWhiteSpace($StopFile)) {
            $runtimeArguments += @(
                "--stop-file",
                (Resolve-WorkspacePath $StopFile)
            )
        }
        $runtimeArguments += @(
            "--vergence-gain",
            $VergenceGain.ToString([System.Globalization.CultureInfo]::InvariantCulture)
        )
        & $python @runtimeArguments
    }
    elseif ($Calibrate) {
        & $python (Join-Path $root "native_raw_eye_probe.py") `
            --adb $adb `
            --title "Quest Pro independent visual-axis calibration" `
            --notice "TEMPORARY MODEL OVERRIDE - Meta personalization after local branch; Q restores stock" `
            --calibration-overlay $overlay `
            --calibration-output $calibrationOutputPath `
            --gaze-seconds $GazeSeconds `
            --convergence-seconds $ConvergenceSeconds
    }
    elseif ($Personalized) {
        & $python (Join-Path $root "native_raw_eye_probe.py") `
            --adb $adb `
            --title "Quest Pro independent personalized visual axes" `
            --notice "TEMPORARY MODEL OVERRIDE - Meta per-eye calibration after local branch; Q restores stock" `
            --instruction "Test a fixed target around the center and corners, then drift or close one eye; the other ray must remain fixed."
    }
    else {
        & $python (Join-Path $root "native_eye_stage_probe.py") `
            --adb $adb `
            --title "Quest Pro Meta local-branch gaze test" `
            --notice "TEMPORARY MODEL OVERRIDE - stock model is restored when Q quits" `
            --instruction "Close one eye or drift only the right eye; the two bottom axes should now remain independent."
    }
    if ($LASTEXITCODE -ne 0) { throw "Independent eye tracking stopped unexpectedly. The Eye tracking log shows why." }
    }
    finally {
        if ($cleanupNeeded) {
            Restore-StockModel $originalProperty
            Write-Host "Stock Meta eye model restored."
        }
    }
}
finally {
    if ($ownsOverride) { $overrideOwner.ReleaseMutex() }
    if ($hadAndroidSerial) {
        $env:ANDROID_SERIAL = $previousAndroidSerial
    }
    else {
        Remove-Item Env:ANDROID_SERIAL -ErrorAction SilentlyContinue
    }
}
