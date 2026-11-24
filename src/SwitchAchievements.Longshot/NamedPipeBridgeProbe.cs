using System;
using System.Threading;
using System.Threading.Tasks;
using SwitchAchievements.Core;

namespace SwitchAchievements.Longshot;

/// <summary>
/// Read-only probe of the Longshot control bridge. It opens one connection and,
/// where possible, performs the experimental ownership query and the read-only
/// <c>status</c> round-trip on that same connection. Handle disposal and finite
/// timeouts apply on every path.
/// </summary>
public sealed class NamedPipeBridgeProbe : INamedPipeProbe
{
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _statusTimeout;

    public NamedPipeBridgeProbe(TimeSpan? connectTimeout = null, TimeSpan? statusTimeout = null)
    {
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
        _statusTimeout = statusTimeout ?? TimeSpan.FromSeconds(10);
    }

    public async Task<BridgeProbeResult> ProbeAsync(string pipeName, CancellationToken ct = default)
    {
        var client = new ControlBridgeClient(pipeName);
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(_connectTimeout);
            await client.ConnectAsync(connectCts.Token).ConfigureAwait(false);

            uint? experimentalServerPid = null;
            var handle = client.PipeHandle;
            if (handle is not null && !handle.IsInvalid)
            {
                // EXPERIMENTAL: diagnostic only; never used to resolve selection.
                try
                {
                    if (NativeMethods.GetNamedPipeServerProcessId(handle, out var pid))
                        experimentalServerPid = pid;
                }
                catch
                {
                    // ownership is best-effort and experimental
                }
            }

            bool responding = false;
            string? error = null;
            try
            {
                var timeoutSeconds = Math.Max(1, (int)_statusTimeout.TotalSeconds);
                var reply = await client
                    .SendAsync(1, Requests.Status(), timeoutSeconds, ct)
                    .ConfigureAwait(false);

                responding = Wire.GetOk(reply);
                if (!responding)
                    error = $"status error={Wire.GetError(reply)} message={Wire.GetMessage(reply)}";
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            return new BridgeProbeResult(true, responding, experimentalServerPid, error);
        }
        catch (Exception ex)
        {
            return new BridgeProbeResult(false, false, null, ex.Message);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }
}