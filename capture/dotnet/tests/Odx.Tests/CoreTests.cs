using System.Buffers.Binary;
using System.Text;
using Odx.Core;
using Xunit;

namespace Odx.Tests;

public class InputLogTests
{
    [Fact]
    public void Writer_output_matches_shared_fixture()
    {
        var sw = new StringWriter();
        using (var log = new InputLogWriter(sw))
        {
            log.Write(new InputEvent(1000.5, InputKind.KeyDown, "W"));
            log.Write(new InputEvent(1001.25, InputKind.MouseMove, "", 3, -1));
            log.Write(new InputEvent(1010, InputKind.ButtonDown, "Left"));
            log.Write(new InputEvent(1020, InputKind.KeyUp, "W"));
            log.Write(new InputEvent(1030, InputKind.ButtonUp, "Left"));
            log.Flush();
            var expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "input_events.csv"));
            Assert.Equal(expected, sw.ToString());
        }
    }

    [Fact]
    public void Clock_samples_have_header_and_full_precision()
    {
        var sw = new StringWriter();
        var clock = new ClockSampleWriter(sw);
        clock.Write(0.1 + 0.2, 1_790_000_000_000);
        Assert.Equal("t_qpc_ms,utc_unix_ms\n0.30000000000000004,1790000000000\n", sw.ToString());
    }
}

public class KeyNamesTests
{
    [Theory]
    [InlineData('W', "W")]
    [InlineData('7', "7")]
    [InlineData(0x70, "F1")]
    [InlineData(0x87, "F24")]
    [InlineData(0x65, "Num5")]
    [InlineData(0x20, "Space")]
    [InlineData(0x25, "ArrowLeft")]
    [InlineData(0xBC, "Comma")]
    [InlineData(0xE7, "VK_E7")]
    public void Maps_virtual_keys_to_stable_names(int vk, string name) => Assert.Equal(name, KeyNames.FromVirtualKey(vk));

    [Fact]
    public void Names_never_contain_delimiters()
    {
        for (int vk = 0; vk < 256; vk++)
        {
            var name = KeyNames.FromVirtualKey(vk);
            Assert.DoesNotContain(",", name);
            Assert.DoesNotContain("|", name);
        }
    }
}

public class RawInputParserTests
{
    private const int Ptr = 8;  // x64

    private delegate void Fill(Span<byte> data);

    private static byte[] Raw(int type, Fill fill)
    {
        var b = new byte[RawInputParser.HeaderSize(Ptr) + 24];
        BinaryPrimitives.WriteInt32LittleEndian(b, type);
        fill(b.AsSpan(RawInputParser.HeaderSize(Ptr)));
        return b;
    }

    [Fact]
    public void Keyboard_make_and_break()
    {
        var down = Raw(RawInputParser.TypeKeyboard, d => BinaryPrimitives.WriteUInt16LittleEndian(d[6..], 'W'));
        var up = Raw(RawInputParser.TypeKeyboard, d =>
        {
            BinaryPrimitives.WriteUInt16LittleEndian(d[2..], 0x01);  // RI_KEY_BREAK
            BinaryPrimitives.WriteUInt16LittleEndian(d[6..], 'W');
        });
        Assert.Equal([new InputEvent(5, InputKind.KeyDown, "W")], RawInputParser.Parse(down, Ptr, 5));
        Assert.Equal([new InputEvent(6, InputKind.KeyUp, "W")], RawInputParser.Parse(up, Ptr, 6));
    }

    [Theory]
    [InlineData(0x00, "Enter")]
    [InlineData(0x02, "NumEnter")]  // RI_KEY_E0
    public void Numpad_enter_is_told_from_enter_by_the_e0_flag(ushort flags, string name)
    {
        var raw = Raw(RawInputParser.TypeKeyboard, d =>
        {
            BinaryPrimitives.WriteUInt16LittleEndian(d[2..], flags);
            BinaryPrimitives.WriteUInt16LittleEndian(d[6..], 0x0D);
        });
        Assert.Equal([new InputEvent(0, InputKind.KeyDown, name)], RawInputParser.Parse(raw, Ptr, 0));
    }

    [Fact]
    public void Fake_virtual_key_is_ignored()
    {
        var fake = Raw(RawInputParser.TypeKeyboard, d => BinaryPrimitives.WriteUInt16LittleEndian(d[6..], 0xFF));
        Assert.Empty(RawInputParser.Parse(fake, Ptr, 0));
    }

    [Fact]
    public void Mouse_relative_move_and_buttons()
    {
        var raw = Raw(RawInputParser.TypeMouse, d =>
        {
            BinaryPrimitives.WriteUInt16LittleEndian(d[4..], 0x0001 | 0x0008);  // left down, right up
            BinaryPrimitives.WriteInt32LittleEndian(d[12..], 4);
            BinaryPrimitives.WriteInt32LittleEndian(d[16..], -2);
        });
        Assert.Equal(
            [
                new InputEvent(1, InputKind.MouseMove, "", 4, -2),
                new InputEvent(1, InputKind.ButtonDown, "Left"),
                new InputEvent(1, InputKind.ButtonUp, "Right"),
            ],
            RawInputParser.Parse(raw, Ptr, 1));
    }

    [Fact]
    public void Absolute_mouse_moves_are_not_deltas()
    {
        var raw = Raw(RawInputParser.TypeMouse, d =>
        {
            BinaryPrimitives.WriteUInt16LittleEndian(d, 0x01);  // MOUSE_MOVE_ABSOLUTE
            BinaryPrimitives.WriteInt32LittleEndian(d[12..], 30000);
        });
        Assert.Empty(RawInputParser.Parse(raw, Ptr, 0));
    }
}

public class CaptureConfigTests
{
    private static string Dir(string? json)
    {
        string dir = Path.Combine(Path.GetTempPath(), "odx-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        if (json is not null) File.WriteAllText(Path.Combine(dir, CaptureConfig.FileName), json);
        return dir;
    }

    [Fact]
    public void Missing_file_or_empty_value_leaves_the_time_server_unset()
    {
        Assert.Null(CaptureConfig.Load(Dir(null)).TimeServer);
        Assert.Null(CaptureConfig.Load(Dir("""{"time_server": " "}""")).TimeServer);
    }

    [Fact]
    public void Reads_the_time_server() =>
        Assert.Equal("10.0.0.5:12300", CaptureConfig.Load(Dir("""{"time_server": "10.0.0.5:12300"}""")).TimeServer);

    [Fact]
    public void Reads_the_orchestrator_address()
    {
        Assert.Equal(new Uri("http://10.0.0.5:5180"),
            CaptureConfig.Load(Dir("""{"orchestrator": "http://10.0.0.5:5180"}""")).Orchestrator);
        Assert.Null(CaptureConfig.Load(Dir("""{"orchestrator": ""}""")).Orchestrator);
        Assert.Throws<InvalidDataException>(() => CaptureConfig.Load(Dir("""{"orchestrator": "10.0.0.5:5180"}""")));
    }

    [Fact]
    public void Reads_the_client_format_names_else_placeholders()
    {
        var c = CaptureConfig.Load(Dir("""{"vendor_name": "Acme", "frames_bin_magic": "X-MP", "frames_bin_record_type": "x.v1.R"}"""));
        Assert.Equal(("Acme", "X-MP", "x.v1.R"), (c.VendorName, c.FramesBinMagic, c.FramesBinRecordType));
        var d = CaptureConfig.Load(Dir("""{"vendor_name": ""}"""));
        Assert.Equal(("Vendor", "CLIENT-MP", "client.mp.v1.FrameRecord"), (d.VendorName, d.FramesBinMagic, d.FramesBinRecordType));
    }

    [Fact]
    public void Invalid_file_is_reported() =>
        Assert.Throws<InvalidDataException>(() => CaptureConfig.Load(Dir("{ time_server")));
}

public class ClockCheckTests
{
    [Fact]
    public void Within_50_ms_of_ntp_passes_layer_1_and_the_file_round_trips()
    {
        var check = new ClockCheck("ntp.test", 50, 12.5, null, "orch:12300", -6507.25, 1, null, 0.5, 1_791_108_000_000, 1_791_108_000_500);
        Assert.True(check.Ok);
        Assert.False((check with { NtpOffsetMs = -50.1 }).Ok);
        Assert.False((check with { NtpOffsetMs = null, NtpError = "timed out" }).Ok);
        string path = Path.Combine(Path.GetTempPath(), $"odx-clock-{Guid.NewGuid():N}.json");
        try
        {
            check.Save(path);
            Assert.Contains("\"ntp_offset_ms\": 50", File.ReadAllText(path));
            Assert.Equal(check, ClockCheck.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
