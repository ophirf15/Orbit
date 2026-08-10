namespace Orbit_App.Services;

/// <summary>
/// Future OS-wide capture hotkey registration. Phase 5 ships a no-op implementation.
/// </summary>
public interface IGlobalCaptureHotkeyRegistrar
{
    void Register(Action onCaptureRequested);

    void Unregister();
}

public sealed class NullGlobalCaptureHotkeyRegistrar : IGlobalCaptureHotkeyRegistrar
{
    public void Register(Action onCaptureRequested)
    {
        // Intentionally empty — Win32 RegisterHotKey arrives with packaging / Phase 17+.
    }

    public void Unregister()
    {
    }
}
