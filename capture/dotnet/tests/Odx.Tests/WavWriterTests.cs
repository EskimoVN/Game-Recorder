using System.Buffers.Binary;
using Odx.Core;
using Xunit;

namespace Odx.Tests;

public class WavWriterTests
{
    private static byte[] Sample()
    {
        var data = new byte[480 * 8];
        for (int i = 0; i < 480; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i * 8), i / 480f);
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i * 8 + 4), -i / 480f);
        }
        return data;
    }

    [Fact]
    public void Writes_the_pinned_float_wav_byte_for_byte()
    {
        var stream = new MemoryStream();
        using (var wav = new WavWriter(stream, 48000, 2, 32, isFloat: true)) wav.Write(Sample());
        Assert.Equal(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "audio.wav")), stream.ToArray());
    }

    [Fact]
    public void Carries_streaming_sizes_until_closed()
    {
        var stream = new MemoryStream();
        var wav = new WavWriter(stream, 48000, 2, 32, isFloat: true);
        wav.Write(Sample());
        byte[] open = stream.ToArray();
        Assert.Equal(0xFFFFFFFF, BinaryPrimitives.ReadUInt32LittleEndian(open.AsSpan(4)));
        Assert.Equal(0xFFFFFFFF, BinaryPrimitives.ReadUInt32LittleEndian(open.AsSpan(42)));
        wav.Dispose();
        byte[] closed = stream.ToArray();
        Assert.Equal((uint)(closed.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(closed.AsSpan(4)));
        Assert.Equal(480u * 8, BinaryPrimitives.ReadUInt32LittleEndian(closed.AsSpan(42)));
    }

    [Fact]
    public void Pcm16_has_a_44_byte_header()
    {
        var stream = new MemoryStream();
        using (var wav = new WavWriter(stream, 44100, 2, 16, isFloat: false)) wav.Write(new byte[4]);
        byte[] bytes = stream.ToArray();
        Assert.Equal(48, bytes.Length);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(20)));
        Assert.Equal("data"u8.ToArray(), bytes[36..40]);
        Assert.Throws<ArgumentException>(() => new WavWriter(new MemoryStream(), 48000, 2, 24, isFloat: false));
    }
}
