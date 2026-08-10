using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Orbit.Core.Preview;
using Orbit_App.Services;
using Windows.Foundation;
using WinRT.Interop;

namespace Orbit_App.Controls;

public sealed partial class FilePreviewControl : UserControl
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".jfif",
    };

    private static readonly HashSet<string> WebViewFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".html", ".htm", ".svg",
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".csv", ".log", ".json", ".xml", ".yml", ".yaml", ".cs", ".ts", ".js", ".css",
    };

    private readonly ShellPreviewHandlerSession _shellPreview = new();
    private CancellationTokenSource? _loadCts;
    private string? _currentPath;
    private bool _webReady;

    public FilePreviewControl()
    {
        InitializeComponent();
    }

    public async Task ShowAsync(string? filePath, string? fallbackText = null)
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        ClearVisuals(keepPlaceholder: false);
        BusyText.Visibility = Visibility.Visible;
        BusyText.Text = "Loading preview…";

        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                ShowText(fallbackText ?? "File not found for preview.");
                return;
            }

            _currentPath = filePath;
            var ext = Path.GetExtension(filePath);

            if (ImageExtensions.Contains(ext))
            {
                await ShowImageAsync(filePath, ct);
                return;
            }

            if (string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".markdown", StringComparison.OrdinalIgnoreCase))
            {
                await ShowMarkdownAsync(filePath, ct);
                return;
            }

            if (WebViewFileExtensions.Contains(ext))
            {
                await ShowWebFileAsync(filePath, ct);
                return;
            }

            if (TryShowShellPreview(filePath))
            {
                BusyText.Visibility = Visibility.Collapsed;
                return;
            }

            if (TextExtensions.Contains(ext) || LooksLikeText(filePath))
            {
                var text = await File.ReadAllTextAsync(filePath, ct);
                if (text.Length > 200_000)
                {
                    text = text[..200_000] + "\n\n… truncated …";
                }

                ShowText(text);
                return;
            }

            // Last resort: try shell again already failed; show fallback / open hint.
            if (!string.IsNullOrWhiteSpace(fallbackText))
            {
                ShowText(fallbackText);
            }
            else
            {
                ShowText("No in-app preview handler for this type. Use Open externally.");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowText($"Preview failed: {ex.Message}");
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                BusyText.Visibility = Visibility.Collapsed;
            }
        }
    }

    public void Clear()
    {
        _loadCts?.Cancel();
        _currentPath = null;
        ClearVisuals(keepPlaceholder: true);
    }

    private async Task ShowImageAsync(string path, CancellationToken ct)
    {
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(await OpenRandomAccessStreamAsync(path));
        ct.ThrowIfCancellationRequested();
        ImagePreview.Source = bitmap;
        ImagePreview.Visibility = Visibility.Visible;
        TextHost.Visibility = Visibility.Collapsed;
        WebPreview.Visibility = Visibility.Collapsed;
        ShellHostSite.Visibility = Visibility.Collapsed;
        _shellPreview.Unload();
    }

    private async Task ShowMarkdownAsync(string path, CancellationToken ct)
    {
        var markdown = await File.ReadAllTextAsync(path, ct);
        var dark = ActualTheme == ElementTheme.Dark
            || (ActualTheme == ElementTheme.Default
                && Application.Current.RequestedTheme == ApplicationTheme.Dark);
        var html = MarkdownPreviewHtml.FromMarkdown(markdown, dark);
        await EnsureWebAsync(ct);
        ct.ThrowIfCancellationRequested();
        WebPreview.Visibility = Visibility.Visible;
        ImagePreview.Visibility = Visibility.Collapsed;
        TextHost.Visibility = Visibility.Collapsed;
        ShellHostSite.Visibility = Visibility.Collapsed;
        _shellPreview.Unload();
        WebPreview.CoreWebView2.NavigateToString(html);
    }

    private async Task ShowWebFileAsync(string path, CancellationToken ct)
    {
        await EnsureWebAsync(ct);
        ct.ThrowIfCancellationRequested();
        WebPreview.Visibility = Visibility.Visible;
        ImagePreview.Visibility = Visibility.Collapsed;
        TextHost.Visibility = Visibility.Collapsed;
        ShellHostSite.Visibility = Visibility.Collapsed;
        _shellPreview.Unload();
        WebPreview.Source = new Uri(Path.GetFullPath(path));
    }

    private bool TryShowShellPreview(string path)
    {
        if (App.MainWindow is null)
        {
            return false;
        }

        var parent = WindowNative.GetWindowHandle(App.MainWindow);
        ShellHostSite.Visibility = Visibility.Visible;
        ShellHostSite.UpdateLayout();
        UpdateLayout();
        ImagePreview.Visibility = Visibility.Collapsed;
        TextHost.Visibility = Visibility.Collapsed;
        WebPreview.Visibility = Visibility.Collapsed;

        var bounds = GetHostRectInParentClient(parent);
        if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
        {
            ShellHostSite.Visibility = Visibility.Collapsed;
            return false;
        }

        if (!_shellPreview.TryStart(path, parent, bounds))
        {
            ShellHostSite.Visibility = Visibility.Collapsed;
            return false;
        }

        return true;
    }

    private async Task EnsureWebAsync(CancellationToken ct)
    {
        if (_webReady && WebPreview.CoreWebView2 is not null)
        {
            return;
        }

        await WebPreview.EnsureCoreWebView2Async();
        ct.ThrowIfCancellationRequested();
        var core = WebPreview.CoreWebView2
            ?? throw new InvalidOperationException("WebView2 core failed to initialize.");
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.AreDevToolsEnabled = false;
        _webReady = true;
    }

    private void ShowText(string text)
    {
        _shellPreview.Unload();
        TextPreview.Text = text;
        TextHost.Visibility = Visibility.Visible;
        ImagePreview.Visibility = Visibility.Collapsed;
        WebPreview.Visibility = Visibility.Collapsed;
        ShellHostSite.Visibility = Visibility.Collapsed;
        BusyText.Visibility = Visibility.Collapsed;
    }

    private void ClearVisuals(bool keepPlaceholder)
    {
        _shellPreview.Unload();
        ImagePreview.Source = null;
        ImagePreview.Visibility = Visibility.Collapsed;
        WebPreview.Visibility = Visibility.Collapsed;
        ShellHostSite.Visibility = Visibility.Collapsed;
        TextHost.Visibility = keepPlaceholder ? Visibility.Visible : Visibility.Collapsed;
        if (keepPlaceholder)
        {
            TextPreview.Text = "Select a file to preview.";
        }

        BusyText.Visibility = Visibility.Collapsed;
    }

    private void FilePreviewControl_Unloaded(object sender, RoutedEventArgs e)
    {
        _loadCts?.Cancel();
        _shellPreview.Dispose();
    }

    private void FilePreviewControl_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_shellPreview.IsActive || App.MainWindow is null)
        {
            return;
        }

        var parent = WindowNative.GetWindowHandle(App.MainWindow);
        _shellPreview.SetBounds(GetHostRectInParentClient(parent));
    }

    private ShellPreviewHandlerSession.RECT GetHostRectInParentClient(IntPtr parentHwnd)
    {
        _ = parentHwnd;
        UIElement? root = App.MainWindow?.Content;
        if (root is null || ShellHostSite.ActualWidth <= 0 || ShellHostSite.ActualHeight <= 0)
        {
            return default;
        }

        var transform = ShellHostSite.TransformToVisual(root);
        var topLeft = transform.TransformPoint(new Point(0, 0));
        var bottomRight = transform.TransformPoint(new Point(ShellHostSite.ActualWidth, ShellHostSite.ActualHeight));
        var scale = XamlRoot?.RasterizationScale ?? 1.0;

        var left = (int)Math.Round(topLeft.X * scale);
        var top = (int)Math.Round(topLeft.Y * scale);
        var right = (int)Math.Round(bottomRight.X * scale);
        var bottom = (int)Math.Round(bottomRight.Y * scale);

        return new ShellPreviewHandlerSession.RECT
        {
            Left = Math.Max(0, left),
            Top = Math.Max(0, top),
            Right = Math.Max(left + 1, right),
            Bottom = Math.Max(top + 1, bottom),
        };
    }

    private static bool LooksLikeText(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 512_000)
            {
                return false;
            }

            Span<byte> buffer = stackalloc byte[512];
            using var stream = File.OpenRead(path);
            var read = stream.Read(buffer);
            for (var i = 0; i < read; i++)
            {
                var b = buffer[i];
                if (b == 0)
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<Windows.Storage.Streams.IRandomAccessStream> OpenRandomAccessStreamAsync(string path)
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        return await file.OpenReadAsync();
    }
}
