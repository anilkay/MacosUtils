using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using MacosUtils.AI;
using MacosUtils.Core.Logging;

namespace MacosUtils.App.Views;

/// <summary>
/// AI Chat: talk to an agent (any OpenAI-compatible endpoint) that can inspect this Mac through the
/// read-only tools of <see cref="PcTools"/>. Reading a file or an environment variable, and network
/// probes, ask first (No is the default). Can also host the tools as a local MCP server. A thin UI
/// over <see cref="ChatSession"/>, <see cref="ChatSettingsStore"/> and <see cref="CredentialStore"/>.
/// </summary>
public sealed class ChatView : UtilityView, IDisposable
{
    private static readonly string[] Efforts = ["Default", "Minimal", "Low", "Medium", "High"];
    private static ILogger Log => AppLog.Logger<ChatView>();

    private readonly TextBox _endpointBox = new() { Text = "https://api.openai.com/v1", Width = 260 };
    private readonly TextBox _apiKeyBox = new() { Width = 180, PasswordChar = '•', PlaceholderText = "API key" };
    private readonly ComboBox _modelBox = new() { Width = 220, IsEditable = true, Text = "gpt-4o-mini" };
    private readonly Button _loadModelsButton = new() { Content = "Load models" };
    private readonly ComboBox _reasoningBox = new() { ItemsSource = Efforts, SelectedIndex = 0, Width = 110 };
    private readonly CheckBox _rememberCheck = new() { Content = "Remember", IsChecked = true };
    private readonly Button _forgetButton = new() { Content = "Forget" };
    private readonly Button _mcpButton = new() { Content = "Start MCP server" };
    private readonly ChatTranscript _transcript = new();
    private readonly TextBox _inputBox = new()
    {
        AcceptsReturn = true,
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        MinHeight = 52,
        MaxHeight = 140,
        PlaceholderText = "Type a message... (Return to send, Shift-Return for a new line)",
    };
    private readonly Button _sendButton = AccentButton("Send");
    private readonly Button _stopButton = new() { Content = "Stop", IsEnabled = false };
    private readonly Button _newChatButton = new() { Content = "New chat" };
    private readonly Button _clearButton = new() { Content = "Clear" };
    private readonly Button _exportButton = new() { Content = "Export..." };
    private readonly TextBlock _status = StatusLine("Not connected.");
    private readonly DispatcherTimer _activityTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly System.Diagnostics.Stopwatch _activityWatch = new();

    private ChatSession? _chatSession;
    private string? _sessionCacheKey;
    private McpServerHost? _mcpHost;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _modelsCts;
    private string? _activityText;
    private string _apiKeyHost = "";

    public ChatView()
    {
        _modelBox.ItemsSource = new List<string>();

        var settings = new WrapPanel();
        settings.Classes.Add("toolbar");
        foreach (var control in new Control[]
                 {
                     Label("Endpoint:"), _endpointBox, Label("API key:"), _apiKeyBox, Label("Model:"), _modelBox, _loadModelsButton,
                     Label("Reasoning:"), _reasoningBox, _rememberCheck, _forgetButton, _mcpButton,
                 })
            settings.Children.Add(control);
        DockPanel.SetDock(settings, Dock.Top);

        var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(_newChatButton);
        actions.Children.Add(_clearButton);
        actions.Children.Add(_exportButton);
        DockPanel.SetDock(actions, Dock.Bottom);

        var input = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        _sendButton.Margin = new Thickness(8, 0, 0, 0);
        _stopButton.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_sendButton, 1);
        Grid.SetColumn(_stopButton, 2);
        input.Children.Add(_inputBox);
        input.Children.Add(_sendButton);
        input.Children.Add(_stopButton);
        DockPanel.SetDock(input, Dock.Bottom);

        Content = new DockPanel { Children = { settings, _status, actions, input, _transcript } };

        _sendButton.Click += async (_, _) => await SendAsync();
        _stopButton.Click += (_, _) => _cts?.Cancel();
        _newChatButton.Click += (_, _) => NewChat();
        _clearButton.Click += (_, _) => _transcript.ClearTranscript();
        _exportButton.Click += async (_, _) => await ExportChatAsync();
        _forgetButton.Click += (_, _) => ForgetSettings();
        _loadModelsButton.Click += async (_, _) => await LoadModelsAsync();
        _mcpButton.Click += async (_, _) => await ToggleMcpServerAsync();
        _endpointBox.LostFocus += (_, _) => OnEndpointChanged();
        _inputBox.KeyDown += (_, e) =>
        {
            // Return sends; Shift-Return (and the IME's Return while composing) inserts a new line.
            if (e.Key == Key.Return && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                e.Handled = true;
                _ = SendAsync();
            }
        };
        _activityTimer.Tick += (_, _) => ShowActivity();

        if (!CredentialStore.IsSupported)
            _rememberCheck.IsEnabled = false;
        LoadPersistedSettings();
        _transcript.AddNote("The assistant uses read-only tools to look at this Mac. Reading a file or a variable, "
            + "and network probes, ask for your permission each time. Nothing is ever changed.");
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _modelsCts?.Cancel();
        _activityTimer.Stop();
        if (_mcpHost is { } host)
        {
            _mcpHost = null;
            _ = host.StopAsync();
        }
    }

    private void NewChat()
    {
        _cts?.Cancel();
        _chatSession?.Reset();
        _transcript.ClearTranscript();
        _status.Text = "New conversation started.";
    }

    // ---- settings ----

    private void LoadPersistedSettings()
    {
        try
        {
            if (ChatSettingsStore.Load() is { } settings)
            {
                if (!string.IsNullOrWhiteSpace(settings.Endpoint))
                    _endpointBox.Text = settings.Endpoint;
                if (!string.IsNullOrWhiteSpace(settings.Model))
                    _modelBox.Text = settings.Model;
                var index = Array.IndexOf(Efforts, settings.ReasoningEffort);
                if (index >= 0)
                    _reasoningBox.SelectedIndex = index;
            }
            OnEndpointChanged();
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Could not load persisted chat settings; starting fresh.");
        }
    }

    /// <summary>The key in the box follows the endpoint's host: on a host change, that host's saved
    /// key (if any) replaces the text, else the box is emptied, so a key saved for one endpoint can
    /// never be sent to another one.</summary>
    private void OnEndpointChanged()
    {
        var host = EndpointHost();
        if (host == _apiKeyHost)
            return;
        _apiKeyHost = host;
        if (host.Length == 0)
            return;
        var key = CredentialStore.Load(host);
        if (key is not null)
        {
            _apiKeyBox.Text = key;
            _status.Text = "Loaded the saved API key for this endpoint from the Keychain.";
        }
        else if (!string.IsNullOrEmpty(_apiKeyBox.Text))
        {
            _apiKeyBox.Text = "";
            _status.Text = "The API key box was emptied: keys are stored per endpoint host.";
        }
    }

    private string EndpointHost() =>
        Uri.TryCreate((_endpointBox.Text ?? "").Trim().TrimEnd('/'), UriKind.Absolute, out var uri) ? uri.Host : "";

    private void SaveSettings()
    {
        try
        {
            ChatSettingsStore.Save(new ChatSettings((_endpointBox.Text ?? "").Trim(), (_modelBox.Text ?? "").Trim(),
                Efforts[Math.Max(0, _reasoningBox.SelectedIndex)]));
            var key = (_apiKeyBox.Text ?? "").Trim();
            var host = EndpointHost();
            if (key.Length > 0 && host.Length > 0 && CredentialStore.IsSupported)
                CredentialStore.Save(key, host);
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not save settings: {ex.Message}";
        }
    }

    private void ForgetSettings()
    {
        try
        {
            ChatSettingsStore.Delete();
            CredentialStore.Delete(EndpointHost());
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Could not forget the saved settings.");
        }
        _apiKeyBox.Text = "";
        _chatSession = null;
        _sessionCacheKey = null;
        _status.Text = "Saved settings and the Keychain item were removed.";
    }

    private ReasoningEffort SelectedEffort() =>
        Enum.TryParse<ReasoningEffort>(Efforts[Math.Max(0, _reasoningBox.SelectedIndex)], out var effort) ? effort : ReasoningEffort.Default;

    /// <summary>The settings as typed on screen, validated; throws with a message to show when incomplete.</summary>
    private ChatAgentOptions BuildAgentOptions()
    {
        var endpoint = (_endpointBox.Text ?? "").Trim().TrimEnd('/');
        var model = (_modelBox.Text ?? "").Trim();
        var key = (_apiKeyBox.Text ?? "").Trim();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out _))
            throw new InvalidOperationException("Enter a valid endpoint URL (e.g. https://api.openai.com/v1).");
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("Enter a model name (e.g. gpt-4o-mini).");
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Enter an API key.");
        return new ChatAgentOptions(endpoint, model, key, SelectedEffort());
    }

    private ChatSession EnsureSession()
    {
        var options = BuildAgentOptions();
        var cacheKey = $"{options.Endpoint}|{options.Model}|{options.ApiKey}|{options.Reasoning}";
        if (_chatSession is not null && cacheKey == _sessionCacheKey)
            return _chatSession;
        _chatSession = ChatSession.Create(options);
        _sessionCacheKey = cacheKey;
        return _chatSession;
    }

    // ---- models ----

    /// <summary>Fills the model drop-down from the endpoint's /models list. On failure the list stays
    /// as it was and the model name can still be typed by hand.</summary>
    private async Task LoadModelsAsync()
    {
        _modelsCts?.Cancel();
        _modelsCts = new CancellationTokenSource();
        var token = _modelsCts.Token;
        _loadModelsButton.IsEnabled = false;
        _status.Text = "Loading models...";
        try
        {
            var models = await ModelCatalog.ListModelsAsync(_endpointBox.Text ?? "", _apiKeyBox.Text ?? "", token);
            var current = _modelBox.Text;
            _modelBox.ItemsSource = models.ToList();
            _modelBox.Text = current;
            _status.Text = models.Count == 0
                ? "The endpoint returned no models. Type the model name by hand."
                : $"Loaded {models.Count} model(s). Pick one or type a name.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Could not list models.");
            _status.Text = ChatSession.IsUnauthorizedError(ex)
                ? "Could not list models: the endpoint rejected the API key (HTTP 401/403). You can still type a model name."
                : $"Could not list models ({ex.Message}). You can still type a model name.";
        }
        finally
        {
            _loadModelsButton.IsEnabled = true;
        }
    }

    // ---- sending ----

    private async Task SendAsync()
    {
        var prompt = (_inputBox.Text ?? "").Trim();
        if (prompt.Length == 0 || !_sendButton.IsEnabled)
            return;

        ChatSession session;
        try
        {
            session = EnsureSession();
        }
        catch (Exception ex)
        {
            await Dialogs.ShowAsync(this, "AI Chat", ex.Message);
            return;
        }
        if (_rememberCheck.IsChecked == true)
            SaveSettings();

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _sendButton.IsEnabled = false;
        _stopButton.IsEnabled = true;
        _inputBox.Text = "";
        _transcript.AddUserMessage(prompt);
        _transcript.BeginAssistantMessage();
        SetActivity(new ChatStreamUpdate(ChatActivity.Waiting));
        _activityTimer.Start();

        try
        {
            try
            {
                await StreamAsync(session, prompt, token);
            }
            catch (Exception ex) when (ChatSession.IsReasoningEffortError(ex) && SelectedEffort() != ReasoningEffort.Default)
            {
                // The model rejects reasoning_effort: drop it and retry once instead of failing.
                Log.LogInformation("Reasoning effort rejected by the model; retrying without it.");
                _reasoningBox.SelectedIndex = 0;
                if (_rememberCheck.IsChecked == true)
                    SaveSettings();
                session.DisableReasoningEffort();
                _transcript.EndAssistantMessage();
                _transcript.AddNote("The model does not support reasoning_effort, retrying without it.");
                _transcript.BeginAssistantMessage();
                SetActivity(new ChatStreamUpdate(ChatActivity.Waiting));
                await StreamAsync(session, prompt, token);
            }
            _transcript.EndAssistantMessage();
            _status.Text = "Ready.";
        }
        catch (OperationCanceledException)
        {
            _transcript.EndAssistantMessage();
            _transcript.AddNote("Cancelled.");
            _status.Text = "Cancelled.";
        }
        catch (TimeoutException ex)
        {
            // The endpoint went silent. The session is still valid (the failed turn is not in its
            // history), so sending again continues the same conversation.
            Log.LogWarning(ex, "The endpoint went silent (response timeout).");
            _transcript.EndAssistantMessage();
            _transcript.AddError($"No response: {ex.Message}");
            _status.Text = "No response from the endpoint.";
        }
        catch (Exception ex)
        {
            // A failed turn can leave the session unusable (e.g. a 400 mid-run): drop it.
            Log.LogError(ex, "Chat request failed.");
            session.Reset();
            _transcript.EndAssistantMessage();
            _transcript.AddError(ChatSession.IsUnauthorizedError(ex)
                ? "Authorization failed: the endpoint rejected the API key (HTTP 401/403). Check that the key is correct and belongs to this endpoint/model."
                : $"Error: {ex.GetType().Name}: {ex.Message}");
            _status.Text = "Error — check endpoint/model/key.";
        }
        finally
        {
            _activityTimer.Stop();
            _activityWatch.Reset();
            _activityText = null;
            _sendButton.IsEnabled = true;
            _stopButton.IsEnabled = false;
        }
    }

    private async Task StreamAsync(ChatSession session, string prompt, CancellationToken token)
    {
        await foreach (var update in session.StreamResponseAsync(prompt, ApproveToolCallAsync, token))
        {
            if (update.Text is not null)
                _transcript.AppendAssistantText(update.Text);
            SetActivity(update);
        }
    }

    /// <summary>Asks before a tool reads a file's text or an environment variable, or probes the network. No is the default.</summary>
    private async Task<bool> ApproveToolCallAsync(ToolApprovalRequest request, CancellationToken token)
    {
        // The MCP server asks from a Kestrel thread: hop to the UI thread first.
        if (!Dispatcher.UIThread.CheckAccess())
            return await Dispatcher.UIThread.InvokeAsync(() => ApproveToolCallAsync(request, token));
        _activityText = $"Waiting for your permission to run {request.ToolName}...";
        _activityWatch.Restart();
        ShowActivity();
        return await Dialogs.ConfirmAsync(this, "AI Chat", request.Prompt, "Allow", "Don't Allow", defaultNo: true);
    }

    private void SetActivity(ChatStreamUpdate update)
    {
        var text = update.Activity switch
        {
            ChatActivity.Thinking => "The model is thinking...",
            ChatActivity.RunningTool => $"Running tool {update.ToolName}...",
            ChatActivity.Writing => "Writing...",
            _ => "Waiting for the model...",
        };
        if (text != _activityText)
        {
            _activityText = text;
            _activityWatch.Restart();
        }
        ShowActivity();
    }

    private void ShowActivity()
    {
        if (_activityText is null)
            return;
        var seconds = (int)_activityWatch.Elapsed.TotalSeconds;
        _status.Text = seconds > 0 ? $"{_activityText} ({seconds} s)" : _activityText;
    }

    // ---- export and MCP ----

    private async Task ExportChatAsync()
    {
        if (!_transcript.HasMessages)
        {
            _status.Text = "Nothing to export.";
            return;
        }
        if (TopLevel.GetTopLevel(this) is not { } top)
            return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export chat",
            SuggestedFileName = $"chat-{DateTime.Now:yyyyMMdd-HHmmss}.md",
            DefaultExtension = "md",
            FileTypeChoices = [new FilePickerFileType("Markdown") { Patterns = ["*.md"] }],
        });
        if (file?.TryGetLocalPath() is not { } path)
            return;
        try
        {
            var header = $"# macOS Utils AI Chat export — {DateTime.Now:F}\n\nModel: {(_modelBox.Text ?? "").Trim()}\n\n";
            await File.WriteAllTextAsync(path, header + _transcript.ToMarkdown());
            _status.Text = $"Chat exported to {ShortenHome(path)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowAsync(this, "Export", $"Could not export chat: {ex.Message}");
        }
    }

    /// <summary>Starts or stops the local MCP server that exposes the agent and the read-only tools
    /// to other apps on this Mac. Uses the settings currently on screen.</summary>
    private async Task ToggleMcpServerAsync()
    {
        if (_mcpHost is not null)
        {
            var host = _mcpHost;
            _mcpHost = null;
            _mcpButton.IsEnabled = false;
            try
            {
                await host.StopAsync();
                _transcript.AddNote("MCP server stopped.");
                _status.Text = "MCP server stopped.";
            }
            catch (Exception ex)
            {
                _status.Text = $"Could not stop the MCP server: {ex.Message}";
            }
            finally
            {
                _mcpButton.IsEnabled = true;
                _mcpButton.Content = "Start MCP server";
            }
            return;
        }

        McpServerHostOptions options;
        try
        {
            options = new McpServerHostOptions(BuildAgentOptions(), ApproveToolCallAsync);
        }
        catch (Exception ex)
        {
            await Dialogs.ShowAsync(this, "AI Chat", ex.Message);
            return;
        }
        if (_rememberCheck.IsChecked == true)
            SaveSettings();

        _mcpButton.IsEnabled = false;
        _status.Text = "Starting MCP server...";
        try
        {
            _mcpHost = await McpServerHost.StartAsync(options);
            _mcpButton.Content = "Stop MCP server";
            _transcript.AddNote($"MCP server running at {_mcpHost.Url} — it exposes the read-only tools and the AI agent; "
                + "the bearer token was shown in a dialog and is new on every start.");
            _status.Text = $"MCP server: {_mcpHost.Url}";
            await Dialogs.ShowTextAsync(this, "MCP Server",
                "Add this entry to the mcp.json of an MCP client on this Mac (e.g. VS Code). The token is new every time the "
                + "server starts, so update the client after restarting it. The server only accepts connections from this Mac "
                + "and stops when the button is clicked again.",
                _mcpHost.ClientConfigJson);
        }
        catch (Exception ex)
        {
            _status.Text = "Could not start the MCP server.";
            Log.LogError(ex, "Could not start the MCP server.");
            await Dialogs.ShowAsync(this, "AI Chat", $"Could not start the MCP server: {ex.Message}");
        }
        finally
        {
            _mcpButton.IsEnabled = true;
        }
    }
}
