using System.Text.Json;
using System.Text.Json.Serialization;
using Orbit.Core.Sync;

namespace Orbit.Infrastructure.Sync;

public sealed class SyncLineageStore
{
    public const string FileName = "sync-lineage.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;
    private readonly object _gate = new();

    public SyncLineageStore(string localDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localDataRoot);
        Directory.CreateDirectory(localDataRoot);
        _path = System.IO.Path.Combine(localDataRoot, FileName);
    }

    public string FilePath => _path;

    public SyncLineageState Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return new SyncLineageState();
            }

            try
            {
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<SyncLineageState>(json, JsonOptions) ?? new SyncLineageState();
            }
            catch (JsonException)
            {
                return new SyncLineageState();
            }
            catch (IOException)
            {
                return new SyncLineageState();
            }
        }
    }

    public void Save(SyncLineageState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(state, JsonOptions);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }
    }

    public void MarkDirty()
    {
        lock (_gate)
        {
            var state = LoadUnlocked();
            if (state.Dirty)
            {
                return;
            }

            state.Dirty = true;
            SaveUnlocked(state);
        }
    }

    public void ClearConflict()
    {
        var state = Load();
        state.Conflict = null;
        Save(state);
    }

    private SyncLineageState LoadUnlocked()
    {
        if (!File.Exists(_path))
        {
            return new SyncLineageState();
        }

        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<SyncLineageState>(json, JsonOptions) ?? new SyncLineageState();
        }
        catch (Exception)
        {
            return new SyncLineageState();
        }
    }

    private void SaveUnlocked(SyncLineageState state)
    {
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(state, JsonOptions);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, _path, overwrite: true);
    }
}
