# Builds OdxArma3.exe into -OutDir: the C++ odx_capture_x64.dll first (CMake, Release), then the net48 launcher that
# embeds it with the odx_capture addon.
#   powershell -ExecutionPolicy Bypass -File build.ps1 -OutDir <folder>
param([Parameter(Mandatory)] [string]$OutDir)
$ErrorActionPreference = 'Stop'

$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
$extractor = Join-Path $repo 'capture\native\extractor'
$build = Join-Path $extractor 'build'
if (-not (Test-Path (Join-Path $build 'CMakeCache.txt'))) { cmake -S $extractor -B $build; if ($LASTEXITCODE) { exit 1 } }
cmake --build $build --config Release --target odx_capture
if ($LASTEXITCODE) { exit 1 }

dotnet build (Join-Path $PSScriptRoot 'OdxArma3') -c Release -o $OutDir
if ($LASTEXITCODE) { exit 1 }
Write-Host "built $(Join-Path $OutDir 'OdxArma3.exe')"
