using System.ComponentModel;
using System.Diagnostics;

namespace Odx.Agent;

/// <summary>Sets the Windows clock from NTP through the elevated <c>odx clock-sync</c> (a UAC prompt each time).</summary>
public static class ClockSyncTool
{
    /// <returns>False when the user declined the prompt, odx is missing, or the sync failed (its own stderr says why).</returns>
    public static async Task<bool> RunElevatedAsync(string odxPath, string ntpServer)
    {
        Process process;
        try
        {
            process = Process.Start(new ProcessStartInfo(odxPath, $"clock-sync --server \"{ntpServer}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            }) ?? throw new Win32Exception("odx did not start");
        }
        catch (Win32Exception)
        {
            return false;  // UAC declined (1223) or odx missing
        }
        using (process)
        {
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
    }
}
