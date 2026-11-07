# Builds odx-capture-<version>-win-x64 with the same layout as a released package, from that released package:
#   Odx Capture.exe           launcher, taken from the base package (it only starts bin\odx-app.exe); with -Launcher,
#                             built here (it embeds the splash and the icon)
#   bin\odx-app.exe           built here from this source
#   bin\odx.exe               built here from this source
#   bin\odx-capture.json      taken from the base package (orchestrator, access key, the client's delivery names)
#   bin\ffmpeg\, bin\games\   taken from the base package
#   data\captures\, data\delivery\
# Only the two executables this patch changes are rebuilt, so the machine needs only the .NET 10 SDK (global.json): no
# MSYS2 (ffmpeg), no MSVC (NativeAOT launcher), no release secrets.
#
#   powershell -ExecutionPolicy Bypass -File build-package.ps1 -Base <odx-capture-0.2.8-win-x64.zip> [-Version 0.2.9]
#                                                              [-Launcher] [-Test]
#
# -Launcher rebuilds "Odx Capture.exe" too (NativeAOT: needs the MSVC build tools, as publish.ps1).
#
# -Base: the released package zip (GitHub release capture-v0.2.8), or its unzipped folder.
# -Test runs the test suite first; its video tests need ffmpeg and ffprobe on PATH.
#
# Output in dist\:
#   odx-capture-<version>-win-x64\        ready to run (its data\ is kept across rebuilds)
#   odx-capture-<version>-win-x64.zip     the full package, laid out like the released one
#   odx-capture-<version>-update.zip      the rebuilt executables only, to unzip over an installed package
param([Parameter(Mandatory = $true)][string]$Base, [string]$Version = "0.2.9", [switch]$Launcher, [switch]$Test)
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$name = "odx-capture-$Version-win-x64"
$out = Join-Path $dist $name
$bin = Join-Path $out 'bin'
$work = Join-Path $root '.cache\package'
$built = Join-Path $work 'built'
$rebuilt = @('odx-app.exe', 'odx.exe')

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet not found: install the .NET 10 SDK (https://dotnet.microsoft.com/download/dotnet/10.0), then open a new PowerShell'
}

# The base package: a zip is unpacked; the package folder is the one holding "Odx Capture.exe" and bin\.
$Base = (Resolve-Path $Base).Path
if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory -Force $work | Out-Null
if ((Get-Item $Base) -is [IO.FileInfo]) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($Base, (Join-Path $work 'base'))
    $Base = Join-Path $work 'base'
}
$baseDir = Get-ChildItem $Base -Recurse -Filter 'Odx Capture.exe' | Select-Object -First 1 | ForEach-Object { $_.DirectoryName }
if (-not $baseDir) { throw "no 'Odx Capture.exe' in ${Base}: give the released odx-capture-<version>-win-x64.zip" }
foreach ($required in 'bin\odx-capture.json', 'bin\ffmpeg\ffmpeg.exe', 'bin\games') {
    if (-not (Test-Path (Join-Path $baseDir $required))) { throw "the base package has no $required" }
}

if ($Test) {
    dotnet test (Join-Path $root 'OdxCapture.sln') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'tests failed: nothing was packaged' }
}

# The two executables, with the same publish settings as publish.ps1.
$publishes = @(
    @{ Project = 'Odx.App'; Framework = $null },
    @{ Project = 'Odx.Cli'; Framework = 'net10.0-windows10.0.22621.0' }
)
foreach ($p in $publishes) {
    $frameworkArgs = if ($p.Framework) { @('-f', $p.Framework) } else { @() }
    dotnet publish (Join-Path $root "src/Apps/$($p.Project)") -c Release -r win-x64 --self-contained @frameworkArgs `
        -p:PublishReadyToRun=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None -p:Version=$Version -o $built
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $($p.Project) failed" }
}
foreach ($exe in $rebuilt) {
    if (-not (Test-Path (Join-Path $built $exe))) { throw "$exe was not produced" }
}
$launcherBuilt = $null
if ($Launcher) {
    # NativeAOT finds the MSVC linker through vswhere.exe, which is not on PATH by default (as publish.ps1).
    $env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"
    $launcherOut = Join-Path $work 'launcher'
    dotnet publish (Join-Path $root 'src/Apps/Odx.Launcher') -c Release -r win-x64 -p:DebugType=None -p:Version=$Version -o $launcherOut
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish Odx.Launcher failed' }
    $launcherBuilt = Join-Path $launcherOut 'odx-launcher.exe'
}

# The package: the base package's files, then the two rebuilt executables over them. data\ holds what this machine
# recorded: a rebuild replaces the program, never the data (as publish.ps1).
$launcherExe = Join-Path $out 'Odx Capture.exe'
foreach ($old in @($bin, $launcherExe)) { if (Test-Path $old) { Remove-Item -Recurse -Force $old } }
New-Item -ItemType Directory -Force $out, (Join-Path $out 'data\captures'), (Join-Path $out 'data\delivery') | Out-Null
Copy-Item $(if ($launcherBuilt) { $launcherBuilt } else { Join-Path $baseDir 'Odx Capture.exe' }) $launcherExe
Copy-Item (Join-Path $baseDir 'bin') $out -Recurse
foreach ($exe in $rebuilt) { Copy-Item (Join-Path $built $exe) (Join-Path $bin $exe) -Force }

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
function New-Zip([string]$path, [IO.FileInfo[]]$files, [string]$from, [string]$prefix, [string[]]$folders) {
    if (Test-Path $path) { Remove-Item -Force $path }
    $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $files) {
            $entry = $prefix + $file.FullName.Substring($from.Length + 1).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $entry,
                [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
        foreach ($folder in $folders) { $zip.CreateEntry($prefix + $folder) | Out-Null }
    }
    finally { $zip.Dispose() }
}

# The full package: the program and empty data folders only; this machine's data\ stays out.
$packageZip = Join-Path $dist "$name.zip"
New-Zip $packageZip (@(Get-Item $launcherExe) + @(Get-ChildItem $bin -Recurse -File)) $out "$name/" @('data/captures/', 'data/delivery/')

# The update: unzipped over an installed package, it replaces the rebuilt executables and nothing else.
$updateDir = Join-Path $work 'update'
New-Item -ItemType Directory -Force (Join-Path $updateDir 'bin') | Out-Null
foreach ($exe in $rebuilt) { Copy-Item (Join-Path $bin $exe) (Join-Path $updateDir "bin\$exe") }
if ($launcherBuilt) { Copy-Item $launcherExe (Join-Path $updateDir 'Odx Capture.exe') }
$replaced = if ($launcherBuilt) { 'Odx Capture.exe, bin\odx-app.exe va bin\odx.exe' } else { 'bin\odx-app.exe va bin\odx.exe' }
$readme = @"
CAP NHAT ODX CAPTURE LEN $Version

1. Tat Odx Capture (chuot phai icon o khay he thong > Exit).
2. Giai nen file zip nay vao thu muc odx-capture-...-win-x64 (thu muc co file "Odx Capture.exe").
3. Khi Windows hoi, chon "Replace the files in the destination".
4. Mo lai "Odx Capture.exe" bang quyen admin.

Chi thay: $replaced. Cau hinh va du lieu da quay (data\captures) giu nguyen.
"@
[IO.File]::WriteAllText((Join-Path $updateDir 'CAP-NHAT.txt'), $readme, (New-Object Text.UTF8Encoding $false))
$updateZip = Join-Path $dist "odx-capture-$Version-update.zip"
New-Zip $updateZip @(Get-ChildItem $updateDir -Recurse -File) $updateDir '' @()

# What the package holds, and where each file came from.
Write-Host ""
Write-Host "$name\"
foreach ($file in @(Get-Item $launcherExe) + @(Get-ChildItem $bin -Recurse -File)) {
    $relative = $file.FullName.Substring($out.Length + 1)
    $origin = if (($rebuilt -contains $file.Name -and $file.DirectoryName -eq $bin) -or
                  ($launcherBuilt -and $file.FullName -eq $launcherExe)) { "built $Version" } else { 'from base' }
    Write-Host ("  {0,-34} {1,12:N0}  {2}" -f $relative, $file.Length, $origin)
}
Write-Host ""
Write-Host "package: $packageZip"
Write-Host "update:  $updateZip"
