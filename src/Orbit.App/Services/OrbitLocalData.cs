using Orbit.Infrastructure.Data;

namespace Orbit_App.Services;

/// <summary>
/// Lazily opens the local Orbit SQLite DB (same LocalDataRoot as Core Host) for App-side stores.
/// </summary>
public static class OrbitLocalData
{
    private static readonly object Gate = new();
    private static OrbitDatabase? _database;
    private static ConversationStore? _conversations;

    public static ConversationStore Conversations
    {
        get
        {
            EnsureOpened();
            return _conversations!;
        }
    }

    public static void EnsureOpened()
    {
        if (_database is not null && _conversations is not null)
        {
            return;
        }

        lock (Gate)
        {
            if (_database is not null && _conversations is not null)
            {
                return;
            }

            var root = App.Settings.LocalDataRoot;
            _database = OrbitDatabase.Open(root);
            _conversations = new ConversationStore(_database.Factory);
        }
    }
}
