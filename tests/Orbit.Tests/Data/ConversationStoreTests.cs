using Orbit.Core.Settings;
using Orbit.Infrastructure.Data;

namespace Orbit.Tests.Data;

public sealed class ConversationStoreTests
{
    [Fact]
    public void CreateOrResumeDesktop_BindsHermesSession_AndAppendsMessages()
    {
        using var temp = new TempDb();
        var factory = new SqliteConnectionFactory(temp.DbPath);
        new SqliteMigrator(factory).ApplyPendingMigrations();
        var store = new ConversationStore(factory);

        var first = store.CreateOrResumeDesktop("hermes-sess-a", "key-a", "Agent chat");
        Assert.Equal("hermes-sess-a", first.HermesSessionId);
        Assert.Equal("key-a", first.HermesSessionKey);

        store.AppendMessage(first.Id, "user", "hello");
        store.AppendMessage(first.Id, "assistant", "hi there");

        var resumed = store.CreateOrResumeDesktop("hermes-sess-a", "key-a");
        Assert.Equal(first.Id, resumed.Id);

        var messages = store.ListMessages(first.Id);
        Assert.Equal(2, messages.Count);
        Assert.Equal("user", messages[0].Role);
        Assert.Equal("hello", messages[0].Body);
        Assert.Equal("assistant", messages[1].Role);
    }

    [Fact]
    public void SyncConversation_Telegram_UpsertsByHermesSession()
    {
        using var temp = new TempDb();
        var factory = new SqliteConnectionFactory(temp.DbPath);
        new SqliteMigrator(factory).ApplyPendingMigrations();
        var store = new ConversationStore(factory);

        var first = store.SyncConversation(
            ConversationStore.ChannelTelegram,
            hermesSessionId: "tg-1",
            hermesSessionKey: "k1",
            title: "Remote");
        Assert.Equal("telegram", first.Channel);
        Assert.Equal("tg-1", first.HermesSessionId);

        var second = store.SyncConversation(
            ConversationStore.ChannelTelegram,
            hermesSessionId: "tg-1",
            title: "Remote updated");
        Assert.Equal(first.Id, second.Id);
        Assert.Equal("Remote updated", second.Title);

        var listed = store.ListByChannel(ConversationStore.ChannelTelegram);
        Assert.Single(listed);
        Assert.Equal(first.Id, listed[0].Id);
    }

    [Fact]
    public void Migration_0005_AddsHermesColumns()
    {
        using var temp = new TempDb();
        var factory = new SqliteConnectionFactory(temp.DbPath);
        var applied = new SqliteMigrator(factory).ApplyPendingMigrations();
        Assert.Contains(applied, v => v.StartsWith("0005_", StringComparison.Ordinal));

        using var connection = factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(conversations);";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        Assert.Contains("hermes_session_id", columns);
        Assert.Contains("hermes_session_key", columns);
    }

    [Fact]
    public void Migration_0007_AppliesTelegramIndex()
    {
        using var temp = new TempDb();
        var factory = new SqliteConnectionFactory(temp.DbPath);
        var applied = new SqliteMigrator(factory).ApplyPendingMigrations();
        Assert.Contains(applied, v => v.StartsWith("0007_", StringComparison.Ordinal));
    }

    private sealed class TempDb : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "OrbitConversationTests", Guid.NewGuid().ToString("N"));

        public string DbPath => Path.Combine(Root, "data", Orbit.Core.Data.OrbitDbPaths.DatabaseFileName);

        public TempDb() => Directory.CreateDirectory(Path.Combine(Root, "data"));

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

public sealed class HermesUrlSecurityTests
{
    [Fact]
    public void LoopbackHttp_DoesNotRequireApiKey_NoWarning()
    {
        Assert.False(HermesUrlValidation.RequiresApiKey("http://127.0.0.1:8642"));
        Assert.Null(HermesUrlValidation.GetRemoteSecurityWarning("http://127.0.0.1:8642"));
    }

    [Fact]
    public void RemoteHttp_RequiresApiKey_AndWarns()
    {
        Assert.True(HermesUrlValidation.RequiresApiKey("http://192.168.1.10:8642"));
        Assert.True(HermesUrlValidation.IsInsecureRemoteHttp("http://192.168.1.10:8642"));
        Assert.False(string.IsNullOrWhiteSpace(
            HermesUrlValidation.GetRemoteSecurityWarning("http://192.168.1.10:8642")));

        Assert.False(HermesUrlValidation.TryValidateForSave(
            "http://192.168.1.10:8642",
            apiKey: null,
            out _,
            out var error,
            out _));
        Assert.Contains("API key", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemoteHttps_RequiresApiKey_NoInsecureWarning()
    {
        Assert.True(HermesUrlValidation.RequiresApiKey("https://hermes.example.com"));
        Assert.False(HermesUrlValidation.IsInsecureRemoteHttp("https://hermes.example.com"));
        Assert.Null(HermesUrlValidation.GetRemoteSecurityWarning("https://hermes.example.com"));
    }
}
