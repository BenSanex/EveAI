using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Eva.App;

public sealed class CodexAppServer : IAsyncDisposable
{
    private readonly string _runtimeWorkspace;
    private readonly string _cliDirectory;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Process? _process;
    private CancellationTokenSource? _readerLifetime;
    private long _nextId;

    public event EventHandler<JsonNode>? Notification;
    public bool IsConnected => _process is { HasExited: false };

    public CodexAppServer(string runtimeWorkspace, string cliDirectory)
    {
        _runtimeWorkspace = Path.GetFullPath(runtimeWorkspace);
        _cliDirectory = Path.GetFullPath(cliDirectory);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected)
        {
            return;
        }

        var info = new ProcessStartInfo("codex")
        {
            WorkingDirectory = _runtimeWorkspace,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        info.ArgumentList.Add("app-server");
        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        info.Environment["PATH"] = _cliDirectory + Path.PathSeparator + currentPath;
        info.Environment["EVA_CODEX_RUNTIME"] = "1";

        _process = Process.Start(info) ?? throw new InvalidOperationException("Could not launch codex app-server.");
        _readerLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = ReadLoop(_process, _readerLifetime.Token);
        _ = DrainErrors(_process, _readerLifetime.Token);
        await RequestAsync("initialize", new JsonObject
        {
            ["clientInfo"] = new JsonObject { ["name"] = "Eva", ["version"] = "0.1.0" },
            ["capabilities"] = new JsonObject()
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonNode?> RequestAsync(
        string method,
        JsonNode? parameters,
        CancellationToken cancellationToken = default)
    {
        if (!IsConnected || _process is null)
        {
            throw new InvalidOperationException("Codex app-server is not connected.");
        }

        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException("Duplicate request ID.");
        }

        using var registration = cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(id, out var pending))
            {
                pending.TrySetCanceled(cancellationToken);
            }
        });

        await SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters
        }, cancellationToken).ConfigureAwait(false);

        return await completion.Task.ConfigureAwait(false);
    }

    public Task InterruptAsync(string threadId, string turnId, CancellationToken cancellationToken = default) =>
        SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "turn/interrupt",
            ["params"] = new JsonObject { ["threadId"] = threadId, ["turnId"] = turnId }
        }, cancellationToken);

    private async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        if (_process is null)
        {
            throw new InvalidOperationException("Codex process is unavailable.");
        }
        var json = message.ToJsonString();
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoop(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }
                JsonNode? message;
                try
                {
                    message = JsonNode.Parse(line);
                }
                catch (JsonException)
                {
                    Notification?.Invoke(this, new JsonObject
                    {
                        ["method"] = "eva/protocol-error",
                        ["params"] = new JsonObject { ["message"] = "Codex emitted malformed JSON." }
                    });
                    continue;
                }

                if (message is null)
                {
                    continue;
                }
                if (message["id"]?.GetValue<long?>() is { } id && _pending.TryRemove(id, out var completion))
                {
                    if (message["error"] is { } error)
                    {
                        completion.TrySetException(new InvalidOperationException(error.ToJsonString()));
                    }
                    else
                    {
                        completion.TrySetResult(message["result"]?.DeepClone());
                    }
                }
                else
                {
                    Notification?.Invoke(this, message);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            foreach (var (_, pending) in _pending)
            {
                pending.TrySetException(new IOException("Codex app-server disconnected."));
            }
            _pending.Clear();
        }
    }

    private async Task DrainErrors(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_readerLifetime is not null)
        {
            await _readerLifetime.CancelAsync().ConfigureAwait(false);
            _readerLifetime.Dispose();
        }
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }
        _process?.Dispose();
        _writeLock.Dispose();
    }
}
