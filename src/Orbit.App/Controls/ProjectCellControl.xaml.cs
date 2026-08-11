using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Orbit_App.ViewModels;
using Windows.System;
using Windows.UI;

namespace Orbit_App.Controls;

public sealed class AgentClarifyReplyEventArgs : EventArgs
{
    public required string TaskId { get; init; }

    public required string Reply { get; init; }
}

public sealed class AgentClarifyDoneEventArgs : EventArgs
{
    public required string TaskId { get; init; }
}

public sealed class ProjectAccentRequestedEventArgs : EventArgs
{
    public required ProjectCellVm Cell { get; init; }

    /// <summary>#RRGGBB or null to clear.</summary>
    public string? AccentColor { get; init; }
}

public sealed class CellLayoutChangedEventArgs : EventArgs
{
    public required ProjectCellVm Cell { get; init; }

    public double X { get; init; }

    public double Y { get; init; }

    public double Width { get; init; }

    public double Height { get; init; }

    public bool IsComplete { get; init; }

    public bool IsResize { get; init; }
}

public sealed partial class ProjectCellControl : UserControl
{
    public event EventHandler<ProjectCellVm>? OpenRequested;

    public event EventHandler<ProjectCellVm>? EnterBoardRequested;

    public event EventHandler<(ProjectCellVm Cell, string TaskId)>? LineOpenRequested;

    public event EventHandler<(ProjectCellVm Cell, string Text)>? CaptureSubmitted;

    public event EventHandler<AgentClarifyReplyEventArgs>? AgentClarifyReply;

    public event EventHandler<AgentClarifyDoneEventArgs>? AgentClarifyDone;

    public event EventHandler<ProjectCellVm>? ArchiveProjectRequested;

    public event EventHandler<ProjectCellVm>? MergeProjectRequested;

    public event EventHandler<ProjectAccentRequestedEventArgs>? AccentColorRequested;

    public event EventHandler<ProjectCellVm>? SetHomeFolderRequested;

    public event EventHandler<ProjectCellVm>? OpenHomeFolderRequested;

    public event EventHandler<CellLayoutChangedEventArgs>? LayoutChanged;

    public event EventHandler<(ProjectCellVm Cell, string Name)>? TitleCommitted;

    private static readonly (string Label, string? Hex)[] AccentPresets =
    [
        ("Default", null),
        ("Blue", "#0F6CBD"),
        ("Sky", "#0284C7"),
        ("Teal", "#0D9488"),
        ("Green", "#16A34A"),
        ("Amber", "#D97706"),
        ("Rose", "#E11D48"),
        ("Violet", "#7C3AED"),
        ("Slate", "#64748B"),
    ];

    private ProjectCellVm? _cell;
    private readonly ObservableCollection<CellLineVm> _lines = [];
    private string? _clarifyTaskId;
    private bool _clarifyBusy;
    private bool _moving;
    private bool _resizing;
    private Windows.Foundation.Point _pointerStart;
    private double _originX;
    private double _originY;
    private double _originW;
    private double _originH;

    public ProjectCellControl()
    {
        InitializeComponent();
        LinesList.ItemsSource = _lines;
        DataContextChanged += ProjectCellControl_DataContextChanged;

        // TextBox often marks Escape as handled before the XAML KeyDown handler runs.
        AddHandler(KeyDownEvent, new KeyEventHandler(ProjectCell_EscapeKeyDown), handledEventsToo: true);

        var cancelCapture = new KeyboardAccelerator { Key = VirtualKey.Escape };
        cancelCapture.Invoked += (_, args) =>
        {
            if (CancelCaptureDraft())
            {
                args.Handled = true;
            }
        };
        CaptureBox.KeyboardAccelerators.Add(cancelCapture);
    }

    private void ProjectCell_EscapeKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || e.Handled)
        {
            return;
        }

        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox focused)
        {
            if (ReferenceEquals(focused, CaptureBox) && CancelCaptureDraft())
            {
                e.Handled = true;
                return;
            }

            if (ReferenceEquals(focused, NameBox) && _cell is not null)
            {
                NameBox.Text = _cell.Name;
                e.Handled = true;
                return;
            }

            if (ReferenceEquals(focused, AgentReplyBox))
            {
                if (!string.IsNullOrEmpty(AgentReplyBox.Text))
                {
                    AgentReplyBox.Text = string.Empty;
                }
                else
                {
                    CloseAgentClarify();
                }

                e.Handled = true;
            }
        }
    }

    /// <summary>Clears an in-progress capture without submitting. Returns true when a draft was cancelled.</summary>
    public bool CancelCaptureDraft()
    {
        if (CaptureBox.Visibility != Visibility.Visible || string.IsNullOrEmpty(CaptureBox.Text))
        {
            return false;
        }

        CaptureBox.Text = string.Empty;
        CaptureStatus.Visibility = Visibility.Collapsed;
        CaptureBox.Focus(FocusState.Programmatic);
        return true;
    }

    private void ProjectCellControl_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (args.NewValue is ProjectCellVm vm)
        {
            Bind(vm);
        }
    }

    public void Bind(ProjectCellVm cell)
    {
        _cell = cell;
        NameBox.Text = cell.Name;
        var bits = new List<string>();
        if (cell.HasBlocker)
        {
            bits.Add(cell.BlockerBadgeText);
            if (!string.IsNullOrWhiteSpace(cell.TopBlockerSummary))
            {
                bits.Add(cell.TopBlockerSummary!);
            }
        }

        if (cell.HasMeeting)
        {
            bits.Add(cell.MeetingText);
        }

        if (cell.HasSuggestion)
        {
            bits.Add(cell.SuggestionText);
        }

        if (!string.IsNullOrWhiteSpace(cell.HygieneText))
        {
            bits.Add(cell.HygieneText);
        }

        if (!string.IsNullOrWhiteSpace(cell.RecentText))
        {
            bits.Add(cell.RecentText);
        }

        MetaText.Text = string.Join(" · ", bits);
        MetaText.Visibility = bits.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        EnterBoardButton.Visibility = cell.IsTaskCell || cell.IsLimboCell
            ? Visibility.Collapsed
            : Visibility.Visible;
        NameBox.IsReadOnly = cell.IsLimboCell;
        NameBox.IsHitTestVisible = !cell.IsLimboCell;
        CaptureBox.PlaceholderText = cell.IsLimboCell
            ? "Ask agent or type a command…"
            : "Capture a line…";
        CaptureBox.Visibility = cell.IsLimboCell ? Visibility.Collapsed : Visibility.Visible;
        CaptureBox.SetValue(
            AutomationProperties.NameProperty,
            cell.IsLimboCell ? "Limbo agent command" : "Project capture");
        if (cell.IsLimboCell)
        {
            AccentStripe.Background = (Brush)Application.Current.Resources["OrbitLimboAccentBrush"];
            if (string.IsNullOrWhiteSpace(MetaText.Text))
            {
                MetaText.Text = "Unassigned · click a line to Assign or Archive";
                MetaText.Visibility = Visibility.Visible;
            }
        }
        else
        {
            ApplyAccentStripe(cell.AccentColor);
        }

        SyncLines(cell.Lines);
        CaptureBox.Text = string.Empty;
        CaptureStatus.Visibility = Visibility.Collapsed;
    }

    public void ApplyAccentStripe(string? accentColor)
    {
        if (_cell is not null && _cell.IsLimboCell)
        {
            AccentStripe.Background = (Brush)Application.Current.Resources["OrbitLimboAccentBrush"];
            return;
        }

        if (_cell is not null)
        {
            _cell.AccentColor = accentColor;
        }

        if (string.IsNullOrWhiteSpace(accentColor)
            || !TryParseHexColor(accentColor, out var color))
        {
            AccentStripe.Background = (Brush)Application.Current.Resources["OrbitCellAccentBrush"];
            return;
        }

        AccentStripe.Background = new SolidColorBrush(color);
    }

    private static bool TryParseHexColor(string hex, out Color color)
    {
        color = default;
        var value = hex.Trim();
        if (value.Length != 7 || value[0] != '#')
        {
            return false;
        }

        static int Nibble(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => 10 + (c - 'a'),
            >= 'A' and <= 'F' => 10 + (c - 'A'),
            _ => -1,
        };

        var rHi = Nibble(value[1]);
        var rLo = Nibble(value[2]);
        var gHi = Nibble(value[3]);
        var gLo = Nibble(value[4]);
        var bHi = Nibble(value[5]);
        var bLo = Nibble(value[6]);
        if (rHi < 0 || rLo < 0 || gHi < 0 || gLo < 0 || bHi < 0 || bLo < 0)
        {
            return false;
        }

        color = Color.FromArgb(255, (byte)((rHi << 4) | rLo), (byte)((gHi << 4) | gLo), (byte)((bHi << 4) | bLo));
        return true;
    }

    public void PrependLine(CellLineVm line)
    {
        if (_cell is null)
        {
            return;
        }

        // Keep VM and UI in sync without rebuilding the whole workbench.
        var list = _cell.Lines as IList<CellLineVm> ?? _cell.Lines.ToList();
        if (list is not List<CellLineVm> mutable)
        {
            mutable = list.ToList();
            _cell.Lines = mutable;
        }

        mutable.RemoveAll(l => l.TaskId == line.TaskId);
        mutable.Insert(0, line);
        while (mutable.Count > 8)
        {
            mutable.RemoveAt(mutable.Count - 1);
        }

        SyncLines(mutable);
        CaptureStatus.Text = "Saved";
        CaptureStatus.Visibility = Visibility.Visible;
    }

    public void UpdateLine(string taskId, string title, string? nextAction = null)
    {
        if (_cell is null || string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        void Apply(CellLineVm line)
        {
            if (line.TaskId != taskId)
            {
                return;
            }

            line.Title = title.Trim();
            if (nextAction is not null)
            {
                line.NextAction = string.IsNullOrWhiteSpace(nextAction) ? null : nextAction.Trim();
            }
        }

        foreach (var line in _lines)
        {
            Apply(line);
        }

        if (_cell.Lines is IList<CellLineVm> list)
        {
            foreach (var line in list)
            {
                Apply(line);
            }
        }

        SyncLines(_cell.Lines);
        CaptureStatus.Text = "Updated";
        CaptureStatus.Visibility = Visibility.Visible;
    }

    public void UpdateLineTitle(string taskId, string title) => UpdateLine(taskId, title);

    private void SyncLines(IEnumerable<CellLineVm> lines)
    {
        _lines.Clear();
        foreach (var line in lines.Take(8))
        {
            _lines.Add(line);
        }
    }

    public void FocusCapture()
    {
        CaptureBox.Focus(FocusState.Programmatic);
    }

    public void BeginAgentClarify(string taskId, string message)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return;
        }

        _clarifyTaskId = taskId;
        _clarifyBusy = false;
        AgentNudgeTip.Target = CaptureBox;
        AgentNudgeBody.Text = message.Trim();
        AgentReplyBox.Text = string.Empty;
        AgentReplyBox.IsEnabled = true;
        AgentSendButton.IsEnabled = true;
        AgentDoneButton.IsEnabled = true;
        AgentNudgeTip.IsOpen = true;
        DispatcherQueue.TryEnqueue(() => AgentReplyBox.Focus(FocusState.Programmatic));
    }

    public void SetAgentClarifyMessage(string message, bool busy = false)
    {
        AgentNudgeBody.Text = message.Trim();
        _clarifyBusy = busy;
        AgentReplyBox.IsEnabled = !busy;
        AgentSendButton.IsEnabled = !busy;
        AgentDoneButton.IsEnabled = !busy;
        if (!busy)
        {
            DispatcherQueue.TryEnqueue(() => AgentReplyBox.Focus(FocusState.Programmatic));
        }
    }

    public void CloseAgentClarify()
    {
        AgentNudgeTip.IsOpen = false;
        _clarifyTaskId = null;
        _clarifyBusy = false;
        AgentReplyBox.Text = string.Empty;
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cell is not null)
        {
            OpenRequested?.Invoke(this, _cell);
        }
    }

    private void OpenButton_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_cell is null || _cell.IsTaskCell || _cell.IsLimboCell)
        {
            return;
        }

        e.Handled = true;
        EnterBoardRequested?.Invoke(this, _cell);
    }

    private void EnterBoardButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cell is not null && !_cell.IsTaskCell && !_cell.IsLimboCell)
        {
            EnterBoardRequested?.Invoke(this, _cell);
        }
    }

    private async void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cell is null)
        {
            return;
        }

        var menu = new MenuFlyout();
        if (_cell.IsLimboCell)
        {
            var hint = new MenuFlyoutItem
            {
                Text = "Unassigned captures · Assign or Archive on a line",
                IsEnabled = false,
            };
            menu.Items.Add(hint);
        }
        else if (_cell.IsTaskCell)
        {
            var openTask = new MenuFlyoutItem { Text = "Open task detail" };
            openTask.Click += (_, _) => OpenRequested?.Invoke(this, _cell);
            var archiveTask = new MenuFlyoutItem { Text = "Archive task…" };
            archiveTask.Click += (_, _) => ArchiveProjectRequested?.Invoke(this, _cell);
            menu.Items.Add(openTask);
            menu.Items.Add(archiveTask);
        }
        else
        {
            var enter = new MenuFlyoutItem { Text = "Enter board" };
            enter.Click += (_, _) => EnterBoardRequested?.Invoke(this, _cell);
            var open = new MenuFlyoutItem { Text = "Open project detail" };
            open.Click += (_, _) => OpenRequested?.Invoke(this, _cell);
            var colors = new MenuFlyoutSubItem { Text = "Stripe color" };
            foreach (var (label, hex) in AccentPresets)
            {
                var item = new MenuFlyoutItem { Text = label, Tag = hex };
                if (hex is not null && TryParseHexColor(hex, out var swatch))
                {
                    item.Icon = new FontIcon
                    {
                        Glyph = "\uE91F",
                        Foreground = new SolidColorBrush(swatch),
                    };
                }

                item.Click += (_, _) =>
                {
                    if (_cell is null)
                    {
                        return;
                    }

                    AccentColorRequested?.Invoke(this, new ProjectAccentRequestedEventArgs
                    {
                        Cell = _cell,
                        AccentColor = item.Tag as string,
                    });
                };
                colors.Items.Add(item);
            }

            var archive = new MenuFlyoutItem { Text = "Archive project…" };
            archive.Click += (_, _) => ArchiveProjectRequested?.Invoke(this, _cell);
            var merge = new MenuFlyoutItem { Text = "Merge project into…" };
            merge.Click += (_, _) => MergeProjectRequested?.Invoke(this, _cell);
            var setHome = new MenuFlyoutItem { Text = "Set home folder…" };
            setHome.Click += (_, _) => SetHomeFolderRequested?.Invoke(this, _cell);
            var openHome = new MenuFlyoutItem { Text = "Open home folder" };
            openHome.Click += (_, _) => OpenHomeFolderRequested?.Invoke(this, _cell);
            menu.Items.Add(enter);
            menu.Items.Add(open);
            menu.Items.Add(setHome);
            menu.Items.Add(openHome);
            menu.Items.Add(colors);
            menu.Items.Add(merge);
            menu.Items.Add(archive);
        }

        menu.ShowAt(MoreButton);
        await Task.CompletedTask;
    }

    private void LineButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cell is null)
        {
            return;
        }

        if (sender is Button button && button.Tag is string taskId)
        {
            LineOpenRequested?.Invoke(this, (_cell, taskId));
        }
    }

    private void CaptureBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            CancelCaptureDraft();
            e.Handled = true;
            return;
        }

        if (e.Key != VirtualKey.Enter || _cell is null)
        {
            return;
        }

        var text = CaptureBox.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return;
        }

        CaptureSubmitted?.Invoke(this, (_cell, text));
        CaptureBox.Text = string.Empty;
        if (!_cell.IsLimboCell)
        {
            CaptureStatus.Text = "Saving…";
            CaptureStatus.Visibility = Visibility.Visible;
        }

        e.Handled = true;
    }

    private void AgentReplyBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            if (!string.IsNullOrEmpty(AgentReplyBox.Text))
            {
                AgentReplyBox.Text = string.Empty;
            }
            else
            {
                CloseAgentClarify();
            }

            e.Handled = true;
            return;
        }

        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        SubmitAgentReply();
    }

    private void AgentSendButton_Click(object sender, RoutedEventArgs e) => SubmitAgentReply();

    private void AgentDoneButton_Click(object sender, RoutedEventArgs e)
    {
        if (_clarifyBusy || string.IsNullOrWhiteSpace(_clarifyTaskId))
        {
            return;
        }

        AgentClarifyDone?.Invoke(this, new AgentClarifyDoneEventArgs { TaskId = _clarifyTaskId });
    }

    private void SubmitAgentReply()
    {
        if (_clarifyBusy || string.IsNullOrWhiteSpace(_clarifyTaskId))
        {
            return;
        }

        var reply = AgentReplyBox.Text?.Trim() ?? string.Empty;
        if (reply.Length == 0)
        {
            return;
        }

        AgentReplyBox.Text = string.Empty;
        AgentClarifyReply?.Invoke(this, new AgentClarifyReplyEventArgs
        {
            TaskId = _clarifyTaskId,
            Reply = reply,
        });
    }

    private void NameBox_LostFocus(object sender, RoutedEventArgs e) => CommitTitle();

    private void NameBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            if (_cell is not null)
            {
                NameBox.Text = _cell.Name;
            }

            OpenButton.Focus(FocusState.Programmatic);
            e.Handled = true;
            return;
        }

        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        CommitTitle();
        // Leave the name box so Enter clearly commits (LostFocus would also save).
        OpenButton.Focus(FocusState.Programmatic);
    }

    /// <summary>Closes clarify tip or cancels capture draft if open. Returns true when Escape was consumed.</summary>
    public bool TryDismissTransientUi()
    {
        if (CancelCaptureDraft())
        {
            return true;
        }

        if (AgentNudgeTip.IsOpen)
        {
            CloseAgentClarify();
            return true;
        }

        return false;
    }

    private void CommitTitle()
    {
        if (_cell is null)
        {
            return;
        }

        var name = NameBox.Text?.Trim() ?? string.Empty;
        if (name.Length == 0 || string.Equals(name, _cell.Name, StringComparison.Ordinal))
        {
            NameBox.Text = _cell.Name;
            return;
        }

        TitleCommitted?.Invoke(this, (_cell, name));
    }

    private void MoveHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_cell is null)
        {
            return;
        }

        _moving = true;
        _pointerStart = e.GetCurrentPoint(null).Position;
        _originX = _cell.BoardX;
        _originY = _cell.BoardY;
        MoveHandle.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void MoveHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_moving || _cell is null)
        {
            return;
        }

        var pos = e.GetCurrentPoint(null).Position;
        RaiseLayout(
            _originX + (pos.X - _pointerStart.X),
            _originY + (pos.Y - _pointerStart.Y),
            _cell.BoardW,
            _cell.BoardH,
            isComplete: false,
            isResize: false);
        e.Handled = true;
    }

    private void MoveHandle_PointerReleased(object sender, PointerRoutedEventArgs e) => EndMove(e);

    private void MoveHandle_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndMove(e);

    private void EndMove(PointerRoutedEventArgs e)
    {
        if (!_moving || _cell is null)
        {
            return;
        }

        _moving = false;
        try
        {
            MoveHandle.ReleasePointerCapture(e.Pointer);
        }
        catch (Exception)
        {
            // capture may already be released
        }

        RaiseLayout(_cell.BoardX, _cell.BoardY, _cell.BoardW, _cell.BoardH, isComplete: true, isResize: false);
        e.Handled = true;
    }

    private void ResizeGrip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_cell is null)
        {
            return;
        }

        _resizing = true;
        _pointerStart = e.GetCurrentPoint(null).Position;
        _originW = _cell.BoardW;
        _originH = _cell.BoardH;
        ResizeGrip.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ResizeGrip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizing || _cell is null)
        {
            return;
        }

        var pos = e.GetCurrentPoint(null).Position;
        RaiseLayout(
            _cell.BoardX,
            _cell.BoardY,
            _originW + (pos.X - _pointerStart.X),
            _originH + (pos.Y - _pointerStart.Y),
            isComplete: false,
            isResize: true);
        e.Handled = true;
    }

    private void ResizeGrip_PointerReleased(object sender, PointerRoutedEventArgs e) => EndResize(e);

    private void ResizeGrip_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndResize(e);

    private void EndResize(PointerRoutedEventArgs e)
    {
        if (!_resizing || _cell is null)
        {
            return;
        }

        _resizing = false;
        try
        {
            ResizeGrip.ReleasePointerCapture(e.Pointer);
        }
        catch (Exception)
        {
            // capture may already be released
        }

        RaiseLayout(_cell.BoardX, _cell.BoardY, _cell.BoardW, _cell.BoardH, isComplete: true, isResize: true);
        e.Handled = true;
    }

    private void RaiseLayout(double x, double y, double w, double h, bool isComplete, bool isResize)
    {
        if (_cell is null)
        {
            return;
        }

        LayoutChanged?.Invoke(this, new CellLayoutChangedEventArgs
        {
            Cell = _cell,
            X = x,
            Y = y,
            Width = w,
            Height = h,
            IsComplete = isComplete,
            IsResize = isResize,
        });
    }
}
