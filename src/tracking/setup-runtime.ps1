param()

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$runtime = Join-Path $root "runtime"
$python = Join-Path $runtime "python.exe"
$archive = Join-Path $root "python-runtime\python-3.13.15-embed-amd64.zip"
$expected = "d1f04d990aee1253d8569e8e5104e30fa9f5fa830899f14843448872d936a2cf"
$wheels = Join-Path $root "python-runtime\wheels"
$requirements = Join-Path $root "requirements-runtime.lock.txt"
$marker = Join-Path $runtime "runtime-ready.json"
$check = "import sys,cv2,numpy,torch,onnx,onnxruntime,frida; assert sys.flags.no_site; assert sys.version_info[:3] == (3,13,15); assert torch.__version__ == '2.14.0+cpu'; assert onnx.__version__ == '1.23.0'; assert hasattr(cv2,'namedWindow'); assert frida.__version__ == '17.18.0'; assert 'DmlExecutionProvider' in onnxruntime.get_available_providers()"

function Write-PythonPath {
    Set-Content -LiteralPath (Join-Path $runtime "python313._pth") -Encoding ASCII -Value @("python313.zip", ".", "Lib\site-packages", "..", "..\hybrid")
}
if (Test-Path -LiteralPath $runtime) { Write-PythonPath }

function Test-Runtime {
    if (-not (Test-Path -LiteralPath $python)) { return $false }
    if (Test-Path -LiteralPath $marker) {
        $ready = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
        if ($ready.lockHash -ne (Get-FileHash -LiteralPath $requirements -Algorithm SHA256).Hash) { return $false }
    } else { return $false }
    $priorPreference = $ErrorActionPreference
    try { $ErrorActionPreference = "Continue"; & $python -c $check *> $null; return $LASTEXITCODE -eq 0 }
    finally { $ErrorActionPreference = $priorPreference }
}
function Write-Ready {
    @{ format = "qft-portable-runtime-v2"; lockHash = (Get-FileHash -LiteralPath $requirements -Algorithm SHA256).Hash } | ConvertTo-Json | Set-Content -LiteralPath $marker -Encoding UTF8
}
if (Test-Runtime) { Write-Ready; Write-Host "Runtime ready. No installation needed."; exit 0 }
if (-not (Test-Path -LiteralPath $archive) -or -not (Test-Path -LiteralPath $wheels)) { throw "Bundled components are missing. Reinstall QFT+." }
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw "The bundled Python archive failed verification. Reinstall QFT+." }

New-Item -ItemType Directory -Force -Path $runtime | Out-Null
Expand-Archive -LiteralPath $archive -DestinationPath $runtime -Force
Write-PythonPath
$pip = Join-Path $wheels "pip-26.2.1-py3-none-any.whl"
if (-not (Test-Path -LiteralPath $pip)) { throw "Bundled package installer is missing. Reinstall QFT+." }
$pipHash = ((Get-Content -LiteralPath $requirements | Where-Object { $_ -match '^pip==' }) -split 'sha256:')[1].Trim()
if ((Get-FileHash -LiteralPath $pip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pipHash) { throw "Bundled package installer failed verification." }
$packages = Join-Path $runtime "Lib\site-packages"
Write-Host "Preparing bundled tracking components..."
& $python -c "import sys,runpy; sys.path.insert(0,sys.argv.pop(1)); runpy.run_module('pip',run_name='__main__')" $pip install --require-hashes --no-index --no-compile --disable-pip-version-check --upgrade --find-links $wheels --target $packages -r $requirements
if ($LASTEXITCODE -ne 0) { throw "Bundled package installation failed." }
& $python -c $check
if ($LASTEXITCODE -ne 0) { throw "Bundled runtime check failed. See setup.log." }
Write-Ready
Write-Host "Runtime ready."
