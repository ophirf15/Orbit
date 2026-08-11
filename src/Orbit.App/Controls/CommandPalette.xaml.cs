using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Orbit.Core.Shell;
using Orbit_App.Services;
using Orbit_App.Views;

namespace Orbit_App.Controls;

public sealed partial class CommandPalette : UserControl
{
    public event EventHandler<ShellCommand>? CommandInvoked;

    private CancellationTokenSource? _searchCts;
    private IReadOnlyList<ShellCommand> _searchHits = [];
    private string _lastQuery = string.Empty;

    public CommandPalette()
    {
        InitializeComponent();
        Visibility = Visibility.Collapsed;
        IsTabStop = false;
        KeyDown += CommandPalette_KeyDown;
        var escape = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, args) =>
        {
            Close();
            args.Handled = true;
        };
        KeyboardAccelerators.Add(escape);
    }

    private void CommandPalette_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    public void Open()
    {
        Visibility = Visibility.Visible;
        QueryBox.Text = string.Empty;
        _searchHits = [];
        _lastQuery = string.Empty;
        RefreshList(null);
        QueryBox.Focus(FocusState.Programmatic);
    }

    public void Close()
    {
        _searchCts?.Cancel();
        Visibility = Visibility.Collapsed;
    }

    private void RefreshList(string? query)
    {
        var q = query?.Trim() ?? string.Empty;
        _lastQuery = q;
        var items = new List<ShellCommand>();

        if (q.Length > 0)
        {
            items.Add(new ShellCommand(
                CommandCatalog.SearchRun,
                $"Search Orbit for \"{q}\"",
                "search find look up",
                q));
            items.AddRange(_searchHits);
        }

        items.AddRange(CommandCatalog.Filter(string.IsNullOrWhiteSpace(q) ? null : q));
        ResultsList.ItemsSource = items;
        EmptyState.Text = q.Length > 0
            ? "No matching commands or results"
            : "No matching commands";
        EmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (items.Count > 0)
        {
            ResultsList.SelectedIndex = 0;
        }
    }

    private async void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = QueryBox.Text;
        RefreshList(query);
        await SearchHitsAsync(query);
    }

    private async Task SearchHitsAsync(string? query)
    {
        _searchCts?.Cancel();
        var q = query?.Trim() ?? string.Empty;
        if (q.Length < 2)
        {
            _searchHits = [];
            if (string.Equals(_lastQuery, q, StringComparison.Ordinal))
            {
                RefreshList(q);
            }

            return;
        }

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        try
        {
            await Task.Delay(220, cts.Token);
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var hits = await client.GlobalSearchAsync(q, ct: cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            _searchHits = hits
                .Take(8)
                .Select(h => new ShellCommand(
                    CommandCatalog.SearchHit,
                    FormatHitTitle(h),
                    $"{h.EntityType} {h.Title} {h.Snippet}",
                    EncodeHit(h)))
                .ToList();

            if (string.Equals(QueryBox.Text?.Trim(), q, StringComparison.Ordinal))
            {
                RefreshList(q);
            }
        }
        catch (OperationCanceledException)
        {
            // superseded
        }
        catch (Exception)
        {
            _searchHits = [];
        }
    }

    private static string FormatHitTitle(SearchHitItem hit)
    {
        var type = string.IsNullOrWhiteSpace(hit.EntityType) ? "result" : hit.EntityType.Trim();
        var label = type.Length == 0
            ? "Result"
            : char.ToUpperInvariant(type[0]) + type[1..];
        return $"{label} · {hit.Title}";
    }

    private static string EncodeHit(SearchHitItem hit) =>
        string.Join('|', hit.EntityType, hit.EntityId, hit.ProjectId ?? string.Empty);

    private void QueryBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            InvokeSelected();
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Down && ResultsList.Items.Count > 0)
        {
            ResultsList.Focus(FocusState.Programmatic);
            ResultsList.SelectedIndex = Math.Min(ResultsList.SelectedIndex + 1, ResultsList.Items.Count - 1);
            e.Handled = true;
        }
    }

    private void ResultsList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            InvokeSelected();
            e.Handled = true;
        }
    }

    private void ResultsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) =>
        InvokeSelected();

    private void ResultsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ShellCommand command)
        {
            RaiseInvoked(command);
        }
    }

    private void Backdrop_PointerPressed(object sender, PointerRoutedEventArgs e) => Close();

    private void InvokeSelected()
    {
        if (ResultsList.SelectedItem is ShellCommand command)
        {
            RaiseInvoked(command);
            return;
        }

        var q = QueryBox.Text?.Trim() ?? string.Empty;
        if (q.Length > 0)
        {
            RaiseInvoked(new ShellCommand(CommandCatalog.SearchRun, q, "search", q));
        }
    }

    private void RaiseInvoked(ShellCommand command)
    {
        CommandInvoked?.Invoke(this, command);
        Close();
    }
}
