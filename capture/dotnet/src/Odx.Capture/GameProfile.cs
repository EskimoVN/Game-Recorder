using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Odx.Capture;

/// <summary>
/// Per-game settings for building a session: the session.json fields that capture cannot observe, and the key/button
/// → action bindings that fill input_actions. Stored as JSON (see capture/profiles/).
/// </summary>
/// <param name="WindowProcess">The game's process name (window_process): recorded instead of the monitor when the user
/// picked none, so other windows and notifications stay out of the video.</param>
/// <param name="Network">The game's network traffic to record in multiplayer (spec v3 §2.3); null records none.</param>
/// <param name="CameraSource">Where the camera comes from when no extractor DLL writes camera_samples.csv:
/// "reforger-mod" (<see cref="ReforgerCamera"/>) or "arma3-mod" (<see cref="Arma3Camera"/>); null for none.</param>
public sealed record GameProfile(
    string GameTitle,
    string Platform,
    string Localization,
    JsonObject InputMouseConvention,
    IReadOnlyDictionary<string, string> Bindings,
    string? WindowProcess = null,
    PacketCaptureSpec? Network = null,
    string? CameraSource = null)
{
    /// <summary>The profile file as read, written out as key_bindings.json.</summary>
    public JsonObject Json { get; init; } = [];

    public static GameProfile Load(string path) =>
        Parse(JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new FormatException($"{path}: not a JSON object"), path);

    /// <param name="source">Named in error messages.</param>
    public static GameProfile Parse(JsonObject json, string source)
    {
        string Required(string key) =>
            json[key]?.GetValue<string>() ?? throw new FormatException($"{source}: missing \"{key}\"");
        var convention = json["input_mouse_convention"]?.AsObject() ??
                         throw new FormatException($"{source}: missing \"input_mouse_convention\"");
        var bindings = json["bindings"]?.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>()) ??
                       throw new FormatException($"{source}: missing \"bindings\"");
        return new GameProfile(Required("game_title"), Required("platform"), Required("localization"),
            (JsonObject)convention.DeepClone(), bindings, json["window_process"]?.GetValue<string>(),
            json["network"] is JsonObject network ? PacketCaptureSpec.Parse(network, source) : null,
            json["camera_source"]?.GetValue<string>() is { } camera
                ? camera is ReforgerCamera.Source or Arma3Camera.Source ? camera : throw new FormatException($"{source}: unknown camera_source \"{camera}\"")
                : null) { Json = (JsonObject)json.DeepClone() };
    }
}

/// <summary>
/// "network": {"protocol": "w3gs", "ports": ["tcp/6112", "udp/6112"]}: which traffic to capture and how to decode it into
/// packets.txt ("w3gs": Warcraft III's game protocol; "csgo": CS:GO legacy netchannel, whose ICE key derives from the game
/// build in "version"; "raw": addresses, sizes and payloads only, e.g. encrypted CS2). For "csgo" the listed UDP ports are
/// the game server's, which tells server→client from client→server.
/// </summary>
public sealed partial record PacketCaptureSpec(string Protocol, IReadOnlyList<string> Ports, string? Version = null)
{
    public IEnumerable<int> UdpPorts => Ports.Where(p => p.StartsWith("udp/", StringComparison.Ordinal)).Select(p => int.Parse(p[4..],
        System.Globalization.CultureInfo.InvariantCulture));

    public static PacketCaptureSpec Parse(JsonObject json, string source)
    {
        string protocol = json["protocol"]?.GetValue<string>() ?? "raw";
        var ports = json["ports"]?.AsArray().Select(p => p!.GetValue<string>().ToLowerInvariant()).ToList() ?? [];
        if (ports.Count == 0 || ports.Any(p => !PortPattern().IsMatch(p)))
            throw new FormatException($"{source}: network.ports must list \"tcp/<port>\" or \"udp/<port>\"");
        return new PacketCaptureSpec(protocol, ports, json["version"]?.GetValue<string>());
    }

    [GeneratedRegex(@"^(tcp|udp)/\d{1,5}$")]
    public static partial Regex PortPattern();
}
