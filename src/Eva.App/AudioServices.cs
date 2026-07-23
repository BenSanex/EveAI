using System.Diagnostics;

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
            _process.Kill();
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

public sealed class SherpaTranscriber(string executable = "sherpa-onnx-offline")
{
    public async Task<string> TranscribeAndDeleteAsync(
        string wavPath,
        string modelDirectory,
        CancellationToken cancellationToken = default)
    {
        try
        {
            try
            {
                return await Transcribe(wavPath, modelDirectory, "cuda", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException)
            {
                return await Transcribe(wavPath, modelDirectory, "cpu", cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            PipeWireRecorder.TryDelete(wavPath);
        }
    }

    private async Task<string> Transcribe(
        string wavPath,
        string modelDirectory,
        string provider,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        info.ArgumentList.Add("--whisper-model");
        info.ArgumentList.Add(modelDirectory);
        info.ArgumentList.Add("--provider");
        info.ArgumentList.Add(provider);
        info.ArgumentList.Add(wavPath);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not launch sherpa-onnx.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Transcription failed using {provider}: {error}");
        }
        return output.Trim();
    }
}

public sealed class PiperSpeaker(string executable = "piper")
{
    private Process? _playback;

    public async Task SpeakAsync(string text, string modelPath, CancellationToken cancellationToken = default)
    {
        Stop();
        var wav = Path.Combine(Path.GetTempPath(), $"eva-tts-{Guid.NewGuid():N}.wav");
        try
        {
            var generate = new ProcessStartInfo(executable)
            {
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            generate.ArgumentList.Add("--model");
            generate.ArgumentList.Add(modelPath);
            generate.ArgumentList.Add("--output_file");
            generate.ArgumentList.Add(wav);
            using (var process = Process.Start(generate) ?? throw new InvalidOperationException("Could not launch Piper."))
            {
                await process.StandardInput.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
                }
            }

            var play = new ProcessStartInfo("pw-play") { UseShellExecute = false };
            play.ArgumentList.Add(wav);
            _playback = Process.Start(play) ?? throw new InvalidOperationException("Could not launch pw-play.");
            await _playback.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _playback?.Dispose();
            _playback = null;
            PipeWireRecorder.TryDelete(wav);
        }
    }

    public void Stop()
    {
        if (_playback is { HasExited: false })
        {
            _playback.Kill();
        }
    }
}
