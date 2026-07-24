using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using EveEsi.Core;

namespace Eva.App;

public sealed class MainWindow : Window
{
    private const string CurrentPromptRevision = "ship-computer-v2";
    private readonly TextBox _transcript = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap
    };
    private readonly TextBox _input = new() { PlaceholderText = "Ask about EVE…", AcceptsReturn = true };
    private readonly TextBlock _status = new() { Text = "Starting Codex…", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _referenceStatus = new() { Text = "Reference index: checking", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _diagnostics = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MaxHeight = 160
    };
    private readonly ComboBox _character = new() { PlaceholderText = "Character", MinWidth = 170 };
    private readonly Button _record = new() { Content = "Record" };
    private readonly Button _mute = new() { Content = "Mute" };
    private readonly Button _stop = new() { Content = "Stop" };
    private readonly PipeWireRecorder _recorder = new();
    private readonly PiperSpeaker _speaker = new();
    private readonly EvaSettingsStore _settingsStore = new();
    private CodexAppServer? _codex;
    private EvaSettings _settings = EvaSettings.Default;
    private string? _threadId;
    private string? _turnId;
    private CancellationTokenSource? _turnLifetime;

    public MainWindow()
    {
        Title = "Eva — EVE Assistant";
        Width = 980;
        Height = 720;
        MinWidth = 720;
        MinHeight = 520;
        Content = BuildLayout();
        Opened += OnOpened;
        Closing += OnClosing;
        _input.KeyDown += OnInputKeyDown;
        _record.Click += OnRecord;
        _mute.Click += (_, _) =>
        {
            _settings = _settings with { Muted = !_settings.Muted };
            _mute.Content = _settings.Muted ? "Unmute" : "Mute";
            if (_settings.Muted)
            {
                _speaker.Stop();
            }
        };
        _stop.Click += async (_, _) => await StopCurrentAsync().ConfigureAwait(true);
    }

    private Control BuildLayout()
    {
        var header = new DockPanel { Margin = new Thickness(14, 12), LastChildFill = true };
        DockPanel.SetDock(_character, Dock.Left);
        header.Children.Add(_character);
        var settings = new Button { Content = "Settings", HorizontalAlignment = HorizontalAlignment.Right };
        settings.Click += (_, _) => new SettingsWindow(_settings, ApplySettingsAsync).ShowDialog(this);
        DockPanel.SetDock(settings, Dock.Right);
        header.Children.Add(settings);
        header.Children.Add(new TextBlock
        {
            Text = "  EVA",
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(14, 8)
        };
        controls.Children.Add(_record);
        controls.Children.Add(_stop);
        controls.Children.Add(_mute);
        controls.Children.Add(_status);
        controls.Children.Add(_referenceStatus);

        var send = new Button { Content = "Send", MinWidth = 90 };
        send.Click += async (_, _) => await SubmitAsync().ConfigureAwait(true);
        var composer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(14, 8, 14, 14)
        };
        Grid.SetColumn(send, 1);
        composer.Children.Add(_input);
        composer.Children.Add(send);

        var diagnosticPanel = new Expander
        {
            Header = "Diagnostics",
            IsExpanded = false,
            Content = _diagnostics,
            Margin = new Thickness(14, 2)
        };

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,Auto") };
        Grid.SetRow(_transcript, 1);
        Grid.SetRow(diagnosticPanel, 2);
        Grid.SetRow(controls, 3);
        Grid.SetRow(composer, 4);
        grid.Children.Add(header);
        grid.Children.Add(_transcript);
        grid.Children.Add(diagnosticPanel);
        grid.Children.Add(controls);
        grid.Children.Add(composer);
        return grid;
    }

    private async void OnOpened(object? sender, EventArgs eventArgs)
    {
        try
        {
            _settings = await _settingsStore.LoadAsync().ConfigureAwait(true);
            var ssoStore = new EveSsoConfigurationStore();
            var sso = await ssoStore.LoadAsync().ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(sso.ClientId) &&
                !string.IsNullOrWhiteSpace(_settings.EveClientId))
            {
                sso = new(_settings.EveClientId, _settings.CallbackUri);
                await ssoStore.SaveAsync(sso).ConfigureAwait(true);
            }
            else
            {
                _settings = _settings with
                {
                    EveClientId = sso.ClientId,
                    CallbackUri = sso.CallbackUri
                };
            }
            _mute.Content = _settings.Muted ? "Unmute" : "Mute";
            var runtime = Path.Combine(AppContext.BaseDirectory, "runtime", "codex-workspace");
            var cli = Path.Combine(AppContext.BaseDirectory, "cli");
            _codex = new CodexAppServer(runtime, cli);
            _codex.Notification += OnCodexNotification;
            await _codex.StartAsync().ConfigureAwait(true);
            _threadId = string.Equals(
                _settings.PromptRevision,
                CurrentPromptRevision,
                StringComparison.Ordinal)
                ? _settings.CodexThreadId
                : null;
            if (string.IsNullOrWhiteSpace(_threadId))
            {
                var developerInstructions = await File.ReadAllTextAsync(
                    Path.Combine(runtime, "AGENTS.md")).ConfigureAwait(true);
                var result = await _codex.RequestAsync("thread/start", new JsonObject
                {
                    ["cwd"] = runtime,
                    ["approvalPolicy"] = "never",
                    ["sandbox"] = "workspace-write",
                    ["developerInstructions"] = developerInstructions
                }).ConfigureAwait(true);
                _threadId = result?["thread"]?["id"]?.GetValue<string>()
                    ?? result?["threadId"]?.GetValue<string>();
                _settings = _settings with
                {
                    CodexThreadId = _threadId,
                    PromptRevision = CurrentPromptRevision
                };
                await _settingsStore.SaveAsync(_settings).ConfigureAwait(true);
            }
            _status.Text = "Ready";
            await RefreshCharactersAsync().ConfigureAwait(true);
            _ = UpdateStaticDataAsync();
        }
        catch (Exception exception)
        {
            _status.Text = "Codex unavailable";
            Append($"System: {exception.Message}\n");
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        _turnLifetime?.Cancel();
        await _settingsStore.SaveAsync(_settings).ConfigureAwait(true);
        await _recorder.DisposeAsync().ConfigureAwait(true);
        if (_codex is not null)
        {
            await _codex.DisposeAsync().ConfigureAwait(true);
        }
    }

    private async void OnInputKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Enter && !eventArgs.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            eventArgs.Handled = true;
            await SubmitAsync().ConfigureAwait(true);
        }
    }

    private async void OnRecord(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        try
        {
            if (!_recorder.IsRecording)
            {
                await _recorder.StartAsync().ConfigureAwait(true);
                _record.Content = "Stop & send";
                _status.Text = "Recording…";
                return;
            }
            var path = await _recorder.StopAsync().ConfigureAwait(true);
            _record.Content = "Record";
            _status.Text = "Transcribing…";
            var transcriber = new SherpaTranscriber();
            _input.Text = await transcriber.TranscribeAndDeleteAsync(path, _settings.WhisperModelDirectory)
                .ConfigureAwait(true);
            await SubmitAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _record.Content = "Record";
            _status.Text = "Voice error";
            Append($"System: {exception.Message}\n");
        }
    }

    private async Task SubmitAsync()
    {
        var prompt = _input.Text?.Trim();
        if (string.IsNullOrWhiteSpace(prompt) || _codex is null || string.IsNullOrWhiteSpace(_threadId))
        {
            return;
        }
        _input.Clear();
        Append($"\nYou: {prompt}\n\nEva: ");
        _status.Text = "Thinking…";
        _turnLifetime?.Dispose();
        _turnLifetime = new CancellationTokenSource();
        try
        {
            var result = await _codex.RequestAsync("turn/start", new JsonObject
            {
                ["threadId"] = _threadId,
                ["input"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = prompt }
                },
                ["sandboxPolicy"] = new JsonObject
                {
                    ["type"] = "workspaceWrite",
                    ["networkAccess"] = true,
                    ["writableRoots"] = new JsonArray(
                        Path.Combine(AppContext.BaseDirectory, "runtime", "codex-workspace"))
                }
            }, _turnLifetime.Token).ConfigureAwait(true);
            _turnId = result?["turn"]?["id"]?.GetValue<string>() ?? result?["turnId"]?.GetValue<string>();
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Stopped";
        }
        catch (Exception exception)
        {
            Append($"\nSystem: {exception.Message}\n");
            _status.Text = "Error";
        }
    }

    private async Task StopCurrentAsync()
    {
        _speaker.Stop();
        _turnLifetime?.Cancel();
        if (_codex is not null && _threadId is not null && _turnId is not null)
        {
            await _codex.InterruptAsync(_threadId, _turnId).ConfigureAwait(true);
        }
        _status.Text = "Stopped";
    }

    private void OnCodexNotification(object? sender, JsonNode message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var routed = CodexNotificationRouter.Route(message);
            if (routed.Kind == CodexNotificationKind.AgentText && routed.Text is not null)
            {
                Append(routed.Text);
            }
            else if (routed.Kind == CodexNotificationKind.Diagnostic && routed.Text is not null)
            {
                AppendDiagnostic(routed.Text);
            }
            else if (routed.Kind == CodexNotificationKind.TurnCompleted)
            {
                _status.Text = "Ready";
                Append("\n");
            }
        });
    }

    private void Append(string text)
    {
        _transcript.Text = (_transcript.Text ?? "") + text;
        _transcript.CaretIndex = _transcript.Text.Length;
    }

    private void AppendDiagnostic(string text)
    {
        const int maximumLength = 20_000;
        var combined = (_diagnostics.Text ?? "") + text + Environment.NewLine;
        _diagnostics.Text = combined.Length > maximumLength ? combined[^maximumLength..] : combined;
        _diagnostics.CaretIndex = _diagnostics.Text.Length;
    }

    private async Task UpdateStaticDataAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            var progress = new Progress<SdeUpdateProgress>(value =>
            {
                var percent = value.Total is > 0
                    ? $" {value.Completed * 100 / value.Total}%"
                    : "";
                _referenceStatus.Text = $"Reference index: {value.Stage}{percent}";
            });
            var result = await new SdeUpdater(http).EnsureCurrentAsync(progress: progress).ConfigureAwait(true);
            _referenceStatus.Text = result.Updated
                ? $"Reference index: build {result.BuildNumber} installed"
                : $"Reference index: build {result.BuildNumber} current";
        }
        catch (Exception exception)
        {
            _referenceStatus.Text = "Reference index: update failed";
            AppendDiagnostic($"SDE update: {SecretRedactor.Redact(exception.Message)}");
        }
    }

    private async Task ApplySettingsAsync(EvaSettings settings)
    {
        _settings = settings;
        await new EveSsoConfigurationStore().SaveAsync(
            new(settings.EveClientId, settings.CallbackUri)).ConfigureAwait(true);
        await _settingsStore.SaveAsync(settings).ConfigureAwait(true);
        await RefreshCharactersAsync().ConfigureAwait(true);
    }

    private async Task RefreshCharactersAsync()
    {
        try
        {
            var characters = await new SecretServiceTokenStore().ListAsync().ConfigureAwait(true);
            _character.ItemsSource = characters.Select(static item => item.CharacterName).ToArray();
            _character.SelectedIndex = characters.Count > 0 ? 0 : -1;
        }
        catch
        {
            _character.ItemsSource = Array.Empty<string>();
        }
    }
}

public sealed class SettingsWindow : Window
{
    public SettingsWindow(EvaSettings settings, Func<EvaSettings, Task> save)
    {
        Title = "Eva Settings";
        Width = 620;
        Height = 470;
        var clientId = Field("EVE SSO client ID", settings.EveClientId);
        var callback = Field("Loopback callback URI", settings.CallbackUri);
        var whisper = Field("Whisper small.en model directory", settings.WhisperModelDirectory);
        var piper = Field("Piper female English voice model", settings.PiperModelPath);
        var shortcut = new Button { Content = "Install Ctrl+Super+Space shortcut" };
        var shortcutStatus = new TextBlock();
        shortcut.Click += async (_, _) =>
        {
            try
            {
                await new GnomeShortcut().InstallAsync(Environment.ProcessPath ?? "eva").ConfigureAwait(true);
                shortcutStatus.Text = "Shortcut installed.";
            }
            catch (Exception exception)
            {
                shortcutStatus.Text = exception.Message;
            }
        };
        var remove = new Button { Content = "Remove shortcut" };
        remove.Click += async (_, _) =>
        {
            await new GnomeShortcut().RemoveAsync().ConfigureAwait(true);
            shortcutStatus.Text = "Shortcut removed.";
        };
        var saveButton = new Button { Content = "Save", HorizontalAlignment = HorizontalAlignment.Right };
        var linkCharacter = new Button { Content = "Link EVE character" };
        var authStatus = new TextBlock();
        linkCharacter.Click += async (_, _) =>
        {
            try
            {
                var pendingSettings = settings with
                {
                    EveClientId = clientId.Text ?? "",
                    CallbackUri = callback.Text ?? "",
                    WhisperModelDirectory = whisper.Text ?? "",
                    PiperModelPath = piper.Text ?? ""
                };
                var sso = new EveSsoConfiguration(
                    pendingSettings.EveClientId,
                    pendingSettings.CallbackUri);
                EveSsoConfigurationStore.ValidateForAuthorization(sso);
                await save(pendingSettings).ConfigureAwait(true);
                var flow = new EveAuthorizationFlow(
                    new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
                    sso.ClientId,
                    new Uri(sso.CallbackUri));
                var token = await flow.LinkAsync(BrowserLauncher.OpenAsync).ConfigureAwait(true);
                await new SecretServiceTokenStore().StoreAsync(token).ConfigureAwait(true);
                authStatus.Text = $"{token.CharacterName} linked securely.";
            }
            catch (Exception exception)
            {
                authStatus.Text = exception.Message;
            }
        };
        saveButton.Click += async (_, _) =>
        {
            await save(settings with
            {
                EveClientId = clientId.Text ?? "",
                CallbackUri = callback.Text ?? "",
                WhisperModelDirectory = whisper.Text ?? "",
                PiperModelPath = piper.Text ?? ""
            }).ConfigureAwait(true);
            Close();
        };
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Authentication", FontSize = 19 },
                clientId,
                callback,
                linkCharacter,
                authStatus,
                new TextBlock { Text = "Local speech", FontSize = 19, Margin = new Thickness(0, 8, 0, 0) },
                whisper,
                piper,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { shortcut, remove } },
                shortcutStatus,
                saveButton
            }
        };
    }

    private static TextBox Field(string label, string value) => new() { PlaceholderText = label, Text = value };
}
