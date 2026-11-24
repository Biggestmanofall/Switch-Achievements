using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SwitchAchievements.Core;

namespace SwitchAchievements.Longshot;

/// <summary>
/// The <see cref="IGameIntegration"/> implementation for Longshot (the Switch 2
/// recompilation runtime). It speaks Longshot's read-only control bridge over a
/// local named pipe and translates the Ocarina-of-Time "oot" samples into
/// normalized snapshots consumed by the rest of the platform.
///
/// This class never performs memory writing, DLL injection, or game modification.
/// It uses only the read-only "status" and "subscribe" commands and never enables
/// <c>HOS_CONTROL_MEM</c>.
/// </summary>
public sealed class LongshotGameIntegration : IGameIntegration
{
    private const string OotEventName = "oot";
    private const int SampleEveryFrames = 60;
    private const int RequestTimeoutSeconds = 10;

    private readonly string _pipeName;

    private volatile ControlBridgeClient? _client;
    private GameProcessInfo? _process;
    private long _nextRequestId;
    private volatile bool _disposed;

    public LongshotGameIntegration(string pipeName = ControlBridgeClient.DefaultPipeName)
    {
        _pipeName = pipeName;
    }

    public string Id => "longshot";

    public string DisplayName => "Longshot (recompilation)";

    public string ProcessName => "longshot";

    /// <summary>True while a bridge connection is currently open.</summary>
    public bool IsConnected => _client?.IsConnected == true;

    /// <summary>The named-pipe name this integration connects to (e.g. "longshot_control").</summary>
    public string PipeName => _pipeName;

    public event Action<string>? StatusChanged;
    public event Action<NormalizedSnapshot>? SnapshotReceived;
    public event Action? Disconnected;

    public bool CanHandle(GameProcessInfo process)
    {
        var path = process.ExecutablePath;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (!string.Equals(Path.GetFileName(path), "longshot.exe", StringComparison.OrdinalIgnoreCase))
            return false;

        var directory = Path.GetDirectoryName(path);
        if (directory is null)
            return false;

        return string.Equals(Path.GetFileName(directory), "bin", StringComparison.OrdinalIgnoreCase);
    }

    public async Task ConnectAsync(GameProcessInfo process, CancellationToken ct = default)
    {
        await DisposeCurrentClientAsync().ConfigureAwait(false);

        var client = new ControlBridgeClient(_pipeName);
        client.StatusChanged += OnClientStatusChanged;
        client.EventReceived += OnClientEvent;

        await client.ConnectAsync(ct).ConfigureAwait(false);

        _process = process;
        _nextRequestId = 0;
        _client = client;
        ObserveDisconnect(client);
    }

    public async Task SubscribeAsync(CancellationToken ct = default)
    {
        var client = RequireClient();
        var reply = await client
            .SendAsync(NextRequestId(), Requests.Subscribe(new[] { OotEventName }, SampleEveryFrames), RequestTimeoutSeconds, ct)
            .ConfigureAwait(false);

        if (!Wire.GetOk(reply))
            throw new InvalidOperationException(
                $"subscribe failed: error={Wire.GetError(reply)} message={Wire.GetMessage(reply)}");
    }

    public async Task UnsubscribeAsync(CancellationToken ct = default)
    {
        if (_client is null)
            return;

        await _client
            .SendAsync(NextRequestId(), Requests.Unsubscribe(), RequestTimeoutSeconds, ct)
            .ConfigureAwait(false);
    }

    public async Task<NormalizedSnapshot?> GetSnapshotAsync(CancellationToken ct = default)
    {
        var client = RequireClient();
        var reply = await client
            .SendAsync(NextRequestId(), Requests.Status(), RequestTimeoutSeconds, ct)
            .ConfigureAwait(false);

        if (!Wire.GetOk(reply))
            throw new InvalidOperationException(
                $"status failed: error={Wire.GetError(reply)} message={Wire.GetMessage(reply)}");

        var status = JsonSerializer.Deserialize<StatusReply>(reply.GetRawText(), Wire.SerializerOptions);
        return status?.Game is null ? null : ToNormalized(status.Game, status.Frame);
    }

    private ControlBridgeClient RequireClient()
        => _client ?? throw new InvalidOperationException("not connected to Longshot");

    private long NextRequestId() => Interlocked.Increment(ref _nextRequestId);

    private void ObserveDisconnect(ControlBridgeClient client)
    {
        _ = ObserveDisconnectAsync(client);
    }

    private async Task ObserveDisconnectAsync(ControlBridgeClient client)
    {
        // client.Disconnected completes once, when the connection's read loop
        // ends (remote close, transport error, or cancellation).
        await client.Disconnected.ConfigureAwait(false);

        // Intentional teardown clears (_client = null) or replaces (_client =
        // a newer client) the client before the loop finishes, and final
        // disposal sets _disposed. Only a still-active, live client that
        // dropped on its own counts as an unexpected loss.
        if (ReferenceEquals(_client, client) && !_disposed)
            RaiseDisconnected();
    }

    private void RaiseDisconnected()
    {
        var handlers = Disconnected;
        if (handlers is null)
            return;

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action)handler)();
            }
            catch
            {
                // A throwing subscriber must never break the read-loop teardown
                // path or stop the remaining subscribers from being notified.
            }
        }
    }

    private void OnClientStatusChanged(string message) => StatusChanged?.Invoke(message);

    private void OnClientEvent(JsonElement element)
    {
        if (Wire.GetEvent(element) == OotEventName)
        {
            OotEvent? oot;
            try
            {
                oot = JsonSerializer.Deserialize<OotEvent>(element.GetRawText(), Wire.SerializerOptions);
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke("failed to parse oot event: " + ex.Message);
                return;
            }

            if (oot?.Game is null)
                return;

            SnapshotReceived?.Invoke(ToNormalized(oot.Game, oot.Frame));
            return;
        }

        // Forward the low-noise lifecycle events as human-readable status. The
        // high-frequency "present" event is ignored for now.
        switch (Wire.GetEvent(element))
        {
            case "map":
                StatusChanged?.Invoke(FormatMapEvent(element));
                break;
            case "state":
                StatusChanged?.Invoke(FormatStateEvent(element));
                break;
        }
    }

    private static string FormatMapEvent(JsonElement element)
    {
        var map = element.TryGetProperty("map", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        var from = element.TryGetProperty("from", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
        return from is null ? $"map={map}" : $"map changed: {from} -> {map}";
    }

    private static string FormatStateEvent(JsonElement element)
    {
        var state = element.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        return $"state={state}";
    }

    private NormalizedSnapshot ToNormalized(OotGameSnapshot game, long frame)
    {
        return new NormalizedSnapshot
        {
            IntegrationId = Id,
            // Canonical game-id mapping (e.g. "zelda-oot") is a later milestone.
            GameId = GameId.Unknown,
            SourceProcessId = _process?.ProcessId ?? 0,
            SourceProcessStartUtc = _process?.ProcessStartUtc ?? default,
            GameFrame = frame,
            ReceivedUtc = DateTime.UtcNow,
            Map = game.Map,
            Age = game.Age,
            EventPlaying = game.EventPlaying,
            Loading = game.Loading,
            Life = game.Life,
            MaxLife = game.MaxLife,
            Rupees = game.Rupees,
            Flags = ToFlags(game.Flags),
        };
    }

    private static IReadOnlyDictionary<string, long?>? ToFlags(Dictionary<string, JsonElement>? flags)
    {
        if (flags is null || flags.Count == 0)
            return null;

        var result = new Dictionary<string, long?>(flags.Count);
        foreach (var kv in flags)
            result[kv.Key] = TryAsLong(kv.Value);

        return result;
    }

    private static long? TryAsLong(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.TryGetInt64(out var v) ? v : null;
            case JsonValueKind.True:
                return 1;
            case JsonValueKind.False:
                return 0;
            case JsonValueKind.String:
                return long.TryParse(element.GetString(), out var s) ? s : null;
            default:
                return null;
        }
    }

    private async Task DisposeCurrentClientAsync()
    {
        if (_client is null)
            return;

        var client = _client;
        _client = null;
        client.StatusChanged -= OnClientStatusChanged;
        client.EventReceived -= OnClientEvent;
        await client.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await DisposeCurrentClientAsync().ConfigureAwait(false);
    }
}