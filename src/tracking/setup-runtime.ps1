param([switch]$Legacy)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$runtime = Join-Path $root "runtime"
$python = Join-Path $runtime "python.exe"
$archive = Join-Path $root "python-runtime\python-3.13.15-embed-amd64.zip"
$expected = "d1f04d990aee1253d8569e8e5104e30fa9f5fa830899f14843448872d936a2cf"
$wheels = Join-Path $root "python-runtime\wheels"
$requirements = Join-Path $root "requirements-runtime.lock.txt"
$legacyRequirements = Join-Path $root "requirements-legacy.lock.txt"
$marker = Join-Path $runtime "runtime-ready.json"
$legacyMarker = Join-Path $runtime "legacy-ready.json"
$check = "import sys,cv2,numpy,onnxruntime,frida; assert sys.flags.no_site; assert sys.version_info[:3] == (3,13,15); assert hasattr(cv2,'namedWindow'); assert frida.__version__ == '17.18.0'; assert 'DmlExecutionProvider' in onnxruntime.get_available_providers()"
$legacyCheck = "import torch,onnx; assert torch.__version__ == '2.14.0+cpu'; assert onnx.__version__ == '1.23.0'"

function Write-PythonPath {
    Set-Content -LiteralPath (Join-Path $runtime "python313._pth") -Encoding ASCII -Value @("python313.zip", ".", "Lib\site-packages", "..", "..\hybrid")
}
if (Test-Path -LiteralPath $runtime) { Write-PythonPath }

function Test-Python([string]$Code) {
    $priorPreference = $ErrorActionPreference
    try { $ErrorActionPreference = "Continue"; & $python -c $Code *> $null; return $LASTEXITCODE -eq 0 }
    finally { $ErrorActionPreference = $priorPreference }
}
function Test-Marker([string]$Path, [string]$Lock) {
    if (-not (Test-Path -LiteralPath $python) -or -not (Test-Path -LiteralPath $Path)) { return $false }
    (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json).lockHash -eq (Get-FileHash -LiteralPath $Lock -Algorithm SHA256).Hash
}
function Write-Marker([string]$Path, [string]$Lock) {
    @{ format = "qft-portable-runtime-v2"; lockHash = (Get-FileHash -LiteralPath $Lock -Algorithm SHA256).Hash } | ConvertTo-Json | Set-Content -LiteralPath $Path -Encoding UTF8
}
function Install-Wheels([string]$Lock) {
    $pip = Join-Path $wheels "pip-26.2.1-py3-none-any.whl"
    if (-not (Test-Path -LiteralPath $pip)) { throw "The package installer is missing. Reinstall QFT+." }
    $pipHash = ((Get-Content -LiteralPath $requirements | Where-Object { $_ -match '^pip==' }) -split 'sha256:')[1].Trim()
    if ((Get-FileHash -LiteralPath $pip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pipHash) { throw "The package installer failed verification." }
    & $python -c "import sys,runpy; sys.path.insert(0,sys.argv.pop(1)); runpy.run_module('pip',run_name='__main__')" $pip install --require-hashes --no-deps --no-index --no-compile --disable-pip-version-check --upgrade --find-links $wheels --target (Join-Path $runtime "Lib\site-packages") -r $Lock
    if ($LASTEXITCODE -ne 0) { throw "Package installation failed." }
}
function Get-Component($Component) {
    $target = Join-Path $root $Component.file
    if ((Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -eq $Component.sha256) { return $target }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    $pending = "$target.download"
    $local = if ($env:QFT_RELEASE_SOURCE) { Join-Path $env:QFT_RELEASE_SOURCE (Split-Path -Leaf $Component.file) } else { "" }
    if ($local -and (Test-Path -LiteralPath $local)) { Copy-Item -LiteralPath $local -Destination $pending -Force }
    elseif ((Test-Path -LiteralPath $pending) -and (Get-Item -LiteralPath $pending).Length -eq $Component.size) { }
    else {
        Write-Host "Downloading $(Split-Path -Leaf $Component.file) ($([math]::Round($Component.size / 1MB)) MB)..."
        & "$env:SystemRoot\System32\curl.exe" --fail --location --silent --show-error --retry 3 --retry-delay 3 --connect-timeout 30 `
            --speed-limit 1024 --speed-time 60 -C - --output $pending $Component.url
        if ($LASTEXITCODE -ne 0) { throw "Couldn't download $(Split-Path -Leaf $Component.file) (curl error $LASTEXITCODE). Check the internet connection and try again; the download continues where it stopped." }
    }
    if ((Get-FileHash -LiteralPath $pending -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Component.sha256) {
        Remove-Item -LiteralPath $pending -Force
        throw "$(Split-Path -Leaf $Component.file) failed verification. Check the connection and try again."
    }
    Move-Item -LiteralPath $pending -Destination $target -Force
    return $target
}

if (-not ((Test-Marker $marker $requirements) -and (Test-Python $check))) {
    if (-not (Test-Path -LiteralPath $archive) -or -not (Test-Path -LiteralPath $wheels)) { throw "Bundled components are missing. Reinstall QFT+." }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw "The Python archive failed verification. Reinstall QFT+." }
    New-Item -ItemType Directory -Force -Path $runtime | Out-Null
    Expand-Archive -LiteralPath $archive -DestinationPath $runtime -Force
    Write-PythonPath
    Write-Host "Preparing tracking components..."
    Install-Wheels $requirements
    & $python -c $check
    if ($LASTEXITCODE -ne 0) { throw "Runtime check failed. See setup.log." }
    Write-Marker $marker $requirements
    Write-Host "Runtime ready."
} else { Write-Marker $marker $requirements; Write-Host "Runtime ready. No installation needed." }

if (-not $Legacy) { exit 0 }
if ((Test-Marker $legacyMarker $legacyRequirements) -and (Test-Python $legacyCheck)) { Write-Host "Older per-user models ready."; exit 0 }
Write-Host "Getting the older per-user models' components..."
foreach ($component in (Get-Content -LiteralPath (Join-Path $root "legacy-components.json") -Raw | ConvertFrom-Json)) {
    if ($component.unpacks -and -not ($component.unpacks | Where-Object { -not (Test-Path -LiteralPath (Join-Path $root $_)) })) { continue }
    $file = Get-Component $component
    if ($component.unpacks) { Expand-Archive -LiteralPath $file -DestinationPath $root -Force; Remove-Item -LiteralPath $file -Force }
}
Install-Wheels $legacyRequirements
& $python -c $legacyCheck
if ($LASTEXITCODE -ne 0) { throw "The older per-user models' components didn't install correctly. See setup.log." }
Write-Marker $legacyMarker $legacyRequirements
Write-Host "Older per-user models ready."
