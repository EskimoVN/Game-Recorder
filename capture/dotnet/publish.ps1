# Builds a self-contained Windows x64 package: odx-app, the odx command-line tool (input, record, build, validate, agent,
# simulate-game, game-server, merge), the .NET runtime, a minimal ffmpeg and the game profiles (games/). Unzip it on any Windows 10 2004+ x64 machine and run
# "Odx Capture.exe" (or bin\odx.exe); nothing else needs installing. Needs MSYS2 (ffmpeg) and the MSVC build tools
# (NativeAOT launcher).
#
#   powershell -ExecutionPolicy Bypass -File publish.ps1 [-Version 0.1.0] [-Orchestrator https://<domain>] [-AccessKey <key>]
#                                                        [-VendorName <name>] [-FramesBinMagic <magic>]
#                                                        [-FramesBinRecordType <type>] [-WithOrchestrator]
#
# -WithOrchestrator adds odx-orchestrator.exe, for running the orchestrator on Windows (guides/orchestrator.md); players
# never run it, and a deployed orchestrator runs in Docker (deploy/).
# -Orchestrator / -AccessKey preset the package's bin/odx-capture.json for a deployed orchestrator (deploy/up.sh prints
# both), so players only type a room's invite code; without them the repository's odx-capture.json is shipped.
# -VendorName / -FramesBinMagic / -FramesBinRecordType: the client's delivery format names (CaptureConfig), kept out of
# the repository; without them the package writes placeholders.
#
# Output: dist/odx-capture-<version>-win-x64/ (its data\ is kept across rebuilds) and dist/odx-capture-<version>-win-x64.zip
param([string]$Version = "0.1.0", [string]$Orchestrator = "", [string]$AccessKey = "", [string]$VendorName = "",
      [string]$FramesBinMagic = "", [string]$FramesBinRecordType = "", [switch]$WithOrchestrator)
$ErrorActionPreference = 'Stop'

# ffmpeg is built from pinned sources by ffmpeg/build-ffmpeg.sh (minimal: only what odx record uses) in MSYS2 and
# cached under .cache/, keyed by the script's hash so a changed script rebuilds it.
$root = $PSScriptRoot
$name = "odx-capture-$Version-win-x64"
$dist = Join-Path $root 'dist'
$out = Join-Path $dist $name
$cache = Join-Path $root '.cache'

$bin = Join-Path $out 'bin'
$launcherExe = Join-Path $out 'Odx Capture.exe'
# data\ holds what this machine recorded and delivered (PackageFolders): a rebuild replaces the program, never the data.
foreach ($old in @($bin, $launcherExe)) { if (Test-Path $old) { Remove-Item -Recurse -Force $old } }
New-Item -ItemType Directory -Force $out, $bin, $cache, (Join-Path $out 'data\captures'), (Join-Path $out 'data\delivery') | Out-Null
# NativeAOT finds the MSVC linker through vswhere.exe, which is not on PATH by default.
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"

# Layout: "Odx Capture.exe" (launcher) at the root; odx-app.exe, odx.exe (and odx-orchestrator.exe), ffmpeg/ and
# games/ in bin/.
# The launcher is NativeAOT (~2 MB): Windows Defender scans a new executable before its first run (12-27 s for the
# 240 MB odx-app.exe); the launcher is scanned at once and shows the splash meanwhile.
# Single-file: each executable embeds its own .NET runtime and libraries (WPF's native libraries are extracted at first
# run), so the two never share or overwrite files; no .pdb files. Not compressed: decompressing on every start delays
# odx-app's window by ~10 s, and the zip is compressed anyway. ReadyToRun avoids most JIT work at startup (~3 s for a
# cold odx record).
$publishes = @(
    @{ Project = 'Odx.App'; Out = $bin; Framework = $null },
    @{ Project = 'Odx.Cli'; Out = $bin; Framework = 'net10.0-windows10.0.22621.0' }
)
if ($WithOrchestrator) { $publishes += @{ Project = 'Odx.Orchestrator'; Out = $bin; Framework = $null } }
foreach ($p in $publishes) {
    $frameworkArgs = if ($p.Framework) { @('-f', $p.Framework) } else { @() }
    dotnet publish (Join-Path $root "src/Apps/$($p.Project)") -c Release -r win-x64 --self-contained @frameworkArgs `
        -p:PublishReadyToRun=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None -p:Version=$Version -o $p.Out
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $($p.Project) failed" }
}
$launcherOut = Join-Path $cache 'launcher'
dotnet publish (Join-Path $root 'src/Apps/Odx.Launcher') -c Release -r win-x64 -p:DebugType=None -p:Version=$Version -o $launcherOut
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish Odx.Launcher failed' }
Copy-Item (Join-Path $launcherOut 'odx-launcher.exe') (Join-Path $out 'Odx Capture.exe')

$configPath = Join-Path $bin 'odx-capture.json'
$config = Get-Content $configPath -Raw | ConvertFrom-Json
if ($Orchestrator) { $config.orchestrator = $Orchestrator }
if ($AccessKey) { $config | Add-Member -NotePropertyName access_key -NotePropertyValue $AccessKey -Force }
if ($VendorName) { $config | Add-Member -NotePropertyName vendor_name -NotePropertyValue $VendorName -Force }
if ($FramesBinMagic) { $config | Add-Member -NotePropertyName frames_bin_magic -NotePropertyValue $FramesBinMagic -Force }
if ($FramesBinRecordType) {
    $config | Add-Member -NotePropertyName frames_bin_record_type -NotePropertyValue $FramesBinRecordType -Force
}
[IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))

$buildScript = Join-Path $root 'ffmpeg/build-ffmpeg.sh'
$scriptHash = (Get-FileHash $buildScript -Algorithm SHA256).Hash.Substring(0, 12)
$ffmpegBuild = Join-Path $cache "ffmpeg-$scriptHash"
if (-not (Test-Path (Join-Path $ffmpegBuild 'ffmpeg.exe'))) {
    $msys = if ($env:MSYS2_ROOT) { $env:MSYS2_ROOT } else { 'C:\msys64' }
    $bash = Join-Path $msys 'usr\bin\bash.exe'
    if (-not (Test-Path $bash)) {
        throw "ffmpeg is not built yet and MSYS2 was not found at $msys (set MSYS2_ROOT). Install MSYS2, then in it: " +
              "pacman -S make diffutils mingw-w64-ucrt-x86_64-gcc mingw-w64-ucrt-x86_64-nasm mingw-w64-ucrt-x86_64-pkgconf"
    }
    Write-Host "building ffmpeg (once, ~10-20 min) into $ffmpegBuild"
    $toUnix = Join-Path $msys 'usr\bin\cygpath.exe'
    $env:MSYSTEM = 'UCRT64'
    $env:CHERE_INVOKING = '1'
    & $bash -lc "bash '$(& $toUnix -u $buildScript)' '$(& $toUnix -u $ffmpegBuild)'"
    if ($LASTEXITCODE -ne 0) { throw 'ffmpeg build failed' }
}
$ffmpegDir = Join-Path $bin 'ffmpeg'
New-Item -ItemType Directory -Force $ffmpegDir | Out-Null
Copy-Item (Join-Path $ffmpegBuild '*') $ffmpegDir

# Smoke test: odx record encodes raw NV12 into a fragmented MP4, VideoFinalize copies it with a WAV into an MP4, and
# odx record --obs remuxes a recording.
$smoke = Join-Path ([IO.Path]::GetTempPath()) "odx-ffmpeg-smoke-$PID"
New-Item -ItemType Directory -Force $smoke | Out-Null
try {
    $ffmpegExe = Join-Path $ffmpegDir 'ffmpeg.exe'
    [IO.File]::WriteAllBytes("$smoke\frames.nv12", (New-Object byte[] (64 * 64 * 3 / 2 * 10)))
    & $ffmpegExe -v error -y -f rawvideo -pix_fmt nv12 -s 64x64 -framerate 30 -i "$smoke\frames.nv12" -c:v libx264 `
        -movflags +frag_keyframe+empty_moov+default_base_moof -f mp4 "$smoke\a.mp4"
    if ($LASTEXITCODE -ne 0) { throw 'bundled ffmpeg cannot encode raw NV12 to a fragmented MP4 (odx record)' }
    # A 44-byte PCM WAV header + 10 ms of silence, as odx record's audio.wav (16-bit here; the float variant adds cbSize).
    $wav = [byte[]](0x52,0x49,0x46,0x46, 0xA4,0x07,0,0, 0x57,0x41,0x56,0x45, 0x66,0x6D,0x74,0x20, 16,0,0,0, 1,0, 2,0,
        0x80,0xBB,0,0, 0,0xEE,0x02,0, 4,0, 16,0, 0x64,0x61,0x74,0x61, 0x80,0x07,0,0) + (New-Object byte[] 1920)
    [IO.File]::WriteAllBytes("$smoke\a.wav", $wav)
    & $ffmpegExe -v error -y -i "$smoke\a.mp4" -i "$smoke\a.wav" -map 0:v:0 -map 1:a:0 -c:v copy -c:a aac -movflags +faststart -f mp4 "$smoke\b.mp4"
    if ($LASTEXITCODE -ne 0) { throw 'bundled ffmpeg cannot finalize a fragmented MP4 with a WAV (VideoFinalize)' }
    & $ffmpegExe -v error -y -i "$smoke\b.mp4" -map 0:v:0 -c copy -movflags +faststart "$smoke\c.mp4"
    if ($LASTEXITCODE -ne 0) { throw 'bundled ffmpeg cannot remux an MP4 (odx record --obs)' }
}
finally {
    Remove-Item -Recurse -Force $smoke
}

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$packageZip = Join-Path $dist "$name.zip"
if (Test-Path $packageZip) { Remove-Item -Force $packageZip }
# The program and empty data folders only: the data\ of this machine stays out of the package.
$zip = [IO.Compression.ZipFile]::Open($packageZip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in @(Get-Item $launcherExe) + @(Get-ChildItem $bin -Recurse -File)) {
        $entry = "$name/" + $file.FullName.Substring($out.Length + 1).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $entry,
            [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    foreach ($folder in 'data/captures/', 'data/delivery/') { $zip.CreateEntry("$name/$folder") | Out-Null }
}
finally { $zip.Dispose() }
Write-Host "package: $packageZip"
