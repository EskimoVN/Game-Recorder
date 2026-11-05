namespace Odx.Core;

/// <summary>
/// The id this installation reports as its machine (session.json players[].machine_id): a random id made on first use and
/// kept in data\machine-id. Nothing of the machine goes into it: a hostname hash or the Windows MachineGuid is shared by
/// PCs installed from one image, which the client would read as two views from one machine.
/// </summary>
public static class MachineIdentity
{
    public const string FileName = "machine-id";

    private static string? _unsaved;

    /// <param name="dataDir">The package's data folder; null for <see cref="PackageFolders.Data"/>.</param>
    public static string Get(string? dataDir = null)
    {
        string path = Path.Combine(dataDir ?? PackageFolders.Data, FileName);
        try
        {
            if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: 32 } saved) return saved;
            string id = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, id);
            return id;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Cannot be kept (read-only install): the same id for the rest of this process at least.
            return _unsaved ??= Guid.NewGuid().ToString("N");
        }
    }
}
