using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using Microsoft.Win32.SafeHandles;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SwitchAchievements.Longshot;

/// <summary>
/// A minimal protocol-v1 client for Longshot's control bridge (host/control.c).
/// Transport on Windows is a local named pipe; framing is newline-delimited JSON.
/// </summary>
public sealed class ControlBridgeClient : IAsyncDisposable
{
    public const string DefaultPipeName = "longshot_control";

    private readonly string _pipeName;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private CancellationTokenSource? _cts;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private TaskCompletionSource _disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ControlBridgeClient(string pipeName = DefaultPipeName) => _pipeName = pipeName;

    /// <summary>Raised for every subscription event line (has an "event" key, no "ok").</summary>
    public event Action<JsonElement>? EventReceived;

    /// <summary>Raised for lifecycle messages (connect/disconnect/errors).</summary>
    public event Action<string>? StatusChanged;

    public bool IsConnected => _pipe is { IsConnected: true };

    /// <summary>
    /// The underlying pipe handle while connected; null before connect or after
    /// close. Exposed so the probe can query experimental server identity on the
    /// same connection used for the read-only status round-trip.
    /// </summary>
    public SafePipeHandle? PipeHandle => _pipe?.SafePipeHandle;

    /// <summary>Completes when the current connection ends.</summary>
    public Task Disconnected => _disconnected.Task;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, ct).ConfigureAwait(false);

        _disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pipe = pipe;
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = false };
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => ReadLoopAsync(pipe, _cts.Token));
        StatusChanged?.Invoke($"connected to \\\\.\\pipe\\{_pipeName}");
    }

    /// <summary>Send one request (fields must contain "cmd"; "id" is injected) and await its reply.</summary>
    public async Task<JsonElement> SendAsync(long id, IDictionary<string, object?> fields, int timeoutSeconds, CancellationToken ct = default)
    {
        if (_pipe is null || _writer is null || !_pipe.IsConnected)
            throw new InvalidOperationException("not connected");

        fields["id"] = id;
        var json = JsonSerializer.Serialize(fields, Wire.SerializerOptions);
        if (Encoding.UTF8.GetByteCount(json) > Wire.MaxLineBytes)
            throw new InvalidOperationException("request exceeds the 64 KB protocol line limit");

        var key = id.ToString(CultureInfo.InvariantCulture);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = tcs;

        await WriteLineAsync(json).ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            return await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"request id={id} timed out after {timeoutSeconds}s");
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

public Task WaitForDisconnectAsync() => _disconnected.Task;

    private async Task WriteLineAsync(string line)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _writer!.WriteAsync(line).ConfigureAwait(false);
            await _writer!.WriteAsync("\n").ConfigureAwait(false);
            await _writer!.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(Stream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                if (line.Length == 0) continue;
                Dispatch(line);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { StatusChanged?.Invoke("read loop error: " + ex.Message); }
        finally
        {
            try
            {
                await CloseAsync().ConfigureAwait(false);
            }
            finally
            {
                // Always signal exactly one disconnect, even if teardown faulted,
                // so a single connection loss is never silently missed.
                _disconnected.TrySetResult();
                StatusChanged?.Invoke("disconnected");
            }
        }
    }

    private void Dispatch(string line)
    {
        JsonElement el;
        try { el = Wire.ParseOneLine(line); }
        catch (Exception) { StatusChanged?.Invoke("ignored non-JSON line"); return; }

        if (Wire.IsEvent(el))
        {
            EventReceived?.Invoke(el);
            return;
        }

        var id = Wire.GetId(el);
        if (id is not null && _pending.TryRemove(id.Value.ToString(CultureInfo.InvariantCulture), out var tcs))
            tcs.TrySetResult(el);
        else
            StatusChanged?.Invoke("unmatched reply: " + line);
    }

    private async Task CloseAsync()
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Tearing down a connection whose peer already closed can make the
            // writer's final flush throw "pipe is broken". That fault must not
            // escape: it would otherwise skip the read loop's disconnect signal
            // below and surface out of DisposeAsync.
            try
            {
                _writer?.Dispose();
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                _writer = null;
            }
        }
        finally
        {
            _writeLock.Release();
        }

        _pipe?.Dispose();
        _pipe = null;

        foreach (var kv in _pending) kv.Value.TrySetCanceled();
        _pending.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        await CloseAsync().ConfigureAwait(false);
        _cts?.Dispose();
    }
}