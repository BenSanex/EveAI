using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using EveEsi.Core;

namespace Eva.App;

public sealed class PipeWireRecorder : IAsyncDisposable
{
    private Process? _process;
    public string? CurrentPath { get; private set; }
    public bool IsRecording => _process is { HasExited: false };

    public Task<string> StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsRecording)
        {
            throw new InvalidOperationException("Recording is already active.");
        }
        var directory = Path.Combine(Path.GetTempPath(), "eva-audio");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        CurrentPath = Path.Combine(directory, $"recording-{Guid.NewGuid():N}.wav");
        var info = new ProcessStartInfo("pw-record")
        {
            UseShellExecute = false,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("--format");
        info.ArgumentList.Add("s16");
        info.ArgumentList.Add("--rate");
        info.ArgumentList.Add("16000");
        info.ArgumentList.Add("--channels");
        info.ArgumentList.Add("1");
        info.ArgumentList.Add(CurrentPath);
        _process = Process.Start(info) ?? throw new InvalidOperationException("Could not launch pw-record.");
        return Task.FromResult(CurrentPath);
    }

    public async Task<string> StopAsync(CancellationToken cancellationToken = default)
    {
        if (_process is null || CurrentPath is null)
        {
            throw new InvalidOperationException("Recording is not active.");
        }
        if (!_process.HasExited)
        {
            using var signal = Process.Start(new ProcessStartInfo("kill")
            {
                UseShellExecute = false,
                ArgumentList = { "-INT", _process.Id.ToString() }
            });
            if (signal is not null)
            {
                await signal.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            using var graceful = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            graceful.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await _process.WaitForExitAsync(graceful.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        _process.Dispose();
        _process = null;
        return CurrentPath;
    }

    public ValueTask DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill();
        }
        _process?.Dispose();
        if (CurrentPath is { } path)
        {
            TryDelete(path);
        }
        return ValueTask.CompletedTask;
    }

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}

public static class SpeechRuntimePaths
{
    public static string Root => Path.Combine(EvaDataDirectory.Get(), "speech");
    public static string VirtualEnvironment => Path.Combine(Root, "venv");
    public static string PythonExecutable => Path.Combine(VirtualEnvironment, "bin", "python");
    public static string WhisperModelDirectory =>
        Path.Combine(Root, "models", "faster-distil-whisper-large-v3");
    public static string PiperModelPath =>
        Path.Combine(
            Root, "models", "piper", "en", "en_US", "amy", "medium", "en_US-amy-medium.onnx");
    public static string WorkerScript =>
        Path.Combine(AppContext.BaseDirectory, "runtime", "speech-worker.py");
}

public sealed class FasterWhisperTranscriber : IAsyncDisposable
{
    private readonly string _pythonExecutable;
    private readonly string _workerScript;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StringBuilder _errors = new();
    private Process? _worker;
    private string? _loadedModel;
    private long _requestId;

    public string? LastProvider { get; private set; }
    public int? LastElapsedMilliseconds { get; private set; }

    public FasterWhisperTranscriber(string? pythonExecutable = null, string? workerScript = null)
    {
        _pythonExecutable = pythonExecutable ?? SpeechRuntimePaths.PythonExecutable;
        _workerScript = workerScript ?? SpeechRuntimePaths.WorkerScript;
    }

    public async Task<string> TranscribeAndDeleteAsync(
        string wavPath,
        string modelDirectory,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureWorker(modelDirectory);
                if (_worker is null)
                {
                    throw new InvalidOperationException("Speech worker did not start.");
                }
                var request = new JsonObject
                {
                    ["id"] = Interlocked.Increment(ref _requestId),
                    ["operation"] = "transcribe",
                    ["path"] = wavPath
                };
                await _worker.StandardInput.WriteLineAsync(
                    request.ToJsonString().AsMemory(), cancellationToken).ConfigureAwait(false);
                await _worker.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                var line = await _worker.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    throw new IOException($"Speech worker exited unexpectedly. {RecentErrors()}");
                }
                var response = JsonNode.Parse(line)
                    ?? throw new InvalidDataException("Speech worker returned an empty response.");
                if (response["ok"]?.GetValue<bool>() != true)
                {
                    throw new InvalidOperationException(
                        response["error"]?.GetValue<string>() ?? "Local transcription failed.");
                }
                LastProvider = response["provider"]?.GetValue<string>();
                LastElapsedMilliseconds = response["elapsedMs"]?.GetValue<int>();
                return response["text"]?.GetValue<string>()?.Trim() ?? "";
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            PipeWireRecorder.TryDelete(wavPath);
        }
    }

    private void EnsureWorker(string modelDirectory)
    {
        if (_worker is { HasExited: false } &&
            string.Equals(_loadedModel, modelDirectory, StringComparison.Ordinal))
        {
            return;
        }
        StopWorker();
        if (!File.Exists(_pythonExecutable) || !File.Exists(_workerScript) || !Directory.Exists(modelDirectory))
        {
            throw new FileNotFoundException(
                "Eva's local speech runtime is not installed. Run ./scripts/setup-speech.sh.");
        }
        var info = new ProcessStartInfo(_pythonExecutable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        info.ArgumentList.Add(_workerScript);
        info.ArgumentList.Add("--model");
        info.ArgumentList.Add(modelDirectory);
        var nativeLibraries = NvidiaLibraryPath();
        if (nativeLibraries.Length > 0)
        {
            var current = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
            info.Environment["LD_LIBRARY_PATH"] = string.IsNullOrWhiteSpace(current)
                ? nativeLibraries
                : nativeLibraries + Path.PathSeparator + current;
        }
        _errors.Clear();
        _worker = Process.Start(info)
            ?? throw new InvalidOperationException("Could not start the local speech worker.");
        _worker.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is null)
            {
                return;
            }
            lock (_errors)
            {
                _errors.AppendLine(eventArgs.Data);
                if (_errors.Length > 8000)
                {
                    _errors.Remove(0, _errors.Length - 8000);
                }
            }
        };
        _worker.BeginErrorReadLine();
        _loadedModel = modelDirectory;
    }

    private static string NvidiaLibraryPath()
    {
        var libraryRoot = Path.Combine(SpeechRuntimePaths.VirtualEnvironment, "lib");
        if (!Directory.Exists(libraryRoot))
        {
            return "";
        }
        var sitePackages = Directory.EnumerateDirectories(libraryRoot, "python*")
            .Select(path => Path.Combine(path, "site-packages"))
            .FirstOrDefault(Directory.Exists);
        if (sitePackages is null)
        {
            return "";
        }
        return string.Join(
            Path.PathSeparator,
            new[]
            {
                Path.Combine(sitePackages, "nvidia", "cublas", "lib"),
                Path.Combine(sitePackages, "nvidia", "cudnn", "lib")
            }.Where(Directory.Exists));
    }

    private string RecentErrors()
    {
        lock (_errors)
        {
            return _errors.ToString().Trim();
        }
    }

    private void StopWorker()
    {
        if (_worker is { HasExited: false })
        {
            _worker.Kill(entireProcessTree: true);
            _worker.WaitForExit(2000);
        }
        _worker?.Dispose();
        _worker = null;
        _loadedModel = null;
    }

    public ValueTask DisposeAsync()
    {
        StopWorker();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class PiperSpeaker
{
    private readonly string _pythonExecutable;
    private readonly string _workerScript;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _worker;
    private Process? _playback;
    private string? _loadedModel;
    private long _requestId;

    public int? LastElapsedMilliseconds { get; private set; }

    public PiperSpeaker(string? pythonExecutable = null, string? workerScript = null)
    {
        _pythonExecutable = pythonExecutable ?? SpeechRuntimePaths.PythonExecutable;
        _workerScript = workerScript ?? SpeechRuntimePaths.WorkerScript;
    }

    public async Task SpeakAsync(string text, string modelPath, CancellationToken cancellationToken = default)
    {
        StopPlayback();
        var wav = Path.Combine(Path.GetTempPath(), $"eva-tts-{Guid.NewGuid():N}.wav");
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureWorker(modelPath);
                if (_worker is null)
                {
                    throw new InvalidOperationException("Voice worker did not start.");
                }
                var request = new JsonObject
                {
                    ["id"] = Interlocked.Increment(ref _requestId),
                    ["operation"] = "synthesize",
                    ["text"] = text,
                    ["path"] = wav
                };
                await _worker.StandardInput.WriteLineAsync(
                    request.ToJsonString().AsMemory(), cancellationToken).ConfigureAwait(false);
                await _worker.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                var line = await _worker.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    throw new IOException("Voice worker exited unexpectedly.");
                }
                var response = JsonNode.Parse(line)
                    ?? throw new InvalidDataException("Voice worker returned an empty response.");
                if (response["ok"]?.GetValue<bool>() != true)
                {
                    throw new InvalidOperationException(
                        response["error"]?.GetValue<string>() ?? "Local voice generation failed.");
                }
                LastElapsedMilliseconds = response["elapsedMs"]?.GetValue<int>();
            }
            finally
            {
                _gate.Release();
            }

            var play = new ProcessStartInfo("pw-play") { UseShellExecute = false };
            play.ArgumentList.Add(wav);
            _playback = Process.Start(play) ?? throw new InvalidOperationException("Could not launch pw-play.");
            await _playback.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (_playback is { HasExited: false })
            {
                _playback.Kill(entireProcessTree: true);
            }
            _playback?.Dispose();
            _playback = null;
            PipeWireRecorder.TryDelete(wav);
        }
    }

    private void EnsureWorker(string modelPath)
    {
        if (_worker is { HasExited: false } &&
            string.Equals(_loadedModel, modelPath, StringComparison.Ordinal))
        {
            return;
        }
        StopWorker();
        if (!File.Exists(_pythonExecutable) || !File.Exists(_workerScript) || !File.Exists(modelPath))
        {
            throw new FileNotFoundException(
                "Eva's local voice is not installed. Run ./scripts/setup-speech.sh.");
        }
        var info = new ProcessStartInfo(_pythonExecutable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        info.ArgumentList.Add(_workerScript);
        info.ArgumentList.Add("--piper-model");
        info.ArgumentList.Add(modelPath);
        _worker = Process.Start(info)
            ?? throw new InvalidOperationException("Could not start the local voice worker.");
        _ = _worker.StandardError.ReadToEndAsync();
        _loadedModel = modelPath;
    }

    public void StopPlayback()
    {
        if (_playback is { HasExited: false })
        {
            _playback.Kill(entireProcessTree: true);
        }
    }

    private void StopWorker()
    {
        if (_worker is { HasExited: false })
        {
            _worker.Kill(entireProcessTree: true);
        }
        _worker?.Dispose();
        _worker = null;
        _loadedModel = null;
    }

    public void Stop()
    {
        StopPlayback();
        StopWorker();
    }
}
