using System.Buffers.Binary;

namespace Odx.Core;

/// <param name="Frames">Samples (= frames for H.264 without B-frame reordering) in the first video track.</param>
/// <param name="DurationSeconds">Movie duration from mvhd.</param>
/// <param name="HasAudio">The file has a sound track (hdlr "soun").</param>
public sealed record Mp4Info(int Width, int Height, long Frames, double DurationSeconds, bool HasAudio = false);

/// <summary>
/// Reads what the validator needs from an MP4 (ISO BMFF) file without ffprobe: walks the box tree to the movie header and
/// the first video track's sample description and sample table. Only the moov box is read; mdat is skipped by seeking,
/// so multi-GB recordings cost a few hundred KB of I/O. Fragmented MP4 (moof) is not supported; odx record never writes it.
/// </summary>
public static class Mp4Reader
{
    /// <exception cref="InvalidDataException">Not an MP4 this reader understands.</exception>
    public static Mp4Info Read(string path)
    {
        using var file = File.OpenRead(path);
        byte[]? moov = null;
        bool fragmented = false;
        while (file.Position < file.Length && moov is null)
        {
            var (type, headerSize, size) = ReadHeader(file, file.Length - file.Position);
            long bodySize = size - headerSize;
            if (type == "moov")
            {
                if (bodySize > int.MaxValue) throw new InvalidDataException("moov box too large");
                moov = new byte[bodySize];
                file.ReadExactly(moov);
            }
            else
            {
                fragmented |= type == "moof";
                file.Seek(bodySize, SeekOrigin.Current);
            }
        }
        if (moov is null) throw new InvalidDataException(fragmented ? "fragmented MP4 is not supported" : "no moov box");

        var mvhd = Child(moov, "mvhd") ?? throw new InvalidDataException("no mvhd box");
        double duration = Duration(mvhd);
        Mp4Info? video = null;
        bool hasAudio = false;
        foreach (var trak in Children(moov, "trak"))
        {
            var mdia = Child(trak, "mdia");
            var hdlr = mdia is null ? null : Child(mdia.Value, "hdlr");
            if (hdlr is null || hdlr.Value.Length < 12) continue;
            string handler = Type(hdlr.Value.Span[8..12]);
            hasAudio |= handler == "soun";
            if (handler != "vide" || video is not null) continue;
            var stbl = Child(mdia!.Value, "minf") is { } minf ? Child(minf, "stbl") : null;
            var stsd = stbl is null ? null : Child(stbl.Value, "stsd");
            var stsz = stbl is null ? null : Child(stbl.Value, "stsz");
            if (stsd is null || stsz is null) throw new InvalidDataException("video track without stsd/stsz");
            // stsd: version/flags(4) entry_count(4), then a VisualSampleEntry: size(4) type(4) reserved(6)
            // data_reference_index(2) pre_defined(2) reserved(2) pre_defined(12) width(2) height(2).
            var entry = stsd.Value.Span[8..];
            if (entry.Length < 36) throw new InvalidDataException("short video sample entry");
            int width = BinaryPrimitives.ReadUInt16BigEndian(entry[32..]), height = BinaryPrimitives.ReadUInt16BigEndian(entry[34..]);
            // stsz: version/flags(4) sample_size(4) sample_count(4).
            if (stsz.Value.Length < 12) throw new InvalidDataException("short stsz box");
            long frames = BinaryPrimitives.ReadUInt32BigEndian(stsz.Value.Span[8..]);
            video = new Mp4Info(width, height, frames, duration);
        }
        return video is null ? throw new InvalidDataException("no video track") : video with { HasAudio = hasAudio };
    }

    private static (string Type, int HeaderSize, long Size) ReadHeader(Stream s, long remaining)
    {
        Span<byte> h = stackalloc byte[16];
        s.ReadExactly(h[..8]);
        long size = BinaryPrimitives.ReadUInt32BigEndian(h);
        string type = Type(h[4..8]);
        int headerSize = 8;
        if (size == 1)
        {
            s.ReadExactly(h[8..16]);
            size = (long)BinaryPrimitives.ReadUInt64BigEndian(h[8..]);
            headerSize = 16;
        }
        else if (size == 0)
        {
            size = remaining;  // box extends to the end of the file
        }
        if (size < headerSize || size > remaining) throw new InvalidDataException($"bad size for box '{type}'");
        return (type, headerSize, size);
    }

    /// <summary>Bodies of the direct children of <paramref name="parent"/> with the given type.</summary>
    private static IEnumerable<ReadOnlyMemory<byte>> Children(ReadOnlyMemory<byte> parent, string type)
    {
        int at = 0;
        while (at + 8 <= parent.Length)
        {
            var span = parent.Span[at..];
            long size = BinaryPrimitives.ReadUInt32BigEndian(span);
            int header = 8;
            if (size == 1)
            {
                if (span.Length < 16) throw new InvalidDataException("truncated box header");
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(span[8..]);
                header = 16;
            }
            else if (size == 0)
            {
                size = span.Length;
            }
            if (size < header || size > span.Length) throw new InvalidDataException("bad box size inside moov");
            if (Type(span[4..8]) == type) yield return parent.Slice(at + header, (int)size - header);
            at += (int)size;
        }
    }

    private static ReadOnlyMemory<byte>? Child(ReadOnlyMemory<byte> parent, string type)
    {
        foreach (var child in Children(parent, type)) return child;
        return null;
    }

    /// <summary>mvhd: version(1) flags(3), then v0 creation(4) modification(4) timescale(4) duration(4),
    /// or v1 creation(8) modification(8) timescale(4) duration(8).</summary>
    private static double Duration(ReadOnlyMemory<byte> mvhd)
    {
        var m = mvhd.Span;
        if (m.Length < 20) throw new InvalidDataException("short mvhd box");
        bool v1 = m[0] == 1;
        if (v1 && m.Length < 32) throw new InvalidDataException("short mvhd box");
        uint timescale = BinaryPrimitives.ReadUInt32BigEndian(m[(v1 ? 20 : 12)..]);
        double duration = v1 ? BinaryPrimitives.ReadUInt64BigEndian(m[24..]) : BinaryPrimitives.ReadUInt32BigEndian(m[16..]);
        if (timescale == 0) throw new InvalidDataException("mvhd timescale is 0");
        return duration / timescale;
    }

    private static string Type(ReadOnlySpan<byte> fourcc) => System.Text.Encoding.ASCII.GetString(fourcc);
}
