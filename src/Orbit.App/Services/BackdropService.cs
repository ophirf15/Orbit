using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Orbit_App.Services;

public static class BackdropService
{
    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        try
        {
            window.SystemBackdrop = new MicaBackdrop();
            return;
        }
        catch (Exception)
        {
            // Fall through.
        }

        try
        {
            window.SystemBackdrop = new DesktopAcrylicBackdrop();
            return;
        }
        catch (Exception)
        {
            // Fall through.
        }

        window.SystemBackdrop = null;
    }
}
