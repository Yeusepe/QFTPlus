$ErrorActionPreference = 'Stop'
$sdk = $env:ANDROID_HOME
if (-not $sdk) { $sdk = Join-Path $env:LOCALAPPDATA 'Android/Sdk' }
$tools = Get-ChildItem (Join-Path $sdk 'build-tools') -Directory |
    Where-Object Name -Match '^\d+\.\d+\.\d+$' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$platform = Get-ChildItem (Join-Path $sdk 'platforms') -Directory |
    Where-Object Name -Match '^android-\d+$' | Sort-Object { [int]($_.Name -replace 'android-', '') } -Descending | Select-Object -First 1
if (-not $tools -or -not $platform) { throw 'Android SDK build tools and platform are required.' }
$output = Join-Path ([IO.Path]::GetTempPath()) ('qft-localization-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $output | Out-Null
$aapt = Join-Path $tools.FullName 'aapt2.exe'
& $aapt compile --dir src/headset/app/res -o "$output/resources.zip"
if ($LASTEXITCODE) { throw 'Android resource compilation failed.' }
& $aapt link -I (Join-Path $platform.FullName 'android.jar') --manifest src/headset/app/AndroidManifest.xml -o "$output/resources.apk" "$output/resources.zip"
if ($LASTEXITCODE) { throw 'Android resource linking failed.' }
