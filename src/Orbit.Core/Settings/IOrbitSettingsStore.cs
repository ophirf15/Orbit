namespace Orbit.Core.Settings;

public interface IOrbitSettingsStore
{
    OrbitSettings Load();

    void Save(OrbitSettings settings);
}
