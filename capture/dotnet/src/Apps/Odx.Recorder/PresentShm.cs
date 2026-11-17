using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;

namespace Odx.Recorder;

/// <summary>
/// The C# side of the shared-memory contract odx_capture_x64.dll writes NV12 frames into (research/arma3-present-capture
/// §1, pinned by odx/present_shm.hpp and PresentShmTests). <c>odx record</c> creates the mapping and owns the control
/// fields (capture, fps, origin); the DLL opens it, writes the slots and bumps <see cref="WriteSeq"/>. The layout is
/// fixed and must match the C++ header byte for byte.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class PresentMapping : IDisposable
{
    public const uint Version = 1;
    public const int SlotCount = 4;
    public const int MaxWidth = 3840;
    public const int MaxHeight = 2160;
    public const int MaxNv12Bytes = MaxWidth * MaxHeight * 3 / 2;  // 12,441,600

    public const string NamePrefix = @"Local\OdxCapture_";
    public const string EventPrefix = @"Local\OdxCaptureFrame_";

    // Header field offsets (odx/present_shm.hpp).
    private const int OffVersion = 0, OffCapture = 4, OffFps = 8, OffSlotCount = 12, OffOrigin = 16;
    private const int OffWidth = 24, OffHeight = 28, OffWriteSeq = 32;
    public const int HeaderSize = 40;
    private const int SlotSeq = 0, SlotPresent = 8, SlotHeaderSize = 16;
    public const long SlotStride = SlotHeaderSize + MaxNv12Bytes;
    public const long MappingSize = HeaderSize + (long)SlotCount * SlotStride;

    public static string Name(int pid) => NamePrefix + pid;
    public static string EventName(int pid) => EventPrefix + pid;

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;

    private PresentMapping(MemoryMappedFile mmf)
    {
        _mmf = mmf;
        _view = mmf.CreateViewAccessor(0, MappingSize, MemoryMappedFileAccess.ReadWrite);
    }

    /// <summary>Creates <c>Local\OdxCapture_&lt;pid&gt;</c> and writes the control fields; the DLL opens it to write frames.</summary>
    public static PresentMapping Create(int pid, int fps, double originMs)
    {
        var mmf = MemoryMappedFile.CreateNew(Name(pid), MappingSize, MemoryMappedFileAccess.ReadWrite);
        var m = new PresentMapping(mmf);
        m._view.Write(OffVersion, Version);
        m._view.Write(OffCapture, 0u);
        m._view.Write(OffFps, (uint)fps);
        m._view.Write(OffSlotCount, (uint)SlotCount);
        m._view.Write(OffOrigin, originMs);
        m._view.Write(OffWidth, 0u);
        m._view.Write(OffHeight, 0u);
        m._view.Write(OffWriteSeq, 0u);
        return m;
    }

    /// <summary>Opens an existing mapping (the DLL side in a test). Null if it does not exist.</summary>
    public static PresentMapping? OpenExisting(int pid)
    {
        try
        {
            return new PresentMapping(MemoryMappedFile.OpenExisting(Name(pid), MemoryMappedFileRights.ReadWrite));
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    public uint Capture
    {
        get => _view.ReadUInt32(OffCapture);
        set => _view.Write(OffCapture, value);
    }
    public int Fps => (int)_view.ReadUInt32(OffFps);
    public double OriginMs => _view.ReadDouble(OffOrigin);
    public int Width => (int)_view.ReadUInt32(OffWidth);
    public int Height => (int)_view.ReadUInt32(OffHeight);
    public uint WriteSeq => _view.ReadUInt32(OffWriteSeq);

    /// <summary>Bytes of one NV12 frame at the size the DLL last reported (0 until the first frame).</summary>
    public int FrameBytes => Width * Height * 3 / 2;

    /// <summary>
    /// Copies the newest frame into <paramref name="dst"/> with its Present QPC ms. False when no frame has been written
    /// yet, the size is out of range, or the slot was overwritten mid-read (a torn read, retried by the caller).
    /// </summary>
    public bool TryReadNewest(byte[] dst, out double presentMs)
    {
        presentMs = 0;
        uint seq = _view.ReadUInt32(OffWriteSeq);
        if (seq == 0) return false;
        int width = Width, height = Height;
        int bytes = width * height * 3 / 2;
        if (width <= 0 || height <= 0 || width > MaxWidth || height > MaxHeight || bytes > dst.Length) return false;
        long slot = HeaderSize + (long)((seq - 1) % SlotCount) * SlotStride;
        if (_view.ReadUInt64(slot + SlotSeq) != seq) return false;  // already being overwritten
        presentMs = _view.ReadDouble(slot + SlotPresent);
        _view.ReadArray(slot + SlotHeaderSize, dst, 0, bytes);
        return _view.ReadUInt64(slot + SlotSeq) == seq;  // still the same frame after the copy
    }

    /// <summary>The DLL's write, used by tests to feed synthetic frames: fills the next slot, then publishes it.</summary>
    public void WriteSlot(double presentMs, ReadOnlySpan<byte> nv12, int width, int height)
    {
        uint seq = _view.ReadUInt32(OffWriteSeq) + 1;
        long slot = HeaderSize + (long)((seq - 1) % SlotCount) * SlotStride;
        _view.Write(OffWidth, (uint)width);
        _view.Write(OffHeight, (uint)height);
        _view.WriteArray(slot + SlotHeaderSize, nv12.ToArray(), 0, nv12.Length);
        _view.Write(slot + SlotPresent, presentMs);
        _view.Write(slot + SlotSeq, (ulong)seq);  // slot complete
        _view.Write(OffWriteSeq, seq);             // publish
    }

    public void Dispose()
    {
        _view.Dispose();
        _mmf.Dispose();
    }
}
