using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    private readonly ObservableCollection<RemoteConversationVm> _remoteSessions = [];
    private readonly ObservableCollection<RemoteChangeVm> _remoteChanges = [];
    private readonly ObservableCollection<PendingSuggestionVm> _operatorSuggestions = [];
    private readonly ObservableCollection<OperatorRuleVm> _operatorRules = [];
    private readonly ObservableCollection<OperatorMemoryVm> _operatorMemory = [];
    private ConversationRecord? _conversation;
    private string? _sessionId;
    private string? _sessionKey;
    private bool _busy;

    public AgentChatPage()
    {
        InitializeComponent();
        MessageList.ItemsSource = _messages;
        RemoteSessionList.ItemsSource = _remoteSessions;
        RemoteChangeList.ItemsSource = _remoteChanges;
        OperatorSuggestionList.ItemsSource = _operatorSuggestions;
        OperatorRuleList.ItemsSource = _operatorRules;
        OperatorMemoryList.ItemsSource = _operatorMemory;
        Loaded += OnLoaded;
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

        await RefreshRemoteActivityAsync();
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
            _operatorSuggestions.Clear();
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
                $"{dash.LatestRunStatus ?? "idle"} · {dash.LatestTrigger ?? "—"} · {dash.Rules.Count} rule(s) · merges only below";

            foreach (var s in dash.PendingSuggestions)
            {
                _operatorSuggestions.Add(s);
            }

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

    private async void SuggestionAccept_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id })
        {
            return;
        }

        using var core = new CoreHostClient(App.Settings, App.SettingsStore);
        var suggestion = _operatorSuggestions.FirstOrDefault(s => s.Id == id);
        var projectId = suggestion?.ProjectId;
        if (string.Equals(suggestion?.SuggestionType, "disambiguate_email_claim", StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(projectId))
        {
            StatusText.Text = "Pick a project on the workbench first, or set ApplyProjectId via Always with a rule.";
            return;
        }

        await core.AcceptSuggestionAsync(id, projectId);
        await RefreshOperatorAsync();
    }

    private async void SuggestionAlways_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id })
        {
            return;
        }

        using var core = new CoreHostClient(App.Settings, App.SettingsStore);
        var suggestion = _operatorSuggestions.FirstOrDefault(s => s.Id == id);
        await core.AcceptSuggestionAlwaysAsync(id, suggestion?.ProjectId);
        await RefreshOperatorAsync();
    }

    private async void SuggestionReject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id })
        {
            return;
        }

        using var core = new CoreHostClient(App.Settings, App.SettingsStore);
        await core.RejectSuggestionAsync(id);
        await RefreshOperatorAsync();
    }

    private async Task RefreshRemoteActivityAsync()
    {
        try
        {
            using var core = new CoreHostClient(App.Settings, App.SettingsStore);
            var activity = await core.GetRemoteActivityAsync();
            _remoteSessions.Clear();
            _remoteChanges.Clear();
            if (activity is null)
            {
                RemoteStatusText.Text = "Core unreachable — remote activity unavailable.";
                return;
            }

            foreach (var session in activity.Conversations)
            {
                _remoteSessions.Add(session);
            }

            foreach (var change in activity.Changes)
            {
                _remoteChanges.Add(change);
            }

            RemoteStatusText.Text = _remoteSessions.Count == 0 && _remoteChanges.Count == 0
                ? "No Telegram sessions or remote mutations yet."
                : $"{_remoteSessions.Count} session(s), {_remoteChanges.Count} change(s).";
        }
        catch (Exception ex)
        {
            RemoteStatusText.Text = $"Remote activity error: {ex.Message}";
        }
    }

    private void RemoteSessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RemoteSessionList.SelectedItem is not RemoteConversationVm selected)
        {
            return;
        }

        OpenMappedConversation(selected.Id);
    }

    private void OpenMappedConversation(string conversationId)
    {
        try
        {
            OrbitLocalData.EnsureOpened();
            var record = OrbitLocalData.Conversations.Get(conversationId);
            if (record is null)
            {
                StatusText.Text = "Conversation not found locally.";
                return;
            }

            _conversation = record;
            _sessionId = record.HermesSessionId;
            _sessionKey = record.HermesSessionKey;
            _messages.Clear();
            foreach (var msg in OrbitLocalData.Conversations.ListMessages(record.Id))
            {
                _messages.Add(ChatBubbleVm.FromStore(msg));
            }

            SessionStatusText.Text =
                $"Opened {record.Channel} conversation {record.Id}" +
                (string.IsNullOrWhiteSpace(record.HermesSessionId)
                    ? string.Empty
                    : $" · Hermes: {record.HermesSessionId}");
            StatusText.Text = "Remote conversation loaded.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
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
        StatusText.Text = "Streaming…";

        try
        {
            OrbitLocalData.Conversations.AppendMessage(_conversation.Id, "user", text);
            _messages.Add(new ChatBubbleVm { RoleLabel = "You", Text = text });

            var assistant = new ChatBubbleVm { RoleLabel = "Hermes", Text = string.Empty };
            _messages.Add(assistant);

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
                    assistant.Text = delta.Text ?? "Error";
                    StatusText.Text = delta.Text ?? "Hermes error";
                    break;
                }

                if (delta.Kind == HermesChatDeltaKind.Content && !string.IsNullOrEmpty(delta.Text))
                {
                    buffer.Append(delta.Text);
                    var snapshot = buffer.ToString();
                    DispatcherQueue.TryEnqueue(() => assistant.Text = snapshot);
                }

                if (delta.Kind == HermesChatDeltaKind.Done)
                {
                    break;
                }
            }

            var finalText = buffer.Length > 0 ? buffer.ToString() : assistant.Text;
            if (!string.IsNullOrWhiteSpace(finalText))
            {
                OrbitLocalData.Conversations.AppendMessage(_conversation.Id, "assistant", finalText);
                assistant.Text = finalText;
                StatusText.Text = "Done.";
            }
            else if (string.IsNullOrWhiteSpace(StatusText.Text) || StatusText.Text == "Streaming…")
            {
                StatusText.Text = "Empty response.";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            SendButton.IsEnabled = true;
            ProbeButton.IsEnabled = true;
        }
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
