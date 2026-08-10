using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Orbit_App.Services;

/// <summary>
/// Resolves a dropped .msg from Explorer (StorageItems) or Outlook OLE (FileContents), when WinUI exposes it.
/// </summary>
public static class MsgDropHelper
{
    public sealed class MsgDropPayload
    {
        public string? SourcePath { get; init; }

        public byte[]? Bytes { get; init; }

        public string SuggestedFileName { get; init; } = "dropped.msg";
    }

    public static void AcceptMsgDrag(Microsoft.UI.Xaml.DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.Caption = "Drop email into Orbit";
        e.DragUIOverride.IsGlyphVisible = true;
    }

    public static async Task<MsgDropPayload?> TryGetMsgAsync(DataPackageView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        if (view.Contains(StandardDataFormats.StorageItems))
        {
            try
            {
                var items = await view.GetStorageItemsAsync();
                var file = items.OfType<StorageFile>()
                    .FirstOrDefault(f => f.FileType.Equals(".msg", StringComparison.OrdinalIgnoreCase));
                if (file is not null)
                {
                    return new MsgDropPayload
                    {
                        SourcePath = file.Path,
                        SuggestedFileName = file.Name,
                    };
                }
            }
            catch (Exception)
            {
                // Outlook sometimes advertises StorageItems then fails when queried.
            }
        }

        IReadOnlyList<string> formats;
        try
        {
            formats = view.AvailableFormats.ToList();
        }
        catch (Exception)
        {
            return null;
        }

        // Classic Outlook: FileGroupDescriptor(W) + FileContents
        var hasDescriptor = formats.Any(f =>
            f.Contains("FileGroupDescriptor", StringComparison.OrdinalIgnoreCase));
        var contentsFormat = formats.FirstOrDefault(f =>
            f.Equals("FileContents", StringComparison.OrdinalIgnoreCase));
        if (!hasDescriptor || contentsFormat is null)
        {
            return null;
        }

        try
        {
            var data = await view.GetDataAsync(contentsFormat);
            await using var stream = await AsStreamAsync(data);
            if (stream is null)
            {
                return null;
            }

            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            if (ms.Length == 0)
            {
                return null;
            }

            var name = await TryReadFirstDescriptorNameAsync(view, formats) ?? "outlook-drop.msg";
            if (!name.EndsWith(".msg", StringComparison.OrdinalIgnoreCase))
            {
                name += ".msg";
            }

            return new MsgDropPayload
            {
                Bytes = ms.ToArray(),
                SuggestedFileName = name,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<string?> TryReadFirstDescriptorNameAsync(
        DataPackageView view,
        IReadOnlyList<string> formats)
    {
        var descriptorFormat = formats.FirstOrDefault(f =>
            f.Equals("FileGroupDescriptorW", StringComparison.OrdinalIgnoreCase))
            ?? formats.FirstOrDefault(f =>
                f.Equals("FileGroupDescriptor", StringComparison.OrdinalIgnoreCase));
        if (descriptorFormat is null)
        {
            return null;
        }

        try
        {
            var data = await view.GetDataAsync(descriptorFormat);
            await using var stream = await AsStreamAsync(data);
            if (stream is null)
            {
                return null;
            }

            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            var bytes = ms.ToArray();
            if (bytes.Length < 8)
            {
                return null;
            }

            // FILEGROUPDESCRIPTORW: DWORD cItems, then FILEDESCRIPTORW[0].cFileName at known offset.
            // Unicode FILEDESCRIPTORW: cFileName starts at offset 72 within each descriptor.
            const int cFileNameOffset = 72;
            if (bytes.Length < 4 + cFileNameOffset + 2)
            {
                return null;
            }

            var nameBytes = bytes.AsSpan(4 + cFileNameOffset, Math.Min(520, bytes.Length - 4 - cFileNameOffset));
            var name = System.Text.Encoding.Unicode.GetString(nameBytes);
            var z = name.IndexOf('\0');
            if (z >= 0)
            {
                name = name[..z];
            }

            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<Stream?> AsStreamAsync(object data)
    {
        switch (data)
        {
            case IRandomAccessStream ras:
                return ras.AsStreamForRead();
            case IRandomAccessStreamReference reference:
            {
                var withContent = await reference.OpenReadAsync();
                return withContent.AsStreamForRead();
            }
            case IInputStream input:
                return input.AsStreamForRead();
            case byte[] bytes:
                return new MemoryStream(bytes);
            case Stream stream:
                return stream;
            default:
                return null;
        }
    }
}
