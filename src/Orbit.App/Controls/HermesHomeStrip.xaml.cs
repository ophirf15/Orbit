using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Orbit_App.ViewModels;

namespace Orbit_App.Controls;

public sealed partial class HermesHomeStrip : UserControl
{
    public event EventHandler? RefreshRequested;

    public event EventHandler<string>? ConcernClicked;

    public HermesHomeStrip()
    {
        InitializeComponent();
    }

    public void SetBusy(bool busy) => RefreshButton.IsEnabled = !busy;

    public void Bind(PulseVm? pulse)
    {
        if (pulse is null)
        {
            DayBriefText.Text = "Could not load pulse from Core Host.";
            HermesHintText.Visibility = Visibility.Collapsed;
            ActivityText.Text = string.Empty;
            ConcernsHeader.Text = "Needs you now";
            ConcernsRepeater.ItemsSource = Array.Empty<PulseConcernVm>();
            return;
        }

        DayBriefText.Text = string.IsNullOrWhiteSpace(pulse.DayBrief)
            ? "Nothing open in the orbit yet. Hermes will fill this brief as it works."
            : pulse.DayBrief;

        if (!string.IsNullOrWhiteSpace(pulse.HermesHint))
        {
            HermesHintText.Text = pulse.HermesHint;
            HermesHintText.Visibility = Visibility.Visible;
        }
        else
        {
            HermesHintText.Visibility = Visibility.Collapsed;
        }

        ActivityText.Text = FormatActivity(pulse);
        var concerns = pulse.Concerns?.ToList() ?? [];
        ConcernsHeader.Text = concerns.Count == 0
            ? "Needs you now"
            : $"Needs you now · {concerns.Count}";
        ConcernsRepeater.ItemsSource = concerns;
    }

    private static string FormatActivity(PulseVm pulse)
    {
        var run = pulse.LastOperatorRun;
        if (run is null)
        {
            return string.IsNullOrWhiteSpace(pulse.GeneratedAt)
                ? "Hermes feeds this home — open a concern or wait for the next duty scan."
                : $"Pulse updated {pulse.GeneratedAtDisplay}";
        }

        var when = string.IsNullOrWhiteSpace(run.WhenDisplay) ? run.CreatedAt : run.WhenDisplay;
        if (string.Equals(run.Status, "running", StringComparison.OrdinalIgnoreCase))
        {
            return $"Hermes working · {FriendlyTrigger(run.TriggerKind)}…";
        }

        if (string.Equals(run.Status, "failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(run.Status, "skipped", StringComparison.OrdinalIgnoreCase))
        {
            var detail = string.IsNullOrWhiteSpace(run.ErrorText) ? run.Status : Truncate(run.ErrorText!, 120);
            return $"Hermes · {FriendlyTrigger(run.TriggerKind)} · {detail} · {when}";
        }

        var summary = string.IsNullOrWhiteSpace(run.BriefingSummary)
            ? run.Status
            : Truncate(run.BriefingSummary!, 140);
        return $"Hermes · {FriendlyTrigger(run.TriggerKind)} · {when} — {summary}";
    }

    private static string FriendlyTrigger(string? trigger) => trigger switch
    {
        "duty.scan" => "duty scan",
        "email.ingested" => "email",
        "calendar.soon" => "calendar",
        "note.created" => "capture",
        "task.updated" => "task update",
        _ => string.IsNullOrWhiteSpace(trigger) ? "run" : trigger,
    };

    private static string Truncate(string text, int max)
    {
        var flat = text.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void ConcernChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string taskId } && !string.IsNullOrWhiteSpace(taskId))
        {
            ConcernClicked?.Invoke(this, taskId);
        }
    }
}
