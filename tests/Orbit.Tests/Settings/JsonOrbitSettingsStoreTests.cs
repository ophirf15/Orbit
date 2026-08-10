using Orbit.Core.Settings;
using Orbit.Infrastructure.Settings;

namespace Orbit.Tests.Settings;

public sealed class JsonOrbitSettingsStoreTests
{
    [Fact]
    public void Load_MissingFile_CreatesDefaults_IncludingHermesUrl()
    {
        using var temp = new TempOrbitRoot();
        var store = new JsonOrbitSettingsStore(temp.Root);

        var settings = store.Load();

        Assert.Equal(OrbitSettingsDefaults.HermesBaseUrl, settings.HermesBaseUrl);
        Assert.Equal(OrbitSettingsDefaults.CoreHostBaseUrl, settings.CoreHostBaseUrl);
        Assert.Equal(OrbitSettingsDefaults.CoreHostBindAddress, settings.CoreHostBindAddress);
        Assert.Equal(ThemePreference.System, settings.ThemePreference);
        Assert.Null(settings.OneDriveSnapshotFolder);
        Assert.False(string.IsNullOrWhiteSpace(settings.DeviceId));
        Assert.False(string.IsNullOrWhiteSpace(settings.DeviceName));
        Assert.True(File.Exists(store.SettingsPath));
    }

    [Fact]
    public void Load_GeneratesStableDeviceId_Once()
    {
        using var temp = new TempOrbitRoot();
        var store = new JsonOrbitSettingsStore(temp.Root);
        var first = store.Load().DeviceId;
        var second = store.Load().DeviceId;
        Assert.Equal(first, second);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsNonSecretFields()
    {
        using var temp = new TempOrbitRoot();
        var store = new JsonOrbitSettingsStore(temp.Root);
        var settings = OrbitSettingsDefaults.CreateDefaults(temp.Root);
        settings.ThemePreference = ThemePreference.Dark;
        settings.DeveloperMode = true;
        settings.BackgroundHostEnabled = false;
        settings.HermesBaseUrl = "http://127.0.0.1:9000";
        settings.CoreHostBaseUrl = "http://127.0.0.1:8742/";
        settings.CoreHostBindAddress = "127.0.0.1";
        settings.OneDriveSnapshotFolder = null;

        store.Save(settings);
        var loaded = store.Load();

        Assert.Equal(settings.LocalDataRoot, loaded.LocalDataRoot);
        Assert.Equal(settings.GeneratedFilesRoot, loaded.GeneratedFilesRoot);
        Assert.Null(loaded.OneDriveSnapshotFolder);
        Assert.Equal(settings.HermesBaseUrl, loaded.HermesBaseUrl);
        Assert.Equal("http://127.0.0.1:8742", loaded.CoreHostBaseUrl);
        Assert.Equal("127.0.0.1", loaded.CoreHostBindAddress);
        Assert.Equal(ThemePreference.Dark, loaded.ThemePreference);
        Assert.False(loaded.BackgroundHostEnabled);
        Assert.True(loaded.DeveloperMode);
        Assert.False(string.IsNullOrWhiteSpace(loaded.DeviceId));
    }

    [Fact]
    public void Save_PreservesNullOneDriveFolder()
    {
        using var temp = new TempOrbitRoot();
        var store = new JsonOrbitSettingsStore(temp.Root);
        var settings = OrbitSettingsDefaults.CreateDefaults(temp.Root);
        settings.OneDriveSnapshotFolder = null;

        store.Save(settings);
        var json = File.ReadAllText(store.SettingsPath);

        Assert.DoesNotContain("oneDriveSnapshotFolder", json, StringComparison.OrdinalIgnoreCase);
        Assert.Null(store.Load().OneDriveSnapshotFolder);
    }

    [Fact]
    public void Load_InvalidTheme_CoercesToSystem()
    {
        using var temp = new TempOrbitRoot();
        var store = new JsonOrbitSettingsStore(temp.Root);
        Directory.CreateDirectory(temp.Root);
        File.WriteAllText(
            store.SettingsPath,
            """
            {
              "localDataRoot": "data",
              "generatedFilesRoot": "generated",
              "hermesBaseUrl": "http://127.0.0.1:8642",
              "themePreference": 99
            }
            """);

        var loaded = store.Load();

        Assert.Equal(ThemePreference.System, loaded.ThemePreference);
    }

    [Fact]
    public void SecretMaterial_DoesNotAppearInSettingsJson()
    {
        using var temp = new TempOrbitRoot();
        var store = new JsonOrbitSettingsStore(temp.Root);
        var settings = OrbitSettingsDefaults.CreateDefaults(temp.Root);
        store.Save(settings);
        store.WriteHermesApiKey(settings, "super-secret-key-value");

        var json = File.ReadAllText(store.SettingsPath);

        Assert.DoesNotContain("super-secret-key-value", json, StringComparison.Ordinal);
        Assert.Equal("super-secret-key-value", store.ReadHermesApiKey(settings));
    }

    [Fact]
    public void Save_IoFailure_DoesNotWipeExistingFile()
    {
        using var temp = new TempOrbitRoot();
        var store = new JsonOrbitSettingsStore(temp.Root);
        var settings = OrbitSettingsDefaults.CreateDefaults(temp.Root);
        settings.DeveloperMode = true;
        store.Save(settings);

        // Replace settings path directory with a file so the next atomic write fails.
        var blocker = store.SettingsPath + ".tmp";
        Directory.CreateDirectory(blocker);

        try
        {
            settings.DeveloperMode = false;
            Assert.ThrowsAny<Exception>(() => store.Save(settings));
        }
        finally
        {
            Directory.Delete(blocker, recursive: true);
        }

        var reloaded = store.Load();
        Assert.True(reloaded.DeveloperMode);
    }

    private sealed class TempOrbitRoot : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "OrbitTests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
                // best-effort
            }
        }
    }
}
