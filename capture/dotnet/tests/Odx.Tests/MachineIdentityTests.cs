using Odx.Core;
using Xunit;

namespace Odx.Tests;

public class MachineIdentityTests
{
    [Fact]
    public void Made_once_per_data_folder_and_different_between_folders()
    {
        string root = Path.Combine(Path.GetTempPath(), $"odx-machine-{Guid.NewGuid():N}");
        try
        {
            string a = MachineIdentity.Get(Path.Combine(root, "a"));
            Assert.Matches("^[0-9a-f]{32}$", a);
            Assert.Equal(a, MachineIdentity.Get(Path.Combine(root, "a")));
            Assert.Equal(a, File.ReadAllText(Path.Combine(root, "a", MachineIdentity.FileName)));
            Assert.NotEqual(a, MachineIdentity.Get(Path.Combine(root, "b")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
