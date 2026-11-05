// odx <command> [args]: the dotnet/ command-line tools in one executable.
//   input      keyboard/mouse with Raw Input (Windows build only)
//   record     game window or monitor with Windows.Graphics.Capture + ffmpeg (Windows build only)
//   build      capture folder -> spec v2 session (frames.csv + session.json)
//   validate   spec v2 §1.5 vendor checks
//   packet-capture the elevated pktmon helper odx-app starts for a room's packet capture (Windows build only)
//   simulate-game  posts a scripted match to odx-orchestrator in place of a game server
//   game-server    the CS2 dedicated server's agent: records and keeps the server's side of every round
//   merge          rounds copied from their participants -> v3 sessions
// The net10.0 build (Linux servers) has build, validate, simulate-game, game-server and merge only.
using System.Text;
using Odx.Cli;

Console.OutputEncoding = new UTF8Encoding(false);
string command = args.Length > 0 ? args[0] : "";
string[] rest = args.Length > 0 ? args[1..] : [];
return command switch
{
#if WINDOWS
    "input" => Odx.Input.InputCommand.Run(rest),
    "record" => Odx.Recorder.RecordCommand.Run(rest),
    "packet-capture" => PacketCaptureCommand.Run(rest),
    "clock-sync" => ClockSyncCommand.Run(rest),
#endif
    "build" => BuildCommand.Run(rest),
    "validate" => ValidateCommand.Run(rest),
    "simulate-game" => SimulateGameCommand.Run(rest),
    "game-server" => GameServerCommand.Run(rest),
    "merge" => MergeCommand.Run(rest),
    _ => Usage(),
};

static int Usage()
{
#if WINDOWS
    Console.Error.WriteLine("usage: odx (input | record | clock-sync | build | validate | simulate-game | game-server | merge) [args]");
#else
    Console.Error.WriteLine("usage: odx (build | validate | simulate-game | game-server | merge) [args]");
#endif
    return 2;
}
