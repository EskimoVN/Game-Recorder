using System.Buffers.Binary;

namespace Odx.Core;

/// <summary>
/// Writes a WAV file: 16-bit PCM or 32-bit IEEE float samples after a 44-byte (PCM) or 46-byte (float, with cbSize)
/// header. The header first carries the streaming sizes (0xFFFFFFFF), so a file whose writer was killed is still read to
/// its end by ffmpeg; <see cref="Dispose"/> patches the real sizes. Written by odx record (raw/audio.wav), muxed into
/// video.mp4 by <see cref="VideoFinalize"/>. Pinned by dotnet/fixtures/audio.wav.
/// </summary>
public sealed class WavWriter : IDisposable
{
    private const uint Streaming = 0xFFFFFFFF;

    private readonly Stream _stream;
    private readonly int _headerSize;
    private bool _disposed;

    public long DataBytes { get; private set; }

    /// <exception cref="ArgumentException">Not 16-bit PCM or 32-bit float.</exception>
    public WavWriter(Stream stream, int sampleRate, int channels, int bitsPerSample, bool isFloat)
    {
        if (!(isFloat ? bitsPerSample == 32 : bitsPerSample == 16))
            throw new ArgumentException($"WAV: {bitsPerSample}-bit {(isFloat ? "float" : "PCM")} is not supported (16-bit PCM or 32-bit float)");
        _stream = stream;
        int fmtSize = isFloat ? 18 : 16;
        _headerSize = 20 + fmtSize + 8;
        var header = new byte[_headerSize];
        var h = header.AsSpan();
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], Streaming);
        "WAVE"u8.CopyTo(h[8..]);
        "fmt "u8.CopyTo(h[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], (uint)fmtSize);
        BinaryPrimitives.WriteUInt16LittleEndian(h[20..], (ushort)(isFloat ? 3 : 1));  // WAVE_FORMAT_IEEE_FLOAT / WAVE_FORMAT_PCM
        BinaryPrimitives.WriteUInt16LittleEndian(h[22..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], (uint)sampleRate);
        int blockAlign = channels * bitsPerSample / 8;
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], (uint)(sampleRate * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(h[34..], (ushort)bitsPerSample);
        if (isFloat) BinaryPrimitives.WriteUInt16LittleEndian(h[36..], 0);  // cbSize
        "data"u8.CopyTo(h[(20 + fmtSize)..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[(24 + fmtSize)..], Streaming);
        _stream.Write(header);
    }

    public void Write(ReadOnlySpan<byte> samples)
    {
        _stream.Write(samples);
        DataBytes += samples.Length;
    }

    /// <summary>Patches the sizes (when the stream seeks and the file is under 4 GB) and closes the stream.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_stream.CanSeek && _headerSize - 8 + DataBytes <= Streaming)
            {
                Span<byte> size = stackalloc byte[4];
                _stream.Position = 4;
                BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)(_headerSize - 8 + DataBytes));
                _stream.Write(size);
                _stream.Position = _headerSize - 4;
                BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)DataBytes);
                _stream.Write(size);
            }
        }
        finally
        {
            _stream.Dispose();
        }
    }
}
