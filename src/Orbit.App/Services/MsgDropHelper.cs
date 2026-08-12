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
    /// <summary>Custom payload format for in-app Orbit tree drags (tasks/projects).</summary>
    public const string OrbitTreeDragFormat = "Orbit.TreeNodeId";

    /// <summary>Text payload prefix used because custom DataPackage formats are unreliable in WinUI DnD.</summary>
    public const string OrbitTreeDragPrefix = "orbit-tree:";

    public sealed class MsgDropPayload
    {
        public string? SourcePath { get; init; }

        public byte[]? Bytes { get; init; }

        public string SuggestedFileName { get; init; } = "dropped.msg";
    }

    public static bool TryParseOrbitTreeDragId(string? text, out string nodeId)
    {
        nodeId = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith(OrbitTreeDragPrefix, StringComparison.Ordinal))
        {
            nodeId = trimmed[OrbitTreeDragPrefix.Length..].Trim();
            return nodeId.Length > 0;
        }

        return false;
    }

    public static bool LooksLikeOrbitTreeDrag(DataPackageView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        try
        {
            if (view.Contains(OrbitTreeDragFormat))
            {
                return true;
            }

            // Prefixed text without file/OLE payloads — TreeView may only expose Text.
            if (view.Contains(StandardDataFormats.Text)
                && !view.Contains(StandardDataFormats.StorageItems)
                && !HasOutlookOleFormats(view))
            {
                // AvailableFormats alone can't prove the prefix; treat text-only in-app packages
                // as tree drags so email captions never steal the gesture. Real .msg drops always
                // advertise StorageItems or Outlook OLE formats.
                return true;
            }
        }
        catch (Exception)
        {
            // AvailableFormats can throw mid-drag.
        }

        return false;
    }

    public static bool LooksLikeMsgDrop(DataPackageView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (LooksLikeOrbitTreeDrag(view))
        {
            return false;
        }

        try
        {
            if (view.Contains(StandardDataFormats.StorageItems))
            {
                return true; // refined on Drop (folder vs .msg)
            }

            return HasOutlookOleFormats(view);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void AcceptMsgDrag(Microsoft.UI.Xaml.DragEventArgs e)
    {
        if (!LooksLikeMsgDrop(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.Caption = "Drop email into Orbit";
        e.DragUIOverride.IsGlyphVisible = true;
    }

    public static async Task<MsgDropPayload?> TryGetMsgAsync(DataPackageView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        if (LooksLikeOrbitTreeDrag(view))
        {
            return null;
        }

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

    private static bool HasOutlookOleFormats(DataPackageView view)
    {
        try
        {
            var formats = view.AvailableFormats.ToList();
            var hasDescriptor = formats.Any(f =>
                f.Contains("FileGroupDescriptor", StringComparison.OrdinalIgnoreCase));
            var hasContents = formats.Any(f =>
                f.Equals("FileContents", StringComparison.OrdinalIgnoreCase));
            return hasDescriptor && hasContents;
        }
        catch (Exception)
        {
            return false;
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
