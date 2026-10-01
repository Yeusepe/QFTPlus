param(
    [string]$SessionPath = "",
    [string]$BaseDirectionPath = "",
    [string]$PretrainedEncoderPath = "",
    [ValidateRange(1, 100)]
    [int]$Epochs = 24,
    [ValidateRange(8, 256)]
    [int]$BatchSize = 64
)

$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
    $python = if (-not [string]::IsNullOrWhiteSpace($env:QPRO_PYTHON)) { $env:QPRO_PYTHON } else { Join-Path $PSScriptRoot "runtime\python.exe" }
    if (-not (Test-Path -LiteralPath $python)) { throw "Run setup-runtime.ps1 -Legacy first." }

    $latest = if (-not [string]::IsNullOrWhiteSpace($SessionPath)) {
        Get-Item -LiteralPath ([System.IO.Path]::GetFullPath($SessionPath)) -ErrorAction Stop
    } else {
        Get-ChildItem .\captures -Filter "*.qpsession.json" -File |
            Sort-Object LastWriteTime -Descending |
            ForEach-Object {
                try {
                    $session = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
                    if ($session.sessionType -in @("tongue-stereo-corrections-v1", "tongue-stereo-refinement-v2", "tongue-stereo-arc-v3") -and $session.completed) { $_ }
                } catch {}
            } | Select-Object -First 1
    }
    if ($null -eq $latest) { throw "No completed quick-refinement capture was found." }
    $sessionMetadata = Get-Content -LiteralPath $latest.FullName -Raw | ConvertFrom-Json
    if ($sessionMetadata.sessionType -notin @("tongue-stereo-corrections-v1", "tongue-stereo-refinement-v2", "tongue-stereo-arc-v3") -or -not $sessionMetadata.completed) {
        throw "The selected dataset is not a completed quick-refinement capture."
    }
    $capture = $latest.FullName -replace '\.qpsession\.json$', '.qpcap'
    if (-not (Test-Path -LiteralPath $capture)) { throw "Matching capture is missing: $capture" }
    $cache = Join-Path $PSScriptRoot ("training\{0}-personal-refinement-224px" -f [System.IO.Path]::GetFileNameWithoutExtension($capture))
    Write-Host "TRAIN_STATUS phase=preparing"
    & $python .\prepare_tongue_stills.py $capture --session $latest.FullName --output $cache --size 224
    if ($LASTEXITCODE -ne 0) { throw "Preparing the refinement frames failed." }

    $bases = @(Get-ChildItem .\models -Filter "qpro-stereo-tongue-v*-direction.pt" -File | ForEach-Object {
        if ($_.Name -match '^qpro-stereo-tongue-v(?<v>\d+)-direction\.pt$') { [pscustomobject]@{ Version=[int]$Matches.v; Direction=$_.FullName } }
    } | Sort-Object Version -Descending)
    $fromEncoder = -not [string]::IsNullOrWhiteSpace($PretrainedEncoderPath)
    if ($fromEncoder) {
        if ($BaseDirectionPath) { throw "Choose either a base model to refine or a pretrained encoder, not both." }
        if (-not (Test-Path -LiteralPath $PretrainedEncoderPath -PathType Leaf)) { throw "The pretrained mouth encoder was not found." }
        $PretrainedEncoderPath = (Resolve-Path -LiteralPath $PretrainedEncoderPath).Path
    } elseif (-not $bases.Count -and -not $BaseDirectionPath) { throw "No base tongue model was found." }
    $base = if ($bases.Count) { $bases[0] } else { [pscustomobject]@{ Version=0; Direction="" } }
    if ($BaseDirectionPath) {
        if (-not (Test-Path -LiteralPath $BaseDirectionPath -PathType Leaf)) { throw "The selected base tongue model is missing." }
        $base.Direction = $BaseDirectionPath
    }
    $version = $base.Version + 1
    while ((Test-Path -LiteralPath ".\models\qpro-stereo-tongue-v$version-gate.pt") -or
           (Test-Path -LiteralPath ".\models\qpro-stereo-tongue-v$version-direction.pt")) { $version++ }
    $directionOutput = ".\models\qpro-stereo-tongue-v$version-direction.pt"

    $device = @(& $python -c "import torch; print('cuda:0' if torch.cuda.is_available() else 'cpu')" | Select-Object -Last 1)[0].Trim()
    if ($LASTEXITCODE -ne 0 -or $device -notin @("cuda:0", "cpu")) { throw "Could not determine the PyTorch training device. Run PC runtime setup again." }
    $effectiveBatchSize = if ($device -eq "cpu") { [Math]::Min($BatchSize, 16) } else { $BatchSize }
    Write-Host "TRAIN_DEVICE device=$device batch=$effectiveBatchSize"
    if ($device -eq "cpu") { Write-Warning "CUDA is unavailable. CPU fallback is active; training can take substantially longer. Compatible calibration jobs can also use the QFT+ GPU trainer." }

    if ($fromEncoder) {
        Write-Host "TRAIN_STAGE index=1 total=1 name=direction epochs=$Epochs device=$device"
        & $python .\train_tongue_model.py $cache --architecture spatial-stereo-resnet-v2 --checkpoint-focus direction --pretrained-encoder $PretrainedEncoderPath --epochs $Epochs --batch-size $effectiveBatchSize --device $device --output $directionOutput
        if ($LASTEXITCODE -ne 0) { throw "Training the tongue model from the pretrained encoder failed." }
        Write-Host "MODEL_READY version=$version parent=0"
    } else {
        Write-Host "TRAIN_STAGE index=1 total=1 name=direction epochs=$Epochs device=$device"
        & $python .\train_tongue_model.py $cache --architecture spatial-stereo-resnet-v2 --checkpoint-focus direction --initial-checkpoint $base.Direction --learning-rate 0.00005 --epochs $Epochs --batch-size $effectiveBatchSize --device $device --output $directionOutput
        if ($LASTEXITCODE -ne 0) { throw "Refining tongue direction failed." }
        Write-Host "MODEL_READY version=$version parent=$($base.Version)"
    }
}
finally {
    Pop-Location
}
