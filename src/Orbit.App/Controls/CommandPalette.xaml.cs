using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Orbit.Core.Shell;

namespace Orbit_App.Controls;

public sealed partial class CommandPalette : UserControl
{
    public event EventHandler<string>? CommandInvoked;

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
        RefreshList(null);
        QueryBox.Focus(FocusState.Programmatic);
    }

    public void Close()
    {
        Visibility = Visibility.Collapsed;
    }

    private void RefreshList(string? query)
    {
        var items = CommandCatalog.Filter(query);
        ResultsList.ItemsSource = items;
        EmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (items.Count > 0)
        {
            ResultsList.SelectedIndex = 0;
        }
    }

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e) =>
        RefreshList(QueryBox.Text);

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
            CommandInvoked?.Invoke(this, command.Id);
            Close();
        }
    }

    private void Backdrop_PointerPressed(object sender, PointerRoutedEventArgs e) => Close();

    private void InvokeSelected()
    {
        if (ResultsList.SelectedItem is ShellCommand command)
        {
            CommandInvoked?.Invoke(this, command.Id);
            Close();
        }
    }
}
