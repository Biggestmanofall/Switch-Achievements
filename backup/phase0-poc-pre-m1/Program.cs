using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SwitchAchievements.Longshot.Poc;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var cmd = args.Length == 0 ? "help" : args[0].ToLowerInvariant();
        var options = ParseOptions(args.Skip(1));

        var logDir = Path.Combine(Directory.GetCurrentDirectory(), "logs");
        var logger = new DiagnosticsLogger(logDir, "longshot-poc");
        Action<string> log = msg => { Console.WriteLine(msg); logger.Info(msg); };

        using var ctrlC = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; ctrlC.Cancel(); };

        try
        {
            return cmd switch
            {
                "help" => Help(),
                "detect" => await DetectAsync(options, log),
                "status" => await StatusOnceAsync(options, log),
                "watch" => await WatchAsync(options, log, ctrlC.Token),
                "quit" => await SendQuitAsync(options, log),
                _ => Unknown(cmd),
            };
        }
        catch (Exception ex)
        {
            logger.Error(ex.ToString());
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return 1;
        }
    }

    private static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
    {
        var map = new Dictionary<string, string>();
        var list = args.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var key = a[2..];
                var val = "true";
                if (i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal))
                    val = list[++i];
                map[key] = val;
            }
        }
        return map;
    }

    private static string Opt(Dictionary<string, string> o, string key, string def)
        => o.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : def;

    private static int OptInt(Dictionary<string, string> o, string key, int def)
        => int.TryParse(Opt(o, key, def.ToString()), out var v) ? v : def;

    private static bool OptBool(Dictionary<string, string> o, string key, bool def)
        => o.TryGetValue(key, out var v) ? (v == "true" || v == "1" || v == "yes") : def;

    private static int Help() { PrintHelp(); return 0; }

    private static int Unknown(string cmd) { Console.WriteLine("unknown command '" + cmd + "'"); PrintHelp(); return 2; }

    private static void PrintHelp()
    {
        Console.WriteLine("Switch Achievements - Longshot Phase 0 proof of concept");
        Console.WriteLine("  (reads game state over Longshot's control bridge, protocol v1; read-only)");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project src/SwitchAchievements.Longshot.Poc -- <command> [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  detect [--wait]   Find longshot.exe processes (PID, path, build id).");
        Console.WriteLine("  status            One-shot game status + OoT snapshot.");
        Console.WriteLine("  watch             Subscribe to oot samples and print them (+ changes).");
        Console.WriteLine("  quit              Ask the bridge to close the game.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --pipe NAME       Named-pipe name (default: longshot_control).");
        Console.WriteLine("  --timeout SEC     Request timeout in seconds.");
        Console.WriteLine("  --every N         oot sample cadence in frames (watch; default 60).");
        Console.WriteLine("  --flags A,B       Flag names/hashes to watch (watch; default PlayerStatus.Life).");
        Console.WriteLine("  --changes BOOL    Report changed flags (watch; default true).");
        Console.WriteLine("  --count N         Stop after N oot samples (watch; default 0 = until stopped).");
        Console.WriteLine("  --wait            Keep polling (detect).");
    }

private static async Task<int> DetectAsync(Dictionary<string, string> o, Action<string> log)
    {
        bool wait = o.ContainsKey("wait");
        var pipe = Opt(o, "pipe", ControlBridgeClient.DefaultPipeName);

        while (true)
        {
            var procs = ProcessDetection.FindLongshotProcesses();
            var game = procs.FirstOrDefault(p => p.IsGameBinary);

            log("longshot process(es): " + (procs.Count == 0 ? "none" : procs.Count.ToString()));
            foreach (var p in procs)
                log("  PID " + p.Pid.ToString().PadLeft(6) + "  " + (p.IsGameBinary ? "GAME  bin/longshot.exe" : "stub  longshot.exe") + "  " + p.ExecutablePath);

            if (game is not null)
            {
                log("=== detected game process ===");
                log("  PID:           " + game.Pid);
                log("  Executable:    " + game.ExecutablePath);
                log("  Game folder:   " + game.GameFolder);
                var bi = BuildIdentifier.FromGameFolder(game.GameFolder);
                if (bi is not null)
                {
                    log("  Build version: " + (bi.Version ?? "?") + "  source: " + (bi.Source ?? "?"));
                    log("  nup_build_id:  " + (bi.NupBuildId ?? "?"));
                    log("  profile:       " + (bi.GameProfileName ?? "?"));
                }
                log("  control pipe:  " + pipe);
                return 0;
            }

            if (!wait)
            {
                log("game not running.");
                log("Enable the bridge: run scripts/launch-recomp-with-control.ps1, then Start Longshot in RecompLauncher.");
                return 1;
            }

            try { await Task.Delay(1500); } catch (OperationCanceledException) { return 0; }
        }
    }

    private static async Task<int> StatusOnceAsync(Dictionary<string, string> o, Action<string> log)
    {
        var pipe = Opt(o, "pipe", ControlBridgeClient.DefaultPipeName);
        var timeout = OptInt(o, "timeout", 10);

        var game = ProcessDetection.FindLongshotProcesses().FirstOrDefault(p => p.IsGameBinary);
        if (game is null)
        {
            log("game not running - launch via scripts/launch-recomp-with-control.ps1 first, then Start Longshot.");
            return 1;
        }

        log("game: PID " + game.Pid + "  " + game.ExecutablePath);
        var bi = BuildIdentifier.FromGameFolder(game.GameFolder);
        if (bi is not null)
            log("build: version=" + bi.Version + " source=" + bi.Source + " nup_build_id=" + bi.NupBuildId);

        await using var client = new ControlBridgeClient(pipe);
        client.StatusChanged += s => log("[bridge] " + s);
        await client.ConnectAsync();

        var reply = await client.SendAsync(1, Requests.Status(), timeout);
        if (!Wire.GetOk(reply))
        {
            log("status failed: error=" + Wire.GetError(reply) + " message=" + Wire.GetMessage(reply));
            return 1;
        }

        var status = JsonSerializer.Deserialize<StatusReply>(reply.GetRawText(), Wire.SerializerOptions);
        log(status is null ? "(empty status)" : FormatStatus(status));
        return 0;
    }

    private static async Task<int> SendQuitAsync(Dictionary<string, string> o, Action<string> log)
    {
        var pipe = Opt(o, "pipe", ControlBridgeClient.DefaultPipeName);
        var timeout = OptInt(o, "timeout", 10);

        if (ProcessDetection.FindLongshotProcesses().All(p => !p.IsGameBinary))
        {
            log("game not running.");
            return 1;
        }

        await using var client = new ControlBridgeClient(pipe);
        await client.ConnectAsync();
        var reply = await client.SendAsync(1, Requests.Quit(), timeout);
        log(Wire.GetOk(reply)
            ? "quit acknowledged - the game is closing."
            : "quit failed: error=" + Wire.GetError(reply) + " message=" + Wire.GetMessage(reply));
        return Wire.GetOk(reply) ? 0 : 1;
    }
private static async Task<int> WatchAsync(Dictionary<string, string> o, Action<string> log, CancellationToken ct)
    {
        var pipe = Opt(o, "pipe", ControlBridgeClient.DefaultPipeName);
        var every = OptInt(o, "every", 60);
        var flags = Opt(o, "flags", "PlayerStatus.Life")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var changes = OptBool(o, "changes", true);
        var count = OptInt(o, "count", 0);

        string? lastMap = null;
        long? lastLife = null;
        long? lastRupees = null;
        int changedEvents = 0;

        while (!ct.IsCancellationRequested)
        {
            var game = ProcessDetection.FindLongshotProcesses().FirstOrDefault(p => p.IsGameBinary);
            if (game is null)
            {
                log("[watch] game not running - run scripts/launch-recomp-with-control.ps1, then Start Longshot in RecompLauncher");
                try { await Task.Delay(1500, ct); } catch (OperationCanceledException) { return 0; }
                continue;
            }

            await using var client = new ControlBridgeClient(pipe);
            Action<string> onStatus = s => log("[bridge] " + s);
            var stop = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            int samples = 0;

            Action<JsonElement> onEvent = el =>
            {
                var ev = Wire.GetEvent(el);
                if (ev != "oot")
                {
                    if (ev is "present" or "state" or "map" or "log" or "log_dropped")
                        log("[" + ev + "] " + el.GetRawText());
                    else
                        log("[event " + (ev ?? "?") + "] " + el.GetRawText());
                    return;
                }

                samples++;
                OotEvent? oot = null;
                try { oot = JsonSerializer.Deserialize<OotEvent>(el.GetRawText(), Wire.SerializerOptions); }
                catch (Exception ex) { log("[watch] failed to parse oot event: " + ex.Message); return; }

                var g = oot?.Game;
                if (g is not null)
                {
                    if (lastMap is not null && g.Map is not null && g.Map != lastMap) log("  >>> MAP CHANGED: " + lastMap + " -> " + g.Map);
                    if (lastLife is not null && g.Life is not null && g.Life != lastLife) log("  >>> LIFE CHANGED: " + lastLife + " -> " + g.Life);
                    if (lastRupees is not null && g.Rupees is not null && g.Rupees != lastRupees) log("  >>> RUPEES CHANGED: " + lastRupees + " -> " + g.Rupees);
                    if (g.Changed is { Count: > 0 })
                    {
                        changedEvents++;
                        log("  >>> CHANGED FLAGS (#" + changedEvents + "): " + FormatFlags(g.Changed));
                    }
                    lastMap = g.Map;
                    lastLife = g.Life;
                    lastRupees = g.Rupees;
                }

                log("[oot #" + samples + "] frame=" + (oot?.Frame ?? -1) + " " + (g?.ToString() ?? "(no game snapshot)"));
                if (count > 0 && samples >= count) stop.TrySetResult(0);
            };

            client.StatusChanged += onStatus;
            client.EventReceived += onEvent;

            try
            {
                await client.ConnectAsync(ct);
                log("[watch] connected (pipe '" + pipe + "'); subscribing oot every=" + every + " flags=" + string.Join(",", flags) + " changes=" + changes);
                var reply = await client.SendAsync(1, Requests.Subscribe(new[] { "oot" }, every, flags.Length > 0 ? flags : null, changes), 15, ct);
                if (!Wire.GetOk(reply))
                {
                    log("[watch] subscribe failed: error=" + Wire.GetError(reply) + " message=" + Wire.GetMessage(reply));
                    try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { return 0; }
                    continue;
                }
                log("[watch] subscribed. Press Ctrl+C to stop. Perform your in-game action now (enter a door, collect a rupee, take damage).");

                var exitOnCancel = Task.Delay(Timeout.Infinite, ct);
                var winner = await Task.WhenAny(stop.Task, client.Disconnected, exitOnCancel);
                if (ct.IsCancellationRequested) { log("[watch] stopped (Ctrl+C)."); return 0; }
                if (winner == stop.Task) { log("[watch] sample limit reached."); return 0; }
                log("[watch] bridge disconnected - waiting for the game to relaunch so it can reconnect.");
            }
            catch (TimeoutException ex) { log("[watch] " + ex.Message); }
            catch (IOException ex) { log("[watch] connection lost: " + ex.Message); }
            catch (InvalidOperationException ex) { log("[watch] " + ex.Message); }
            catch (OperationCanceledException) { log("[watch] stopped."); return 0; }
            finally
            {
                client.EventReceived -= onEvent;
                client.StatusChanged -= onStatus;
            }

            try { await Task.Delay(1500, ct); } catch (OperationCanceledException) { return 0; }
        }

        return 0;
    }

private static string BoolStr(bool? v) => v is bool x ? (x ? "true" : "false") : "?";

    private static string FormatStatus(StatusReply s)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("  frame=" + s.Frame + "  protocol=" + s.Protocol + "  fps=" + s.Fps + "  state=" + (s.State ?? "?") + "  stalled=" + s.Stalled);
        sb.AppendLine("  draws=" + s.Draws + "  replayed=" + s.Replayed + "  uptime=" + s.Uptime + "s  clients=" + s.Clients);
        sb.AppendLine("  map=" + (s.Map ?? "null") + "  version=" + (s.Version ?? "?"));
        sb.AppendLine("  renderer=" + (s.Renderer ?? "?"));
        if (s.Game is null)
        {
            sb.AppendLine("  game=null (no Link update yet)");
        }
        else
        {
            var g = s.Game;
            var pos = g.Pos is { Length: 3 }
                ? "(" + g.Pos[0].ToString("0.0") + ", " + g.Pos[1].ToString("0.0") + ", " + g.Pos[2].ToString("0.0") + ")"
                : "null";
            sb.AppendLine("  game: map=" + (g.Map ?? "null") + "  age=" + (g.Age ?? "?") + "  event=" + BoolStr(g.EventPlaying) + "  loading=" + BoolStr(g.Loading));
            sb.AppendLine("        life=" + (g.Life?.ToString() ?? "?") + "/" + (g.MaxLife?.ToString() ?? "?") + "  rupees=" + (g.Rupees?.ToString() ?? "?") + "  pos=" + pos);
            sb.AppendLine("        prompt=" + (g.Prompt ?? "?") + "  music=" + (g.Music ?? "?"));
            sb.AppendLine("        flags=" + (g.Flags is not null ? FormatFlags(g.Flags) : "(none)"));
        }
        return sb.ToString().TrimEnd();
    }

    private static string FormatFlags(Dictionary<string, JsonElement> flags)
        => flags.Count == 0 ? "{}" : "{" + string.Join(", ", flags.Select(kv => kv.Key + "=" + Wire.FormatScalar(kv.Value))) + "}";
}