// odx clock-sync --server <ntp host[:port]>
// Puts the Windows clock within ClockCheck.MaxOffsetMs of NTP (spec v3 §4 layer 1), as administrator: first through the
// Windows Time service (w32tm /resync), and when that fails or leaves the clock off (the service is often disabled on
// gaming PCs) by setting the clock to an SNTP measurement of the given server. odx-app starts it with a UAC prompt.
#if WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;
using Odx.Agent;
using Odx.Core;

namespace Odx.Cli;

public static class ClockSyncCommand
{
    public static int Run(string[] args)
    {
        string? server = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--server" && i + 1 < args.Length) server = args[++i];
            else { server = null; break; }
        }
        if (server is null)
        {
            Console.Error.WriteLine("usage: odx clock-sync --server <ntp host[:port]>");
            return 2;
        }
        if (!PktmonCapture.IsElevated)
        {
            Console.Error.WriteLine("odx clock-sync needs administrator rights (it sets the Windows clock)");
            return 1;
        }

        double? before = Offset(server);
        Console.WriteLine($"Windows clock against {server}: {Text(before)}");
        if (before is double b && Math.Abs(b) <= ClockCheck.MaxOffsetMs) return 0;

        if (W32tmResync() && Offset(server) is double afterW32tm && Math.Abs(afterW32tm) <= ClockCheck.MaxOffsetMs)
        {
            Console.WriteLine($"w32tm /resync: now {afterW32tm:+0.0;-0.0} ms");
            return 0;
        }
        for (int attempt = 0; attempt < 2; attempt++)
        {
            NtpSample sample;
            try
            {
                sample = SntpClient.Query(server, TimeSpan.FromSeconds(2));
            }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or InvalidDataException)
            {
                Console.Error.WriteLine($"{server}: {e.Message}");
                return 1;
            }
            var now = DateTime.UtcNow.AddMilliseconds(sample.SystemOffsetMs);
            var time = new SystemTime
            {
                Year = (ushort)now.Year, Month = (ushort)now.Month, DayOfWeek = (ushort)now.DayOfWeek, Day = (ushort)now.Day,
                Hour = (ushort)now.Hour, Minute = (ushort)now.Minute, Second = (ushort)now.Second, Milliseconds = (ushort)now.Millisecond,
            };
            if (!SetSystemTime(ref time))
            {
                Console.Error.WriteLine($"SetSystemTime failed: {Marshal.GetLastPInvokeErrorMessage()}");
                return 1;
            }
            double? after = Offset(server);
            Console.WriteLine($"clock set from {server}: now {Text(after)}");
            if (after is double a && Math.Abs(a) <= ClockCheck.MaxOffsetMs) return 0;
        }
        Console.Error.WriteLine("the clock is still off after setting it (unstable network, or another program sets the clock)");
        return 1;
    }

    private static string Text(double? offset) => offset is double o ? $"{o:+0.0;-0.0} ms (NTP - Windows)" : "no answer";

    /// <summary>The lowest-RTT sample of a few, as <see cref="ClockCheck.Measure"/> takes it.</summary>
    private static double? Offset(string server) => ClockCheck.Measure(server, null).NtpOffsetMs;

    private static bool W32tmResync()
    {
        try
        {
            using var w32tm = Process.Start(new ProcessStartInfo("w32tm", "/resync")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (w32tm is null) return false;
            string output = w32tm.StandardOutput.ReadToEnd() + w32tm.StandardError.ReadToEnd();
            if (!w32tm.WaitForExit(TimeSpan.FromSeconds(30))) { w32tm.Kill(); return false; }
            Console.WriteLine($"w32tm /resync: exit {w32tm.ExitCode} {output.Trim()}");
            return w32tm.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetSystemTime(ref SystemTime time);
}
#endif
