using Odx.Capture;
using Xunit;

namespace Odx.Tests;

public class RecordingGateTests
{
    [Theory]
    [InlineData(1920, 1080, true)]
    [InlineData(1600, 900, true)]
    [InlineData(1280, 720, true)]
    [InlineData(2560, 1440, true)]
    [InlineData(1600, 866, false)]
    [InlineData(1278, 720, false)]
    [InlineData(1920, 1078, false)]
    [InlineData(2560, 1080, false)]
    [InlineData(0, 0, false)]
    public void Only_an_exact_16_by_9_frame_passes(int width, int height, bool passes) =>
        Assert.Equal(passes, RecordingGate.Is16By9(width, height));

    [Fact]
    public void Window_process_comes_from_a_window_source_only()
    {
        Assert.Equal("arma3_x64", RecordingGate.WindowProcess("window:arma3_x64"));
        Assert.Null(RecordingGate.WindowProcess("monitor:primary"));
    }

    [Fact]
    public void The_last_keys_line_of_the_rpt_decides()
    {
        string rpt = "10:00:01 \"ESKIMO,keys,bad,Reload;Throw\"\r\n10:00:05 \"ESKIMO,on,server x\"\r\n" +
                     "10:00:31 \"ESKIMO,keys,bad,Reload\"\r\n";
        Assert.Equal(new[] { "Reload" }, RecordingGate.Arma3ReboundActions(rpt));
        Assert.Null(RecordingGate.Arma3ReboundActions(rpt + "10:01:01 \"ESKIMO,keys,ok\"\r\n"));
    }

    [Fact]
    public void No_keys_line_blocks_nobody() =>
        Assert.Null(RecordingGate.Arma3ReboundActions("10:00:01 \"ESKIMO,on,server x\"\r\n"));

    [Fact]
    public void Reforger_rebinds_are_keyboard_or_mouse_inputs()
    {
        Assert.True(RecordingGate.ReforgerHasReboundKeys("ActionBinding Reload { Input \"keyboard:KC_T\" }"));
        Assert.True(RecordingGate.ReforgerHasReboundKeys("Input \"mouse:button4\""));
        Assert.False(RecordingGate.ReforgerHasReboundKeys("Sensitivity 1.2"));
    }
}
