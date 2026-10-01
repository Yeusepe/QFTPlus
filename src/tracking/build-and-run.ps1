param(
    [switch]$NoWindow,
    [switch]$StudioMode,
    [string]$CalibrationUi = "",
    [string]$ResumeSession = "",
    [string]$PupilCalibrationPath = "",
    [switch]$SkipPythonSetup,
    [ValidateSet("all", "face", "eyes", "mouth")]
    [string]$CameraMode = "all",
    [ValidateRange(0, 120)]
    [int]$MaxFps = 30,
    [ValidateRange(1024, 65535)]
    [int]$StreamPort = 27273,
    [string]$AdbTarget = "",
    [switch]$Record,
    [ValidateRange(0, 3600)]
    [int]$RecordSeconds = 0,
    [string]$RecordPath = "",
    [ValidateRange(1024, 65535)]
    [int]$LabelsPort = 27274,
    [switch]$NoLabels,
    [switch]$Calibration,
    [switch]$TongueCalibration,
    [switch]$TongueStillCalibration,
    [switch]$TongueCorrectionCalibration,
    [switch]$TongueRefinementCalibration,
    [switch]$TongueArcCalibration,
    [switch]$TonguePreview,
    [switch]$AllCameras,
    [switch]$PupilPreview,
    [switch]$EnablePupilDilation,
    [ValidateSet("", "puff", "cheeks", "brows", "pucker", "corners", "nose", "jaw", "mouth")]
    [string]$ExtraFaceCapture = "",
    [string]$ExtraFaceModel = "",
    [switch]$EnableExtraFaceOutput,
    [switch]$EnableTongueOutput,
    [string]$TongueModelPath = ".\models\qpro-stereo-tongue-v8-direction.pt",
    [switch]$NoTongueModel,
    [string]$TongueModelDevice = "auto",
    [ValidateRange(0, 100)]
    [int]$TongueSmoothing = 55,
    [string]$StopFile = ""
)

trap { [Console]::Out.WriteLine("QFT_ERROR: " + ($_.Exception.Message -replace '\s+', ' ')); break }
$ErrorActionPreference = "Continue"
$relayStarted = $false
$relayProcess = $null
$relayClientOutputPath = Join-Path $PSScriptRoot "questpro-relay-client-output.txt"
$relayClientErrorPath = Join-Path $PSScriptRoot "questpro-relay-client-error.txt"
$labelBridgeProcess = $null
$launcherFailure = $null
$hadAndroidSerial = Test-Path Env:ANDROID_SERIAL
$previousAndroidSerial = $env:ANDROID_SERIAL
$previousStreamToken = $env:QFT_STREAM_TOKEN

function Find-AdbExecutable {
    if (-not [string]::IsNullOrWhiteSpace($env:QPRO_ADB) -and (Test-Path -LiteralPath $env:QPRO_ADB)) {
        return [System.IO.Path]::GetFullPath($env:QPRO_ADB)
    }

    $bundledPath = Join-Path $PSScriptRoot "platform-tools\adb.exe"
    if (Test-Path -LiteralPath $bundledPath) { return $bundledPath }

    $command = Get-Command adb -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $standardPath = Join-Path $env:LOCALAPPDATA "Android\Sdk\platform-tools\adb.exe"
    if (Test-Path -LiteralPath $standardPath) { return $standardPath }

    throw "Android Platform Tools were not found. Re-extract the release so platform-tools\adb.exe is present."
}

function Resolve-WorkspacePath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return "" }
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $Path))
}

function Get-RelayStartupError([string]$LogText, [string]$Fallback) {
    $sharedFailure = ($LogText -split '\r?\n' | Where-Object { $_ -match '^SHARED_MEMORY_FAILED\b' } | Select-Object -First 1)
    if ($sharedFailure) {
        return "The headset camera relay could not prepare shared memory. $sharedFailure Send questpro-live-relay.txt."
    }
    return "$Fallback Send questpro-live-relay.txt."
}

if (-not [string]::IsNullOrWhiteSpace($AdbTarget)) {
    $env:ANDROID_SERIAL = $AdbTarget.Trim()
}
if (Get-Variable PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}
$adbExecutable = Find-AdbExecutable

$nativeArtifacts = @(
    (Join-Path $PSScriptRoot "libquestpro-camera-streamer-v8.so"),
    (Join-Path $PSScriptRoot "questpro-camera-relay-v8")
)
foreach ($artifact in $nativeArtifacts) {
    if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) {
        throw "A packaged headset component is missing: $([IO.Path]::GetFileName($artifact)). Reinstall QFT+."
    }
}

Push-Location $PSScriptRoot
try {
    $tokenBytes = New-Object byte[] 32
    $generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($tokenBytes) } finally { $generator.Dispose() }
    $env:QFT_STREAM_TOKEN = ([BitConverter]::ToString($tokenBytes)).Replace('-', '').ToLowerInvariant()
    Write-Host "QproFaceTracking launcher v2.35 (stream port $StreamPort)"
    if (-not [string]::IsNullOrWhiteSpace($AdbTarget)) {
        $adbState = & $adbExecutable get-state 2>&1
        if ($LASTEXITCODE -ne 0 -or ($adbState -join "`n").Trim() -ne "device") {
            throw "ADB target is unavailable: $AdbTarget. Reconnect it or use USB."
        }
        Write-Host "ADB target: $AdbTarget"
    }
    else {
        $adbState = & $adbExecutable get-state 2>&1
        if ($LASTEXITCODE -ne 0 -or ($adbState -join "`n").Trim() -ne "device") {
            throw "No authorized Quest was found over ADB. Connect it by USB or pass -AdbTarget."
        }
    }

    $rootProbe = & $adbExecutable shell su -c id 2>&1
    $rootProbeText = ($rootProbe -join "`n").Trim()
    if ($LASTEXITCODE -ne 0 -or $rootProbeText -notmatch 'uid=0\(root\)') {
        $transportHint = if ([string]::IsNullOrWhiteSpace($AdbTarget)) { "USB" } else { $AdbTarget }
        throw "Magisk root is not granted to Android Shell on $transportHint. On the headset open Magisk > Superuser and enable Shell (or ADB Shell), then retry. The camera relay and provider injector were not started."
    }

    if ($TongueCalibration -or $TongueStillCalibration -or $TongueCorrectionCalibration -or $TongueRefinementCalibration -or $TongueArcCalibration) { $CameraMode = "face" }
    if ($TonguePreview) {
        $CameraMode = if ($AllCameras) { "all" } else { "mouth" }
        if (-not $PSBoundParameters.ContainsKey("MaxFps")) { $MaxFps = 20 }
    }

    if ($ExtraFaceCapture -or $PupilPreview -or $EnablePupilDilation -or $ExtraFaceModel) { $CameraMode = "all" }
    if (($ExtraFaceCapture -or $PupilPreview) -and $NoWindow -and -not $CalibrationUi) { throw "Face/pupil calibration requires a visible window." }
    if ($ExtraFaceCapture -and ($RecordSeconds -gt 0)) { throw "ExtraFaceCapture saves only on Space; do not set RecordSeconds." }
    $calibrationModes = @('Calibration', 'TongueCalibration', 'TongueStillCalibration',
        'TongueCorrectionCalibration', 'TongueRefinementCalibration', 'TongueArcCalibration')
    $selectedCalibrations = @($calibrationModes | Where-Object { $PSBoundParameters[$_] })
    $recordEnabled = $Record -or $selectedCalibrations.Count -gt 0 -or $ExtraFaceCapture -or -not [string]::IsNullOrWhiteSpace($RecordPath)
    $labelsEnabled = ($recordEnabled -or $TonguePreview) -and -not $NoLabels
    $vrcftRequired = $selectedCalibrations.Count -gt 0 -or $TonguePreview -or $EnablePupilDilation -or $EnableExtraFaceOutput
    if ($RecordSeconds -gt 0 -and -not $recordEnabled) {
        throw "RecordSeconds requires -Record or -RecordPath."
    }
    if ($Calibration -and $CameraMode -ne "all") { throw "Calibration requires -CameraMode all (the default)." }
    if ($selectedCalibrations.Count -gt 1 -or ($TonguePreview -and $selectedCalibrations.Count -gt 0)) {
        throw "Run one calibration or TonguePreview at a time."
    }
    foreach ($mode in $selectedCalibrations) {
        if ($NoWindow -and -not ($mode -eq 'TongueRefinementCalibration' -and $CalibrationUi)) {
            throw "$mode requires the visible prompt window."
        }
        if ($RecordSeconds -gt 0) { throw "$mode controls its own capture; do not set RecordSeconds." }
        if ($NoLabels) { throw "$mode requires factory labels from Virtual Desktop or Steam Link." }
    }
    if ($TonguePreview -and $NoWindow -and -not $EnableTongueOutput) { throw "Hidden tongue tracking requires EnableTongueOutput." }
    if ($TonguePreview -and $NoLabels) { throw "TonguePreview requires native TongueOut confidence." }
    if ($TonguePreview -and -not $NoTongueModel -and -not (Test-Path -LiteralPath $TongueModelPath)) { throw "Tongue model not found: $TongueModelPath" }
    if ($vrcftRequired -and -not (Get-Process -Name "VRCFaceTracking" -ErrorAction SilentlyContinue)) {
        throw "Start VRCFaceTracking first. No VRCFaceTracking process was found."
    }
    if ($vrcftRequired -and -not (Get-Process -Name "vrserver" -ErrorAction SilentlyContinue)) {
        throw "Start SteamVR first. SteamVR's vrserver process was not found."
    }

    $python = if ($env:QPRO_PYTHON) { $env:QPRO_PYTHON } else { Join-Path $PSScriptRoot "runtime\python.exe" }
    if (-not (Test-Path -LiteralPath $python)) {
        if ($SkipPythonSetup) { throw "Run setup-runtime.ps1 to prepare the bundled runtime." }
        & (Join-Path $PSScriptRoot "setup-runtime.ps1")
    }
    & $python -c "import cv2,numpy,onnxruntime; assert hasattr(cv2,'namedWindow')"
    if ($LASTEXITCODE) { throw "The runtime is incomplete. Run setup-runtime.ps1." }

    if ($labelsEnabled) {
        $labelBridgeExe = Join-Path $PSScriptRoot "vd-label-bridge\bin\Release\net10.0\Qpro.VirtualDesktopLabelBridge.exe"
        if (-not (Test-Path -LiteralPath $labelBridgeExe -PathType Leaf)) {
            throw "The packaged factory label bridge is missing. Reinstall QFT+."
        }
    }

    & $adbExecutable push .\libquestpro-camera-streamer-v8.so /data/local/tmp/libquestpro-camera-streamer-v8.so
    if ($LASTEXITCODE -ne 0) { throw "Pushing the streamer failed with exit code $LASTEXITCODE" }
    & $adbExecutable push .\questpro-camera-relay-v8 /data/local/tmp/questpro-camera-relay-v8
    if ($LASTEXITCODE -ne 0) { throw "Pushing the relay failed with exit code $LASTEXITCODE" }
    & $adbExecutable shell chmod 755 /data/local/tmp/questpro-camera-relay-v8
    if ($LASTEXITCODE -ne 0) { throw "Marking the headset executables runnable failed with exit code $LASTEXITCODE" }
    & $adbExecutable shell chmod 644 /data/local/tmp/libquestpro-camera-streamer-v8.so
    if ($LASTEXITCODE -ne 0) { throw "Setting the streamer library permissions failed with exit code $LASTEXITCODE" }
    & $adbExecutable shell "rm -f /data/local/tmp/questpro-live-v3.log /data/local/tmp/questpro-live-v8.log; touch /data/local/tmp/questpro-live-v3.log /data/local/tmp/questpro-live-v8.log; chmod 666 /data/local/tmp/questpro-live-v3.log /data/local/tmp/questpro-live-v8.log"
    if ($LASTEXITCODE -ne 0) { throw "Preparing the headset logs as Android Shell failed with exit code $LASTEXITCODE" }

    $null = & $adbExecutable forward --remove "tcp:$StreamPort" 2>&1
    if (-not [string]::IsNullOrWhiteSpace($AdbTarget)) {
        $injectOutput = & $python .\camera_injector.py --adb $adbExecutable 2>&1
        $injectExit = $LASTEXITCODE
        @($injectOutput | ForEach-Object { $_.ToString() }) | Tee-Object -FilePath .\questpro-live-inject.txt
        if ($injectExit -ne 0) { throw "Injection failed with exit code $injectExit. Send questpro-live-inject.txt." }
    }
    if (-not [string]::IsNullOrWhiteSpace($AdbTarget)) {
        $relayExec = "/data/local/tmp/questpro-camera-relay-v8 --mode $CameraMode --max-fps $MaxFps --token $env:QFT_STREAM_TOKEN"
        Remove-Item -LiteralPath $relayClientOutputPath, $relayClientErrorPath -Force -ErrorAction SilentlyContinue
        $relayArguments = "shell su -c `"$relayExec`""
        $relayProcess = Start-Process -FilePath $adbExecutable `
            -ArgumentList $relayArguments `
            -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput $relayClientOutputPath `
            -RedirectStandardError $relayClientErrorPath
        Start-Sleep -Milliseconds 800
        if ($relayProcess.HasExited) {
            $relayOutput = if (Test-Path -LiteralPath $relayClientOutputPath) { Get-Content -LiteralPath $relayClientOutputPath -Raw } else { "" }
            $relayError = if (Test-Path -LiteralPath $relayClientErrorPath) { Get-Content -LiteralPath $relayClientErrorPath -Raw } else { "" }
            $relayLogText = (($relayOutput, $relayError) -join "`n").Trim()
            Set-Content -LiteralPath .\questpro-live-relay.txt -Value $relayLogText
            if (-not [string]::IsNullOrWhiteSpace($relayLogText)) { Write-Host $relayLogText }
            $relayProcess.Dispose()
            $relayProcess = $null
            throw (Get-RelayStartupError $relayLogText "The wireless root relay exited during startup.")
        }
        $relayStarted = $true
        Write-Host "RELAY_LISTENING address=127.0.0.1 port=$StreamPort mode=$CameraMode max_fps=$MaxFps transport=live-adb-su"
    }
    else {
        $relayCommand = 'chmod 755 /data/local/tmp/questpro-camera-relay-v8; : > /data/local/tmp/questpro-relay-v8.log; nohup /data/local/tmp/questpro-camera-relay-v8 --mode {0} --max-fps {1} --token {2} > /data/local/tmp/questpro-relay-v8.log 2>&1 < /dev/null &' -f $CameraMode, $MaxFps, $env:QFT_STREAM_TOKEN
        & $adbExecutable shell su -c $relayCommand
        if ($LASTEXITCODE -ne 0) { throw "Starting the root relay failed with exit code $LASTEXITCODE" }
        $relayStarted = $true
        Start-Sleep -Milliseconds 800
        $relayLog = & $adbExecutable shell su -c "cat /data/local/tmp/questpro-relay-v8.log" 2>&1
        @($relayLog | ForEach-Object { $_.ToString() }) | Tee-Object -FilePath .\questpro-live-relay.txt
        if (-not (($relayLog -join "`n") -match "RELAY_LISTENING")) {
            throw (Get-RelayStartupError ($relayLog -join "`n") "The root relay did not begin listening.")
        }
    }

    if ([string]::IsNullOrWhiteSpace($AdbTarget)) {
        $injectOutput = & $python .\camera_injector.py --adb $adbExecutable 2>&1
        $injectExit = $LASTEXITCODE
        @($injectOutput | ForEach-Object { $_.ToString() }) | Tee-Object -FilePath .\questpro-live-inject.txt
        if ($injectExit -ne 0) { throw "Injection failed with exit code $injectExit. Send questpro-live-inject.txt." }
    }

    Start-Sleep -Milliseconds 800
    $headsetLog = & $adbExecutable shell su -c "cat /data/local/tmp/questpro-live-v8.log" 2>&1
    @($headsetLog | ForEach-Object { $_.ToString() }) | Tee-Object -FilePath .\questpro-live-headset.txt

    & $adbExecutable forward "tcp:$StreamPort" "tcp:$StreamPort"
    if ($LASTEXITCODE -ne 0) { throw "ADB port forwarding failed with exit code $LASTEXITCODE" }

    $capText = if ($MaxFps -eq 0) { "unlimited" } else { "$MaxFps FPS" }
    Write-Host "Transport mode: $CameraMode; cap: $capText"
    if ($labelsEnabled) {
        $labelBridgeProcess = Start-Process -FilePath $labelBridgeExe -ArgumentList @("--port", "$LabelsPort") -PassThru -WindowStyle Hidden -RedirectStandardOutput .\questpro-label-bridge.txt -RedirectStandardError .\questpro-label-bridge-error.txt
        Start-Sleep -Milliseconds 300
        if ($labelBridgeProcess.HasExited) {
            throw "The factory label bridge exited during startup. Send questpro-label-bridge-error.txt."
        }
    }
    $receiverArguments = @(".\receiver.py", "--port", "$StreamPort")
    if ($StudioMode) { $receiverArguments += "--studio" }
    if ($PupilPreview) { $receiverArguments += "--pupil-preview" }
    if ($EnablePupilDilation) { $receiverArguments += "--pupil-dilation" }
    if ($ExtraFaceCapture) { $receiverArguments += @("--extra-face-capture", $ExtraFaceCapture) }
    if ($ExtraFaceModel) { $receiverArguments += @("--extra-face-model", (Resolve-Path -LiteralPath $ExtraFaceModel).Path) }
    if ($EnableExtraFaceOutput) { $receiverArguments += "--extra-face-output" }
    if ($ResumeSession) { $receiverArguments += @("--resume-session", (Resolve-WorkspacePath $ResumeSession)) }
    if ($CalibrationUi) { $receiverArguments += @("--calibration-ui", (Resolve-WorkspacePath $CalibrationUi)) }
    if ($PupilCalibrationPath) { $receiverArguments += @("--pupil-calibration", (Resolve-WorkspacePath $PupilCalibrationPath)) }
    if ($NoWindow) { $receiverArguments += "--no-window" }
    if ($recordEnabled) {
        $receiverArguments += "--record"
        if (-not [string]::IsNullOrWhiteSpace($RecordPath)) { $receiverArguments += $RecordPath }
        if ($RecordSeconds -gt 0) { $receiverArguments += @("--record-seconds", "$RecordSeconds") }
        $receiverArguments += @("--labels-port", "$LabelsPort")
        if ($NoLabels) { $receiverArguments += "--no-labels" }
        if ($Calibration) { $receiverArguments += "--calibration" }
        if ($TongueCalibration) { $receiverArguments += "--tongue-calibration" }
        if ($TongueStillCalibration) { $receiverArguments += "--tongue-still-calibration" }
        if ($TongueCorrectionCalibration) { $receiverArguments += "--tongue-correction-calibration" }
        if ($TongueRefinementCalibration) { $receiverArguments += "--tongue-refinement-calibration" }
        if ($TongueArcCalibration) { $receiverArguments += "--tongue-arc-calibration" }
    }
    if ($TonguePreview) {
        if (-not $NoTongueModel) {
            $resolvedTongueModelPath = (Resolve-Path -LiteralPath $TongueModelPath).Path
            $receiverArguments += @("--tongue-model", $resolvedTongueModelPath, "--tongue-model-device", $TongueModelDevice)
        }
        $receiverArguments += @("--tongue-smoothing", "$TongueSmoothing", "--labels-port", "$LabelsPort")
        if ($EnableTongueOutput) { $receiverArguments += "--tongue-output" }
    }
    if (-not [string]::IsNullOrWhiteSpace($StopFile)) {
        $receiverArguments += @("--stop-file", (Resolve-WorkspacePath $StopFile))
    }
    & $python @receiverArguments
    if ($LASTEXITCODE -ne 0) { throw "The PC tracking runtime exited with code $LASTEXITCODE." }
} catch {
    $launcherFailure = $_
} finally {
    if ($null -ne $labelBridgeProcess -and -not $labelBridgeProcess.HasExited) {
        Stop-Process -Id $labelBridgeProcess.Id -Force -ErrorAction SilentlyContinue
        Wait-Process -Id $labelBridgeProcess.Id -Timeout 2 -ErrorAction SilentlyContinue
    }
    if ($relayStarted) {
        $stopOutput = & $adbExecutable shell su -c "/data/local/tmp/questpro-camera-relay-v8 --stop" 2>&1
        if ($LASTEXITCODE -eq 0) { $stopOutput | ForEach-Object { Write-Host $_ } }
        if ($null -ne $relayProcess) {
            if (-not $relayProcess.WaitForExit(2000)) {
                $relayProcess.Kill()
                $relayProcess.WaitForExit(2000) | Out-Null
            }
            $relayOutput = if (Test-Path -LiteralPath $relayClientOutputPath) { Get-Content -LiteralPath $relayClientOutputPath -Raw } else { "" }
            $relayError = if (Test-Path -LiteralPath $relayClientErrorPath) { Get-Content -LiteralPath $relayClientErrorPath -Raw } else { "" }
            (($relayOutput, $relayError) -join "`n").Trim() | Set-Content -LiteralPath .\questpro-live-relay.txt
            $relayProcess.Dispose()
        }
        else {
            $finalRelayLog = & $adbExecutable shell su -c "cat /data/local/tmp/questpro-relay-v8.log" 2>&1
            @($finalRelayLog | ForEach-Object { $_.ToString() }) | Set-Content -LiteralPath .\questpro-live-relay.txt
        }
    }
    $null = & $adbExecutable forward --remove "tcp:$StreamPort" 2>&1
    Pop-Location
    $env:QFT_STREAM_TOKEN = $previousStreamToken
    if ($hadAndroidSerial) {
        $env:ANDROID_SERIAL = $previousAndroidSerial
    } else {
        Remove-Item Env:ANDROID_SERIAL -ErrorAction SilentlyContinue
    }
}
if ($null -ne $launcherFailure) {
    $ErrorActionPreference = "Stop"
    throw $launcherFailure
}
