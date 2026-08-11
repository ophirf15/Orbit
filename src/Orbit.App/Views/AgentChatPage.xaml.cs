using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Orbit.Agent.Contracts.Hermes;
using Orbit.Core.Settings;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Hermes;
using Orbit_App.Services;
using Orbit_App.ViewModels;

namespace Orbit_App.Views;

public sealed partial class AgentChatPage : Page
{
    private readonly ObservableCollection<ChatBubbleVm> _messages = [];
    private readonly ObservableCollection<OperatorRuleVm> _operatorRules = [];
    private readonly ObservableCollection<OperatorMemoryVm> _operatorMemory = [];
    private ConversationRecord? _conversation;
    private string? _sessionId;
    private string? _sessionKey;
    private bool _busy;
    private ChatBubbleVm? _statusBubble;
    private DispatcherTimer? _idleTimer;
    private int _idleTick;
    private ScrollViewer? _messageScrollViewer;
    private DateTimeOffset _lastStreamScrollUtc = DateTimeOffset.MinValue;

    public AgentChatPage()
    {
        InitializeComponent();
        MessageList.ItemsSource = _messages;
        OperatorRuleList.ItemsSource = _operatorRules;
        OperatorMemoryList.ItemsSource = _operatorMemory;
        Loaded += OnLoaded;
        Unloaded += (_, _) => StopIdleTimer();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            OrbitLocalData.EnsureOpened();
            var existing = OrbitLocalData.Conversations.GetLatestDesktop();
            using var client = CreateClient();
            var session = await client.EnsureSessionAsync(
                existing?.HermesSessionId,
                existing?.HermesSessionKey);

            _sessionId = session.SessionId;
            _sessionKey = session.SessionKey;
            _conversation = OrbitLocalData.Conversations.CreateOrResumeDesktop(
                session.SessionId,
                session.SessionKey);

            _messages.Clear();
            foreach (var msg in OrbitLocalData.Conversations.ListMessages(_conversation.Id))
            {
                _messages.Add(ChatBubbleVm.FromStore(msg));
            }

            SessionStatusText.Text =
                $"Hermes session: {_sessionId}" +
                (session.PersistedRemotely ? " (remote)" : " (local header)") +
                $" · Orbit conversation: {_conversation.Id}" +
                $" · channel: {_conversation.Channel}";
            StatusText.Text = string.Empty;
        }
        catch (Exception ex)
        {
            SessionStatusText.Text = "Could not start Hermes session.";
            StatusText.Text = ex.Message;
        }

        await RefreshOperatorAsync();
    }

    private async void RefreshOperator_Click(object sender, RoutedEventArgs e) =>
        await RefreshOperatorAsync();

    private void OpenHermesDashboard_Click(object sender, RoutedEventArgs e)
    {
        Frame?.Navigate(typeof(HermesDashboardPage));
    }

    private async Task RefreshOperatorAsync()
    {
        try
        {
            using var core = new CoreHostClient(App.Settings, App.SettingsStore);
            var dash = await core.GetOperatorDashboardAsync();
            _operatorRules.Clear();
            _operatorMemory.Clear();
            if (dash is null)
            {
                OperatorStatusText.Text = "Core unreachable — operator rail unavailable.";
                return;
            }

            BriefingText.Text = string.IsNullOrWhiteSpace(dash.LatestBriefing)
                ? "No duty briefing yet. Push mail with Ctrl+Shift+O — results also show on the Workbench banner."
                : dash.LatestBriefing;
            OperatorStatusText.Text =
                $"{dash.LatestRunStatus ?? "idle"} · {dash.LatestTrigger ?? "—"} · {dash.Rules.Count} rule(s) · review unmatched mail on Pulse";

            foreach (var r in dash.Rules)
            {
                _operatorRules.Add(r);
            }

            foreach (var m in dash.Memory.Take(20))
            {
                _operatorMemory.Add(m);
            }
        }
        catch (Exception ex)
        {
            OperatorStatusText.Text = $"Operator error: {ex.Message}";
        }
    }

    private async void ProbeButton_Click(object sender, RoutedEventArgs e)
    {
        InputBox.Text = "What can you see right now in Orbit? Summarize route, focused project, workbench projects, and which Orbit tools you can use.";
        await SendMessageCoreAsync(InputBox.Text);
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        var text = InputBox.Text?.Trim() ?? string.Empty;
        InputBox.Text = string.Empty;
        await SendMessageCoreAsync(text);
    }

    private void InputBox_PreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
        {
            return;
        }

        var shift = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (shift)
        {
            return;
        }

        e.Handled = true;
        var text = InputBox.Text?.Trim() ?? string.Empty;
        InputBox.Text = string.Empty;
        _ = SendMessageCoreAsync(text);
    }

    private async Task SendMessageCoreAsync(string text)
    {
        if (_busy)
        {
            return;
        }

        text = text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (_conversation is null || string.IsNullOrWhiteSpace(_sessionId))
        {
            StatusText.Text = "Session not ready.";
            return;
        }

        _busy = true;
        SendButton.IsEnabled = false;
        ProbeButton.IsEnabled = false;
        InputBox.IsEnabled = false;
        StatusText.Text = string.Empty;

        try
        {
            OrbitLocalData.Conversations.AppendMessage(_conversation.Id, "user", text);
            _messages.Add(new ChatBubbleVm { RoleLabel = "You", Text = text });

            var assistant = new ChatBubbleVm { RoleLabel = "Hermes", Text = string.Empty };
            BeginInChatThinking();

            var history = OrbitLocalData.Conversations.ListMessages(_conversation.Id)
                .Select(m => new HermesChatMessage { Role = NormalizeRole(m.Role), Content = m.Body })
                .ToList();

            history.Insert(0, new HermesChatMessage
            {
                Role = "system",
                Content = OrbitRuntimeContextProvider.Instance.Capture().ToSystemPrompt(),
            });

            using var client = CreateClient();
            var buffer = new System.Text.StringBuilder();
            var startedContent = false;
            await foreach (var delta in client.StreamChatAsync(new HermesChatRequest
            {
                Messages = history,
                SessionId = _sessionId,
                SessionKey = _sessionKey,
                Stream = true,
            }))
            {
                if (delta.Kind == HermesChatDeltaKind.Error)
                {
                    EndInChatThinking();
                    if (!_messages.Contains(assistant))
                    {
                        _messages.Add(assistant);
                    }

                    assistant.Text = delta.Text ?? "Error";
                    StatusText.Text = delta.Text ?? "Hermes error";
                    break;
                }

                if (delta.Kind == HermesChatDeltaKind.Progress)
                {
                    UpdateInChatProgress(delta);
                    continue;
                }

                if (delta.Kind == HermesChatDeltaKind.Content && !string.IsNullOrEmpty(delta.Text))
                {
                    if (!startedContent)
                    {
                        startedContent = true;
                        EndInChatThinking();
                        _messages.Add(assistant);
                    }

                    buffer.Append(delta.Text);
                    var snapshot = buffer.ToString();
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        assistant.Text = snapshot;
                        ScrollMessagesToEnd(throttle: true);
                    });
                }

                if (delta.Kind == HermesChatDeltaKind.Done)
                {
                    break;
                }
            }

            EndInChatThinking();
            if (!startedContent && buffer.Length == 0 && string.IsNullOrWhiteSpace(assistant.Text))
            {
                if (!_messages.Contains(assistant))
                {
                    _messages.Add(assistant);
                }

                assistant.Text = "(no reply text — Hermes may have only run tools)";
            }
            else if (startedContent || buffer.Length > 0)
            {
                if (!_messages.Contains(assistant))
                {
                    _messages.Add(assistant);
                }
            }

            var finalText = buffer.Length > 0 ? buffer.ToString() : assistant.Text;
            if (!string.IsNullOrWhiteSpace(finalText)
                && !finalText.StartsWith("(no reply", StringComparison.Ordinal))
            {
                OrbitLocalData.Conversations.AppendMessage(_conversation.Id, "assistant", finalText);
                assistant.Text = finalText;
                StatusText.Text = string.Empty;
            }
            else if (string.IsNullOrWhiteSpace(StatusText.Text))
            {
                StatusText.Text = "Empty response.";
            }

            ScrollMessagesToEnd();
        }
        catch (Exception ex)
        {
            EndInChatThinking();
            StatusText.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            SendButton.IsEnabled = true;
            ProbeButton.IsEnabled = true;
            InputBox.IsEnabled = true;
            EndInChatThinking();
        }
    }

    private void BeginInChatThinking()
    {
        EndInChatThinking();
        _idleTick = 0;
        _statusBubble = new ChatBubbleVm
        {
            RoleLabel = "Hermes",
            Text = HermesThinkingCopy.NextIdleLine(0),
            IsStatus = true,
        };
        _messages.Add(_statusBubble);
        ScrollMessagesToEnd();

        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.4) };
        _idleTimer.Tick += IdleTimer_Tick;
        _idleTimer.Start();
    }

    private void IdleTimer_Tick(object? sender, object e)
    {
        if (_statusBubble is null || _statusBubble.HasRealProgress)
        {
            return;
        }

        _idleTick++;
        _statusBubble.Text = HermesThinkingCopy.NextIdleLine(_idleTick);
    }

    private void UpdateInChatProgress(HermesChatDelta delta)
    {
        if (_statusBubble is null)
        {
            BeginInChatThinking();
        }

        if (_statusBubble is null)
        {
            return;
        }

        _statusBubble.HasRealProgress = true;
        _statusBubble.Text = HermesThinkingCopy.FromProgress(delta.Text, delta.ToolName, delta.Status);
        DispatcherQueue.TryEnqueue(() => ScrollMessagesToEnd(throttle: true));
    }

    private void EndInChatThinking()
    {
        StopIdleTimer();
        if (_statusBubble is not null)
        {
            _messages.Remove(_statusBubble);
            _statusBubble = null;
        }
    }

    private void StopIdleTimer()
    {
        if (_idleTimer is null)
        {
            return;
        }

        _idleTimer.Tick -= IdleTimer_Tick;
        _idleTimer.Stop();
        _idleTimer = null;
    }

    private void ScrollMessagesToEnd(bool throttle = false)
    {
        if (_messages.Count == 0)
        {
            return;
        }

        if (throttle)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastStreamScrollUtc < TimeSpan.FromMilliseconds(120))
            {
                return;
            }

            _lastStreamScrollUtc = now;
        }
        else
        {
            _lastStreamScrollUtc = DateTimeOffset.UtcNow;
        }

        // Prefer ChangeView over ScrollIntoView — the latter recycles ListView containers and flashes the chat.
        var scroll = _messageScrollViewer ??= FindDescendantScrollViewer(MessageList);
        if (scroll is not null)
        {
            scroll.UpdateLayout();
            scroll.ChangeView(null, scroll.ScrollableHeight, null, disableAnimation: true);
            return;
        }

        MessageList.UpdateLayout();
        MessageList.ScrollIntoView(_messages[^1]);
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv)
        {
            return sv;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindDescendantScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static HermesHttpClient CreateClient()
    {
        var settings = App.Settings;
        if (!HermesUrlValidation.TryValidate(settings.HermesBaseUrl, out var url, out var error))
        {
            throw new InvalidOperationException(error ?? "Invalid Hermes URL.");
        }

        var key = App.SettingsStore.ReadHermesApiKey(settings);
        return new HermesHttpClient(new Uri(url!), key);
    }

    private static string NormalizeRole(string role)
    {
        if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
        {
            return "assistant";
        }

        if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
        {
            return "system";
        }

        return "user";
    }
}

public sealed class ChatBubbleVm : System.ComponentModel.INotifyPropertyChanged
{
    private string _text = string.Empty;

    public string RoleLabel { get; init; } = string.Empty;

    public bool IsStatus { get; init; }

    public bool HasRealProgress { get; set; }

    public double TextOpacity => IsStatus ? 0.72 : 1.0;

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value)
            {
                return;
            }

            _text = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Text)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public static ChatBubbleVm FromStore(ConversationMessageRecord msg) =>
        new()
        {
            RoleLabel = string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "Hermes" : "You",
            Text = msg.Body,
        };
}
