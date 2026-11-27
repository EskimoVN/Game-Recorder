#!/usr/bin/env bash
# Builds the minimal static ffmpeg.exe that `odx record` needs, in an MSYS2 UCRT64 shell:
#   raw NV12 frames on a pipe -> libx264, h264_nvenc or h264_mf (Media Foundation: Intel Quick Sync, AMD) -> MP4 file;
#   --obs: OBS's recording (MP4, hybrid/fragmented MP4, MOV, MKV, FLV or MPEG-TS) remuxed into an MP4, streams copied.
# Everything else (other codecs, demuxers, filters, network, ffprobe, ffplay) is left out: ~100 MB -> a few MB.
#
#   bash build-ffmpeg.sh <out_dir>
#
# Needs: pacman -S make diffutils mingw-w64-ucrt-x86_64-{gcc,nasm,pkgconf}. publish.ps1 runs this once and caches
# the result. Writes <out_dir>/ffmpeg.exe, LICENSE.txt (GPLv2: x264 is GPL) and SOURCES.txt (what was built, for the
# GPL source offer).
set -euo pipefail

FFMPEG_VERSION=9.0.2
FFMPEG_URL=https://ffmpeg.org/releases/ffmpeg-$FFMPEG_VERSION.tar.xz
FFMPEG_SHA256=8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e
X264_COMMIT=b35605ace3ddf7c1a5d67a2eb553f034aef41d55  # stable branch
X264_URL=https://code.videolan.org/videolan/x264/-/archive/$X264_COMMIT/x264-$X264_COMMIT.tar.bz2
X264_SHA256=6eeb82934e69fd51e043bd8c5b0d152839638d1ce7aa4eea65a3fedcf83ff224
# 11.1 headers: NVENC then needs NVIDIA driver 471.41+ instead of 610+ with the newest headers.
NVHEADERS_VERSION=n11.1.5.4
NVHEADERS_URL=https://github.com/FFmpeg/nv-codec-headers/archive/refs/tags/$NVHEADERS_VERSION.tar.gz
NVHEADERS_SHA256=cbad7c68365ae50b03fe4cfbea05975c94406bdcc0a995bd094a3ea355656ffb

OUT=$(realpath -m "${1:?usage: build-ffmpeg.sh <out_dir>}")
# A fixed work dir, independent of where the repo is checked out: ffmpeg embeds its configure line (with this prefix)
# in the binary, so the build is the same on every machine and leaks no local path.
WORK=/tmp/odx-ffmpeg-build
PREFIX=$WORK/prefix
JOBS=$(nproc)
mkdir -p "$OUT" "$WORK" "$PREFIX"
cd "$WORK"

fetch() {  # url sha256 file
    [ -f "$3" ] || curl -fsSL --retry 5 --retry-all-errors --retry-delay 3 -o "$3" "$1"
    echo "$2  $3" | sha256sum -c --quiet -
}

fetch "$X264_URL" "$X264_SHA256" x264.tar.bz2
fetch "$NVHEADERS_URL" "$NVHEADERS_SHA256" nv-codec-headers.tar.gz
fetch "$FFMPEG_URL" "$FFMPEG_SHA256" ffmpeg.tar.xz

rm -rf x264 nv-codec-headers ffmpeg
mkdir x264 nv-codec-headers ffmpeg
tar -xjf x264.tar.bz2 -C x264 --strip-components=1
tar -xzf nv-codec-headers.tar.gz -C nv-codec-headers --strip-components=1
tar -xJf ffmpeg.tar.xz -C ffmpeg --strip-components=1

(cd x264 && ./configure --prefix="$PREFIX" --enable-static --disable-cli --disable-opencl --disable-lavf \
    --disable-swscale --disable-ffms --disable-gpac --disable-lsmash && make -j"$JOBS" && make install)
(cd nv-codec-headers && make install PREFIX="$PREFIX")

CONFIGURE=(
    --prefix="$PREFIX" --pkg-config-flags=--static
    --extra-cflags="-I$PREFIX/include" --extra-ldflags="-L$PREFIX/lib -static"
    --disable-everything --disable-autodetect --disable-doc --disable-debug --disable-network
    --disable-ffplay --disable-ffprobe
    --enable-gpl --enable-libx264 --enable-ffnvcodec --enable-nvenc --enable-mediafoundation --enable-d3d11va  # mfenc.c needs d3d11va
    --enable-encoder=libx264,h264_nvenc,h264_mf,aac
    --enable-demuxer=rawvideo,pcm_f32le,pcm_s16le,wav --enable-decoder=rawvideo,pcm_f32le,pcm_s16le  # wav: audio.wav (VideoFinalize)
    --enable-demuxer=mov,matroska,flv,mpegts --enable-parser=h264,hevc,av1,aac,opus --enable-bsf=aac_adtstoasc
    --enable-decoder=aac  # MPEG-TS gives AAC's sample rate only once a frame is decoded
    --enable-muxer=mp4 --enable-protocol=file,pipe,fd  # ffmpeg 9 reads '-i -' through fd:, not pipe:
    --enable-filter=buffer,buffersink,crop,scale,format,null,fps,abuffer,abuffersink,aformat,aresample,anull
)
(cd ffmpeg && PKG_CONFIG_PATH="$PREFIX/lib/pkgconfig" ./configure "${CONFIGURE[@]}" && make -j"$JOBS")

strip -o "$OUT/ffmpeg.exe" ffmpeg/ffmpeg.exe
cp ffmpeg/COPYING.GPLv2 "$OUT/LICENSE.txt"
{
    echo "ffmpeg.exe for odx record, built by odx-capture/capture-cs/ffmpeg/build-ffmpeg.sh."
    echo "GPLv2 (FFmpeg with --enable-gpl, x264). Sources:"
    echo "  FFmpeg $FFMPEG_VERSION            $FFMPEG_URL  sha256 $FFMPEG_SHA256"
    echo "  x264 $X264_COMMIT  $X264_URL  sha256 $X264_SHA256"
    echo "  nv-codec-headers $NVHEADERS_VERSION   $NVHEADERS_URL  sha256 $NVHEADERS_SHA256"
    echo "FFmpeg configure: ${CONFIGURE[*]//$PREFIX/<prefix>}"
} > "$OUT/SOURCES.txt"
echo "built $OUT/ffmpeg.exe ($(du -h "$OUT/ffmpeg.exe" | cut -f1))"
