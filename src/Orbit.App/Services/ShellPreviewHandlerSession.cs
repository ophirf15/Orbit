using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Orbit_App.Services;

/// <summary>
/// Hosts Windows Shell IPreviewHandler COM objects (Explorer preview pane handlers).
/// </summary>
internal sealed class ShellPreviewHandlerSession : IDisposable
{
    private static readonly Guid PreviewHandlerIid = new("8895b1c6-b41f-4c1c-a562-0d564250836f");

    private IPreviewHandler? _handler;
    private IntPtr _hostHwnd;
    private bool _disposed;

    public bool IsActive => _handler is not null && _hostHwnd != IntPtr.Zero;

    public static bool TryFindHandlerClsid(string extension, out Guid clsid)
    {
        clsid = Guid.Empty;
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        var ext = extension.StartsWith('.') ? extension : "." + extension;
        var handlerIid = PreviewHandlerIid.ToString("B");
        var guidText =
            ReadPreviewHandlerGuid($@"{ext}\ShellEx\{handlerIid}")
            ?? ReadPreviewHandlerGuid($@"SystemFileAssociations\{ext}\ShellEx\{handlerIid}");

        if (guidText is null)
        {
            using var extKey = Registry.ClassesRoot.OpenSubKey(ext);
            var progId = extKey?.GetValue(null) as string;
            if (!string.IsNullOrWhiteSpace(progId))
            {
                guidText = ReadPreviewHandlerGuid($@"{progId}\ShellEx\{handlerIid}");
            }
        }

        return guidText is not null && Guid.TryParse(guidText, out clsid);
    }

    public bool TryStart(string filePath, IntPtr parentHwnd, RECT bounds)
    {
        Unload();
        if (parentHwnd == IntPtr.Zero || !File.Exists(filePath))
        {
            return false;
        }

        var ext = Path.GetExtension(filePath);
        if (!TryFindHandlerClsid(ext, out var clsid))
        {
            return false;
        }

        try
        {
            var type = Type.GetTypeFromCLSID(clsid, throwOnError: true)!;
            var instance = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("Preview handler activation returned null.");

            if (instance is IInitializeWithFile initFile)
            {
                initFile.Initialize(filePath, 0);
            }
            else if (instance is IInitializeWithItem initItem)
            {
                var item = CreateShellItem(filePath);
                if (item is null)
                {
                    Marshal.ReleaseComObject(instance);
                    return false;
                }

                initItem.Initialize(item, 0);
                Marshal.ReleaseComObject(item);
            }
            else
            {
                Marshal.ReleaseComObject(instance);
                return false;
            }

            _hostHwnd = CreateHostWindow(parentHwnd, bounds);
            if (_hostHwnd == IntPtr.Zero)
            {
                Marshal.ReleaseComObject(instance);
                return false;
            }

            _handler = (IPreviewHandler)instance;
            _handler.SetWindow(_hostHwnd, ref bounds);
            _handler.SetRect(ref bounds);
            _handler.DoPreview();
            return true;
        }
        catch (Exception)
        {
            Unload();
            return false;
        }
    }

    public void SetBounds(RECT bounds)
    {
        if (_hostHwnd != IntPtr.Zero)
        {
            SetWindowPos(
                _hostHwnd,
                IntPtr.Zero,
                bounds.Left,
                bounds.Top,
                Math.Max(1, bounds.Right - bounds.Left),
                Math.Max(1, bounds.Bottom - bounds.Top),
                SWP_NOZORDER | SWP_NOACTIVATE);
        }

        _handler?.SetRect(ref bounds);
    }

    public void Unload()
    {
        if (_handler is not null)
        {
            try
            {
                _handler.Unload();
            }
            catch (Exception)
            {
            }

            try
            {
                Marshal.ReleaseComObject(_handler);
            }
            catch (Exception)
            {
            }

            _handler = null;
        }

        if (_hostHwnd != IntPtr.Zero)
        {
            DestroyWindow(_hostHwnd);
            _hostHwnd = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unload();
    }

    private static string? ReadPreviewHandlerGuid(string subKeyPath)
    {
        // Accept either HKCR-relative (".pdf\ShellEx\{…}") or HKLM Software\Classes\… paths.
        if (subKeyPath.StartsWith(@"Software\Classes\", StringComparison.OrdinalIgnoreCase))
        {
            var relative = subKeyPath[@"Software\Classes\".Length..];
            using var hkcr = Registry.ClassesRoot.OpenSubKey(relative);
            if (hkcr?.GetValue(null) is string fromClasses)
            {
                return fromClasses;
            }

            using var hklm = Registry.LocalMachine.OpenSubKey(subKeyPath);
            if (hklm?.GetValue(null) is string fromLlm)
            {
                return fromLlm;
            }

            using var hkcu = Registry.CurrentUser.OpenSubKey(subKeyPath);
            return hkcu?.GetValue(null) as string;
        }

        using var key = Registry.ClassesRoot.OpenSubKey(subKeyPath);
        return key?.GetValue(null) as string;
    }

    private static IShellItem? CreateShellItem(string path)
    {
        var hr = SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItem).GUID, out var item);
        return hr == 0 ? (IShellItem)item : null;
    }

    private static IntPtr CreateHostWindow(IntPtr parent, RECT bounds)
    {
        var width = Math.Max(1, bounds.Right - bounds.Left);
        var height = Math.Max(1, bounds.Bottom - bounds.Top);
        return CreateWindowEx(
            0,
            "Static",
            string.Empty,
            WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
            bounds.Left,
            bounds.Top,
            width,
            height,
            parent,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
    }

    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("8895b1c6-b41f-4c1c-a562-0d564250836f")]
    private interface IPreviewHandler
    {
        void SetWindow(IntPtr hwnd, ref RECT prc);
        void SetRect(ref RECT prc);
        void DoPreview();
        void Unload();
        void SetFocus();
        void QueryFocus(out IntPtr phwnd);
        [PreserveSig]
        uint TranslateAccelerator(IntPtr pmsg);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("b7d14566-1744-4359-9373-1ab5a9b2bbf7")]
    private interface IInitializeWithFile
    {
        void Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("7F73BE3F-FB79-493C-A6C7-7EE14E245841")]
    private interface IInitializeWithItem
    {
        void Initialize(IShellItem psi, uint grfMode);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    private interface IShellItem
    {
    }
}
