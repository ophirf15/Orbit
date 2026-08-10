using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Orbit_App.Services;

/// <summary>Resolves a dropped folder from Explorer StorageItems.</summary>
public static class FolderDropHelper
{
    public static async Task<string?> TryGetFolderPathAsync(DataPackageView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (!view.Contains(StandardDataFormats.StorageItems))
        {
            return null;
        }

        try
        {
            var items = await view.GetStorageItemsAsync();
            var folder = items.OfType<StorageFolder>().FirstOrDefault();
            if (folder is not null && !string.IsNullOrWhiteSpace(folder.Path))
            {
                return folder.Path;
            }

            // Directory path dropped as a StorageFile in some shells — accept if it exists as a directory.
            foreach (var file in items.OfType<StorageFile>())
            {
                if (!string.IsNullOrWhiteSpace(file.Path) && Directory.Exists(file.Path))
                {
                    return file.Path;
                }
            }
        }
        catch (Exception)
        {
            return null;
        }

        return null;
    }

    public static bool LooksLikeFolderDrop(DataPackageView view)
    {
        try
        {
            return view.Contains(StandardDataFormats.StorageItems);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
