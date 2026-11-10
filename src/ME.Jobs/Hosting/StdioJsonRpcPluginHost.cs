// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Acornsoft.MessageExplorer.Jobs.Model;
using Acornsoft.MessageExplorer.Jobs.Protocol;

namespace Acornsoft.MessageExplorer.Jobs.Hosting;

public sealed record PluginHostOptions
{
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Runs a plugin as a child process and talks JSON-RPC 2.0 over its stdin and stdout, one JSON object per line.
/// Stderr is kept as a short tail for crash reports. A timed-out or misbehaving plugin is stopped.
/// </summary>
public sealed class StdioJsonRpcPluginHost : IPluginHost
{
    private const int StderrTailLines = 20;

    private readonly Process _process;
    private readonly string _label;
    private readonly PluginHostOptions _options;
    private readonly ConcurrentDictionary<long, (TaskCompletionSource<JsonNode?> Completion, string Method)> _pending = new();
    private readonly ConcurrentQueue<string> _stderr = new();
    private readonly ConcurrentQueue<JsonObject> _events = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Task _stdoutPump;
    private readonly Task _stderrPump;
    private long _nextId;
    private volatile Exception? _fault;
    private PluginInfo? _info;
    private bool _disposed;

    private StdioJsonRpcPluginHost(Process process, string label, PluginHostOptions options)
    {
        _process = process;
        _label = label;
        _options = options;
        _stdoutPump = Task.Run(PumpStdoutAsync);
        _stderrPump = Task.Run(PumpStderrAsync);
    }

    public PluginInfo Info => _info ?? throw new InvalidOperationException("The plugin has not finished its handshake.");

    /// <summary>Notifications the plugin sent with method <c>event</c>.</summary>
    public IReadOnlyCollection<JsonObject> Events => _events.ToArray();

    public bool HasExited => _process.HasExited;

    public static async Task<StdioJsonRpcPluginHost> StartAsync(
        string expectedName,
        string expectedVersion,
        IReadOnlyList<string> command,
        string workingDirectory,
        IReadOnlySet<PluginRole> requiredRoles,
        PluginInitialize initialize,
        PluginHostOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Count == 0)
            throw new ArgumentException("A plugin command is required.", nameof(command));
        options ??= new PluginHostOptions();

        var start = new ProcessStartInfo(command[0])
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in command.Skip(1))
            start.ArgumentList.Add(argument);
        start.Environment["PYTHONUNBUFFERED"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new PluginException(expectedName, $"could not start '{command[0]}'.");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new PluginException(expectedName, $"could not start '{command[0]}': {exception.Message}", exception);
        }

        var host = new StdioJsonRpcPluginHost(process, expectedName, options);
        try
        {
            var result = await host.CallAsync("initialize", initialize.ToWire(), options.StartTimeout, cancellationToken).ConfigureAwait(false);
            host._info = WireParse.Handshake(expectedName, result, expectedName, expectedVersion, requiredRoles);
            return host;
        }
        catch
        {
            host.Kill();
            await host.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<OutboundMessage>> TickAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        WireParse.Messages(_label, await CallAsync("tick", new JsonObject { ["now"] = PluginProtocol.FormatTime(now) }, null, cancellationToken).ConfigureAwait(false), "messages");

    public async Task<IReadOnlyList<OutboundMessage>> NextBatchAsync(int max, CancellationToken cancellationToken) =>
        WireParse.Messages(_label, await CallAsync("nextBatch", new JsonObject { ["max"] = max }, null, cancellationToken).ConfigureAwait(false), "messages");

    public async Task<IReadOnlyList<OutboundMessage>> DeliverAsync(BusMessage message, DateTimeOffset now, CancellationToken cancellationToken) =>
        WireParse.Messages(_label, await CallAsync("deliver", new JsonObject { ["now"] = PluginProtocol.FormatTime(now), ["message"] = message.ToWire() }, null, cancellationToken).ConfigureAwait(false), "replies");

    public async Task<IReadOnlyList<JsonObject>> ExpectAsync(BusMessage sent, CancellationToken cancellationToken) =>
        WireParse.Expectations(_label, await CallAsync("expect", new JsonObject { ["message"] = sent.ToWire() }, null, cancellationToken).ConfigureAwait(false), sent.CorrelationId);

    public async Task<IReadOnlyList<Observation>> ObserveAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        WireParse.Observations(_label, await CallAsync("observe", new JsonObject { ["now"] = PluginProtocol.FormatTime(now) }, null, cancellationToken).ConfigureAwait(false));

    public async Task<Verdict> AssertAsync(string correlationId, IReadOnlyList<JsonObject> expected, IReadOnlyList<Observation> actual, CancellationToken cancellationToken)
    {
        var parameters = new JsonObject
        {
            ["correlationId"] = correlationId,
            ["expected"] = new JsonArray(expected.Select(e => (JsonNode)e.DeepClone()).ToArray()),
            ["actual"] = new JsonArray(actual.Select(a => (JsonNode)a.ToWire()).ToArray()),
        };
        return WireParse.Verdict(_label, await CallAsync("assert", parameters, null, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Sends one request and waits for its answer, a crash, or the timeout.</summary>
    public async Task<JsonNode?> CallAsync(string method, JsonObject parameters, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_fault is not null)
            throw _fault;

        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = (completion, method);
        if (_fault is not null && _pending.TryRemove(id, out _))
            throw _fault;
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters };

        try
        {
            await WriteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            _pending.TryRemove(id, out _);
            throw await CrashAsync().ConfigureAwait(false);
        }

        var limit = timeout ?? _options.CallTimeout;
        try
        {
            return await completion.Task.WaitAsync(limit, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _pending.TryRemove(id, out _);
            var error = new PluginTimeoutException(_label, method, limit);
            _fault = error;
            Kill();
            throw error;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        if (!_process.HasExited && _fault is null && _info is not null)
        {
            try
            {
                await CallAsync("shutdown", new JsonObject(), _options.ShutdownTimeout, CancellationToken.None).ConfigureAwait(false);
            }
            catch (PluginException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        _disposed = true;
        try
        {
            _process.StandardInput.Close();
        }
        catch (IOException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        using (var wait = new CancellationTokenSource(_options.ShutdownTimeout))
        {
            try
            {
                await _process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill();
            }
        }

        await Task.WhenAny(Task.WhenAll(_stdoutPump, _stderrPump), Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        _process.Dispose();
        _writeLock.Dispose();
    }

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteLineAsync(message.ToJsonString().AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task PumpStdoutAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(line);
                }
                catch (JsonException)
                {
                    Fail(new PluginProtocolException(_label, $"wrote a line to stdout that is not JSON: {Shorten(line)}. Logs belong on stderr."));
                    Kill();
                    return;
                }

                if (node is not JsonObject obj)
                    continue;
                if (obj["id"] is JsonValue idValue && idValue.TryGetValue<long>(out var id) && _pending.TryRemove(id, out var entry))
                {
                    var completion = entry.Completion;
                    if (obj["error"] is JsonObject error)
                    {
                        var code = error["code"] is JsonValue c && c.TryGetValue<int>(out var number) ? number : -32000;
                        var text = error["message"] is JsonValue m && m.TryGetValue<string>(out var s) ? s : "error";
                        completion.TrySetException(new PluginCallException(_label, entry.Method, code, text));
                    }
                    else
                    {
                        completion.TrySetResult(obj["result"]?.DeepClone());
                    }
                }
                else if (obj["method"] is JsonValue methodValue && methodValue.TryGetValue<string>(out var method) && method == "event")
                {
                    _events.Enqueue((JsonObject)(obj["params"] as JsonObject ?? new JsonObject()).DeepClone());
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }

        Fail(await CrashAsync().ConfigureAwait(false));
    }

    private async Task PumpStderrAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                _stderr.Enqueue(line);
                while (_stderr.Count > StderrTailLines)
                    _stderr.TryDequeue(out _);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private async Task<PluginException> CrashAsync()
    {
        if (_fault is PluginException known)
            return known;
        int? exitCode = null;
        try
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            exitCode = _process.ExitCode;
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException)
        {
        }

        await Task.WhenAny(_stderrPump, Task.Delay(500)).ConfigureAwait(false);
        return new PluginCrashedException(_label, exitCode, string.Join(" | ", _stderr.ToArray().TakeLast(5)));
    }

    private void Fail(PluginException error)
    {
        _fault ??= error;
        foreach (var id in _pending.Keys.ToList())
        {
            if (_pending.TryRemove(id, out var entry))
                entry.Completion.TrySetException(_fault);
        }
    }

    private void Kill()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }

    private static string Shorten(string text) => text.Length <= 80 ? text : text[..80] + "...";
}
