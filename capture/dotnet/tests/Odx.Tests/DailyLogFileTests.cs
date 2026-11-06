using Odx.Core;
using Xunit;

namespace Odx.Tests;

public class DailyLogFileTests
{
    [Fact]
    public void Appends_stamped_lines_to_todays_file_and_drops_files_older_than_two_weeks()
    {
        string dir = Path.Combine(Path.GetTempPath(), "odx-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string old = Path.Combine(dir, DailyLogFile.FileName(DateTime.Now.AddDays(-DailyLogFile.KeepDays - 1)));
        string recent = Path.Combine(dir, DailyLogFile.FileName(DateTime.Now.AddDays(-1)));
        string other = Path.Combine(dir, "notes.log");
        File.WriteAllText(old, "x");
        File.WriteAllText(recent, "x");
        File.WriteAllText(other, "x");
        try
        {
            var log = new DailyLogFile(dir);
            log.Write("in room ABCDEF");
            log.Write("stop ABCDEF-20261106-020054-r01 (round_end)");

            Assert.False(File.Exists(old));
            Assert.True(File.Exists(recent));
            Assert.True(File.Exists(other));
            string[] lines = File.ReadAllLines(Path.Combine(dir, DailyLogFile.FileName(DateTime.Now)));
            Assert.Equal(2, lines.Length);
            Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} in room ABCDEF$", lines[0]);
            Assert.EndsWith(" stop ABCDEF-20261106-020054-r01 (round_end)", lines[1]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void A_folder_that_cannot_be_made_is_no_error()
    {
        string file = Path.Combine(Path.GetTempPath(), "odx-log-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "a file where the folder should be");
        try
        {
            new DailyLogFile(file).Write("lost");
        }
        finally
        {
            File.Delete(file);
        }
    }
}
