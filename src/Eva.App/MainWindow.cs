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
    private readonly TextBox _input = new()
    {
        PlaceholderText = "ENTER PILOT QUERY…",
        AcceptsReturn = true
    };
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
    private readonly TextBlock _clock = new()
    {
        FontSize = 11,
        Foreground = new SolidColorBrush(Color.Parse("#3EA2BD")),
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly DispatcherTimer _hudClock = new() { Interval = TimeSpan.FromSeconds(1) };
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
        Title = "EVA // SHIPBOARD INTELLIGENCE";
        Width = 1120;
        Height = 780;
        MinWidth = 780;
        MinHeight = 580;
        Background = Brushes.Transparent;
        TransparencyLevelHint =
        [
            WindowTransparencyLevel.AcrylicBlur,
            WindowTransparencyLevel.Blur,
            WindowTransparencyLevel.Transparent
        ];
        _transcript.Background = Brushes.Transparent;
        _transcript.BorderThickness = new Thickness(0);
        _transcript.FontSize = 16;
        _transcript.Padding = new Thickness(8);
        _input.MinHeight = 58;
        _input.FontSize = 15;
        _input.Padding = new Thickness(14, 10);
        _diagnostics.Background = Brushes.Transparent;
        _diagnostics.BorderThickness = new Thickness(0);
        _diagnostics.Foreground = new SolidColorBrush(Color.Parse("#70BCD0"));
        _status.Foreground = new SolidColorBrush(Color.Parse("#79E7FF"));
        _referenceStatus.Foreground = new SolidColorBrush(Color.Parse("#559CB1"));
        Content = BuildLayout();
        Opened += OnOpened;
        Closing += OnClosing;
        _input.KeyDown += OnInputKeyDown;
        _record.Click += OnRecord;
        _mute.Click += (_, _) =>
        {
            _settings = _settings with { Muted = !_settings.Muted };
            _mute.Content = _settings.Muted ? "◇  AUDIO ON" : "◇  AUDIO";
            if (_settings.Muted)
            {
                _speaker.Stop();
            }
        };
        _stop.Click += async (_, _) => await StopCurrentAsync().ConfigureAwait(true);
        _hudClock.Tick += (_, _) => _clock.Text = DateTimeOffset.Now.ToString("'LOCAL // 'HH:mm:ss");
        _clock.Text = DateTimeOffset.Now.ToString("'LOCAL // 'HH:mm:ss");
        _hudClock.Start();
    }

    private Control BuildLayout()
    {
        _character.HorizontalAlignment = HorizontalAlignment.Center;
        _character.MinWidth = 230;
        var settings = new Button { Content = "SYSTEM CONFIG", HorizontalAlignment = HorizontalAlignment.Right };
        settings.Click += (_, _) => new SettingsWindow(_settings, ApplySettingsAsync).ShowDialog(this);

        var brand = new StackPanel { Spacing = -2 };
        brand.Children.Add(new TextBlock
        {
            Text = "E V A",
            FontSize = 29,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#74E7FF"))
        });
        brand.Children.Add(new TextBlock
        {
            Text = "SHIPBOARD INTELLIGENCE  //  ONLINE",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse("#348CA8"))
        });
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(20, 13)
        };
        Grid.SetColumn(_character, 1);
        var rightHeader = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            Children = { _clock, settings }
        };
        Grid.SetColumn(rightHeader, 2);
        header.Children.Add(brand);
        header.Children.Add(_character);
        header.Children.Add(rightHeader);

        _record.Content = "●  RECORD";
        _stop.Content = "■  ABORT";
        _mute.Content = "◇  AUDIO";
        _record.Margin = new Thickness(0, 0, 7, 0);
        _stop.Margin = new Thickness(0, 0, 7, 0);
        var controls = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        controls.Margin = new Thickness(4, 2);
        controls.Children.Add(_record);
        controls.Children.Add(_stop);
        controls.Children.Add(_mute);
        controls.Children.Add(StatusChip("CORE", _status));
        controls.Children.Add(StatusChip("SDE", _referenceStatus));

        var send = new Button { Content = "TRANSMIT  ›", MinWidth = 125, Margin = new Thickness(10, 0, 0, 0) };
        send.Click += async (_, _) => await SubmitAsync().ConfigureAwait(true);
        var composer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(8, 4)
        };
        Grid.SetColumn(send, 1);
        composer.Children.Add(_input);
        composer.Children.Add(send);

        var diagnosticPanel = new Expander
        {
            Header = "▸ AUXILIARY TELEMETRY / DIAGNOSTICS",
            IsExpanded = false,
            Content = _diagnostics,
            Foreground = new SolidColorBrush(Color.Parse("#4BA8C2")),
            Margin = new Thickness(10, 2)
        };

        var interfaceGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,Auto"),
            Margin = new Thickness(18)
        };
        var transcriptPanel = HudPanel(_transcript, "TACTICAL DIALOGUE  /  COMMS CHANNEL 01");
        var controlPanel = HudPanel(controls, "FLIGHT INTERFACE");
        var composerPanel = HudPanel(composer, "COMMAND UPLINK");
        Grid.SetRow(transcriptPanel, 1);
        Grid.SetRow(diagnosticPanel, 2);
        Grid.SetRow(controlPanel, 3);
        Grid.SetRow(composerPanel, 4);
        interfaceGrid.Children.Add(HudPanel(header, "PILOT INTERFACE  /  ESI READ-ONLY"));
        interfaceGrid.Children.Add(transcriptPanel);
        interfaceGrid.Children.Add(diagnosticPanel);
        interfaceGrid.Children.Add(controlPanel);
        interfaceGrid.Children.Add(composerPanel);

        var root = new Grid();
        root.Children.Add(new HudBackdrop { IsHitTestVisible = false });
        root.Children.Add(interfaceGrid);
        return root;
    }

    private static Control HudPanel(Control content, string label)
    {
        var chrome = new HudChrome { IsHitTestVisible = false };
        var panel = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Margin = new Thickness(4)
        };
        Grid.SetRowSpan(chrome, 2);
        var title = new TextBlock
        {
            Text = label,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#399DB9")),
            Margin = new Thickness(18, 9, 18, 0)
        };
        Grid.SetRow(content, 1);
        content.Margin = new Thickness(
            content.Margin.Left + 12,
            content.Margin.Top + 6,
            content.Margin.Right + 12,
            content.Margin.Bottom + 10);
        panel.Children.Add(chrome);
        panel.Children.Add(title);
        panel.Children.Add(content);
        return panel;
    }

    private static Control StatusChip(string name, TextBlock value)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto"),
            Margin = new Thickness(14, 5, 0, 5)
        };
        var label = new TextBlock
        {
            Text = name + " // ",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse("#2E7F97")),
            VerticalAlignment = VerticalAlignment.Center
        };
        value.FontSize = 11;
        Grid.SetColumn(value, 1);
        grid.Children.Add(label);
        grid.Children.Add(value);
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
            _mute.Content = _settings.Muted ? "◇  AUDIO ON" : "◇  AUDIO";
            var runtime = Path.Combine(AppContext.BaseDirectory, "runtime", "codex-workspace");
            var cli = Path.Combine(AppContext.BaseDirectory, "cli");
            _codex = new CodexAppServer(runtime, cli);
            _codex.Notification += OnCodexNotification;
            await _codex.StartAsync().ConfigureAwait(true);
            await EnsureThreadAsync(runtime).ConfigureAwait(true);
            _status.Text = "Ready";
            await RefreshCharactersAsync().ConfigureAwait(true);
            _ = UpdateStaticDataAsync();
            _input.Focus();
        }
        catch (Exception exception)
        {
            _status.Text = "Codex unavailable";
            Append($"System: {exception.Message}\n");
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        _hudClock.Stop();
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
                _record.Content = "●  CAPTURE / SEND";
                _status.Text = "Recording…";
                return;
            }
            var path = await _recorder.StopAsync().ConfigureAwait(true);
            _record.Content = "●  RECORD";
            _status.Text = "Transcribing…";
            var transcriber = new SherpaTranscriber();
            _input.Text = await transcriber.TranscribeAndDeleteAsync(path, _settings.WhisperModelDirectory)
                .ConfigureAwait(true);
            await SubmitAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _record.Content = "●  RECORD";
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
            await StartTurnAsync(prompt, _turnLifetime.Token).ConfigureAwait(true);
        }
        catch (Exception exception) when (IsThreadNotFound(exception))
        {
            try
            {
                AppendDiagnostic("Saved Codex thread was unavailable; created a replacement.");
                await CreateThreadAsync(RuntimeWorkspace()).ConfigureAwait(true);
                await StartTurnAsync(prompt, _turnLifetime.Token).ConfigureAwait(true);
            }
            catch (Exception retryException)
            {
                Append($"\nSystem: {retryException.Message}\n");
                _status.Text = "Error";
            }
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

    private async Task EnsureThreadAsync(string runtime)
    {
        _threadId = string.Equals(
            _settings.PromptRevision,
            CurrentPromptRevision,
            StringComparison.Ordinal)
            ? _settings.CodexThreadId
            : null;
        if (!string.IsNullOrWhiteSpace(_threadId) && _codex is not null)
        {
            try
            {
                var developerInstructions = await File.ReadAllTextAsync(
                    Path.Combine(runtime, "AGENTS.md")).ConfigureAwait(true);
                var resumed = await _codex.RequestAsync("thread/resume", new JsonObject
                {
                    ["threadId"] = _threadId,
                    ["cwd"] = runtime,
                    ["model"] = _settings.CodexModel,
                    ["approvalPolicy"] = "never",
                    ["sandbox"] = "workspace-write",
                    ["developerInstructions"] = developerInstructions
                }).ConfigureAwait(true);
                _threadId = resumed?["thread"]?["id"]?.GetValue<string>() ?? _threadId;
                return;
            }
            catch (Exception exception) when (IsThreadNotFound(exception))
            {
                AppendDiagnostic("Saved Codex thread was not found; starting a new dialogue.");
                _threadId = null;
            }
        }
        await CreateThreadAsync(runtime).ConfigureAwait(true);
    }

    private async Task CreateThreadAsync(string runtime)
    {
        if (_codex is null)
        {
            throw new InvalidOperationException("Codex app-server is unavailable.");
        }
        var developerInstructions = await File.ReadAllTextAsync(
            Path.Combine(runtime, "AGENTS.md")).ConfigureAwait(true);
        var result = await _codex.RequestAsync("thread/start", new JsonObject
        {
            ["cwd"] = runtime,
            ["model"] = _settings.CodexModel,
            ["approvalPolicy"] = "never",
            ["sandbox"] = "workspace-write",
            ["developerInstructions"] = developerInstructions
        }).ConfigureAwait(true);
        _threadId = result?["thread"]?["id"]?.GetValue<string>()
            ?? result?["threadId"]?.GetValue<string>()
            ?? throw new InvalidDataException("Codex did not return a thread ID.");
        _settings = _settings with
        {
            CodexThreadId = _threadId,
            PromptRevision = CurrentPromptRevision
        };
        await _settingsStore.SaveAsync(_settings).ConfigureAwait(true);
    }

    private async Task StartTurnAsync(string prompt, CancellationToken cancellationToken)
    {
        if (_codex is null || string.IsNullOrWhiteSpace(_threadId))
        {
            throw new InvalidOperationException("Codex thread is unavailable.");
        }
        var result = await _codex.RequestAsync("turn/start", new JsonObject
        {
            ["threadId"] = _threadId,
            ["model"] = _settings.CodexModel,
            ["effort"] = _settings.CodexReasoningEffort,
            ["input"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = prompt }
            },
            ["sandboxPolicy"] = new JsonObject
            {
                ["type"] = "workspaceWrite",
                ["networkAccess"] = true,
                ["writableRoots"] = new JsonArray(RuntimeWorkspace())
            }
        }, cancellationToken).ConfigureAwait(true);
        _turnId = result?["turn"]?["id"]?.GetValue<string>() ?? result?["turnId"]?.GetValue<string>();
    }

    public static bool IsThreadNotFound(Exception exception) =>
        exception.Message.Contains("thread not found", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("thread_not_found", StringComparison.OrdinalIgnoreCase);

    private static string RuntimeWorkspace() =>
        Path.Combine(AppContext.BaseDirectory, "runtime", "codex-workspace");

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
        Title = "EVA // SYSTEM CONFIGURATION";
        Width = 620;
        Height = 560;
        Background = new SolidColorBrush(Color.Parse("#061522"));
        var clientId = Field("EVE SSO client ID", settings.EveClientId);
        var callback = Field("Loopback callback URI", settings.CallbackUri);
        var whisper = Field("Whisper small.en model directory", settings.WhisperModelDirectory);
        var piper = Field("Piper female English voice model", settings.PiperModelPath);
        var model = new ComboBox
        {
            ItemsSource = new[] { "gpt-5.6-luna", "gpt-5.6-terra", "gpt-5.6-sol" },
            SelectedItem = settings.CodexModel,
            MinWidth = 220
        };
        var reasoning = new ComboBox
        {
            ItemsSource = new[] { "low", "medium", "high" },
            SelectedItem = settings.CodexReasoningEffort,
            MinWidth = 140
        };
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
                    PiperModelPath = piper.Text ?? "",
                    CodexModel = model.SelectedItem?.ToString() ?? EvaSettings.Default.CodexModel,
                    CodexReasoningEffort = reasoning.SelectedItem?.ToString() ??
                        EvaSettings.Default.CodexReasoningEffort
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
                PiperModelPath = piper.Text ?? "",
                CodexModel = model.SelectedItem?.ToString() ?? EvaSettings.Default.CodexModel,
                CodexReasoningEffort = reasoning.SelectedItem?.ToString() ??
                    EvaSettings.Default.CodexReasoningEffort
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
                new TextBlock { Text = "Codex model and reasoning", FontSize = 19, Margin = new Thickness(0, 8, 0, 0) },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { model, reasoning }
                },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { shortcut, remove } },
                shortcutStatus,
                saveButton
            }
        };
    }

    private static TextBox Field(string label, string value) => new() { PlaceholderText = label, Text = value };
}
