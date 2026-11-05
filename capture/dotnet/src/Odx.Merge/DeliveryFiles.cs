namespace Odx.Merge;

/// <summary>
/// What a delivered session may hold: spec v3 §1's tree, plus key_bindings.json (SOW §2), validation.json and
/// schema.bin.gz (how to decode the .bin files; session.json points at it). Anything else in the tree is a working file
/// that got out (they belong in the _work folder next to the delivery).
/// </summary>
public static class DeliveryFiles
{
    private static readonly HashSet<string> Root = ["session.json", "validation.json", "schema.bin.gz"];
    private static readonly HashSet<string> Server = ["events.jsonl", "server_state.bin", "packets.bin", "packets.txt"];
    private static readonly HashSet<string> Player =
        ["player.json", "frames.csv", "frames.bin", "video.mp4", "events.jsonl", "packets.bin", "packets.txt", "key_bindings.json"];

    /// <summary>Relative paths ('/'-separated) of the files that do not belong in the delivery.</summary>
    public static List<string> Unexpected(string sessionDir) =>
        Directory.EnumerateFiles(sessionDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(sessionDir, f).Replace('\\', '/'))
            .Where(path => !Allowed(path.Split('/')))
            .Order(StringComparer.Ordinal)
            .ToList();

    private static bool Allowed(string[] parts) => parts switch
    {
        [var file] => Root.Contains(file),
        ["server", var file] => Server.Contains(file),
        ["players", _, var file] => Player.Contains(file),
        _ => false,
    };
}
