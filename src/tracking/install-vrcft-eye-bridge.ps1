param([switch]$Rebuild)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$customLibs = Join-Path $env:APPDATA 'VRCFaceTracking\CustomLibs'
$source = Join-Path $root 'vrcft-gaze-bridge\bin\Release\net10.0\Qpro.GazeBridge.dll'
$destination = Join-Path $customLibs '000-Qpro.IndependentGaze.dll'
$transaction = [Guid]::NewGuid().ToString('N')
$staged = Join-Path $customLibs ($transaction + '.tmp')
$previous = Join-Path $customLibs ($transaction + '.bak')
$moved = @()
$replaced = $false

try {
    if ($Rebuild) {
        $project = Join-Path $root 'vrcft-gaze-bridge\Qpro.GazeBridge.csproj'
        if (-not (Test-Path -LiteralPath $project)) { throw 'This package contains a prebuilt module. Rebuilding requires the QFT+ source checkout.' }
        & dotnet build $project -c Release -o (Split-Path -Parent $source)
        if ($LASTEXITCODE -ne 0) { throw 'Building the VRCFaceTracking module failed. Open the setup log for details.' }
    }
    if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or (Get-Item -LiteralPath $source).Length -eq 0) {
        throw 'The QFT+ VRCFaceTracking module is missing or incomplete. Repair or reinstall QFT+ before trying setup again.'
    }
    if (Get-Process -Name 'VRCFaceTracking' -ErrorAction SilentlyContinue) {
        throw 'Quit VRCFaceTracking from its system tray menu, then try again.'
    }

    New-Item -ItemType Directory -Force -Path $customLibs | Out-Null
    Copy-Item -LiteralPath $source -Destination $staged
    $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    if ((Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash -ne $sourceHash) { throw 'The module copy could not be verified. Try again.' }

    for ($attempt = 0; ; $attempt++) {
        try {
            if (Test-Path -LiteralPath $destination) { [IO.File]::Replace($staged, $destination, $previous) }
            else { [IO.File]::Move($staged, $destination) }
            $replaced = $true
            break
        } catch {
            if (($_.Exception.GetBaseException().HResult -band 0xffff) -notin @(32, 33) -or $attempt -ge 8) { throw }
            Start-Sleep -Milliseconds 250
        }
    }
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $sourceHash) { throw 'The installed module could not be verified. Try again.' }

    foreach ($id in @('7f9be083-a4f1-4e30-b28a-8e6ec878d583', '91a90618-b020-4064-8832-809b2ca2b3bc', '2a8c8080-2a76-46af-bf76-1da7c0127ef8')) {
        $conflict = [IO.Path]::GetFullPath((Join-Path $customLibs $id))
        $backup = [IO.Path]::GetFullPath((Join-Path $root "backups\vrcft-$id-$transaction"))
        if (-not $conflict.StartsWith([IO.Path]::GetFullPath($customLibs) + '\', [StringComparison]::OrdinalIgnoreCase) -or
            -not $backup.StartsWith([IO.Path]::GetFullPath($root) + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid module backup path.' }
        if (Test-Path -LiteralPath $conflict) {
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $backup) | Out-Null
            Move-Item -LiteralPath $conflict -Destination $backup
            $moved += @{ Original = $conflict; Backup = $backup }
        }
    }
} catch {
    $failure = $_.Exception.GetBaseException()
    $message = $failure.Message
    if (($failure.HResult -band 0xffff) -in @(32, 33)) { $message = 'The module file is still in use. Quit VRCFaceTracking from its system tray menu, then try again.' }
    elseif ($failure -is [UnauthorizedAccessException]) { $message = 'Windows blocked the module update. Check access to the VRCFaceTracking CustomLibs folder and try again.' }
    try {
        for ($i = $moved.Count - 1; $i -ge 0; $i--) { Move-Item -LiteralPath $moved[$i].Backup -Destination $moved[$i].Original }
        if ($replaced) {
            if (Test-Path -LiteralPath $previous) { [IO.File]::Replace($previous, $destination, $staged) }
            else { Remove-Item -LiteralPath $destination }
        }
    } catch { $message += " Recovery also failed: $($_.Exception.Message). Backups are in $customLibs and $(Join-Path $root 'backups')." }
    Write-Output "QFT_SETUP_ERROR: $message"
    Write-Error -Message $message -ErrorAction Continue
    exit 1
} finally {
    if (Test-Path -LiteralPath $staged) { Remove-Item -LiteralPath $staged -ErrorAction SilentlyContinue }
}
if (Test-Path -LiteralPath $previous) { Remove-Item -LiteralPath $previous -ErrorAction SilentlyContinue }
Write-Output 'Installed and verified the QFT+ VRCFaceTracking module.'
