using Orbit.Core.Data;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Data.Demo;

namespace Orbit.Tests.Data;

public sealed class TelegramProvenanceTests
{
    [Fact]
    public void CreateTask_WithTelegramProvenance_WritesAuditAndRemoteActivity()
    {
        using var temp = new TempDb();
        var factory = new SqliteConnectionFactory(temp.DbPath);
        new SqliteMigrator(factory).ApplyPendingMigrations();
        var ids = new DemoGraphSeed(factory).SeedIfEmpty();

        var conversations = new ConversationStore(factory);
        conversations.SyncConversation(
            ConversationStore.ChannelTelegram,
            hermesSessionId: "tg-unit",
            title: "Unit telegram");

        var mutations = new OrbitMutationStore(factory);
        var task = mutations.CreateTask(
            "Telegram follow-up",
            ids.HarborProjectId,
            status: null,
            actor: "agent",
            provenance: new MutationProvenance
            {
                Actor = "hermes",
                Channel = "telegram",
                HermesSessionId = "tg-unit",
                ExternalUserId = "user-9",
            });

        Assert.False(string.IsNullOrWhiteSpace(task.Id));

        using (var connection = factory.CreateConnection())
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT detail_json FROM audit_events
                WHERE event_type = 'task.created' AND entity_id = $id
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$id", task.Id);
            var detail = cmd.ExecuteScalar() as string;
            Assert.False(string.IsNullOrWhiteSpace(detail));
            Assert.Contains("\"channel\":\"telegram\"", detail, StringComparison.Ordinal);
            Assert.Contains("\"hermesSessionId\":\"tg-unit\"", detail, StringComparison.Ordinal);
            Assert.Contains("\"externalUserId\":\"user-9\"", detail, StringComparison.Ordinal);
        }

        var activity = new RemoteActivityStore(factory).GetRemoteActivity();
        Assert.Contains(activity.Conversations, c => c.HermesSessionId == "tg-unit");
        Assert.Contains(
            activity.AuditEvents,
            a => a.EventType == "task.created"
                && a.Channel == "telegram"
                && a.HermesSessionId == "tg-unit"
                && a.EntityId == task.Id);
    }

    private sealed class TempDb : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "OrbitTelegramProvTests", Guid.NewGuid().ToString("N"));

        public string DbPath => Path.Combine(Root, "data", OrbitDbPaths.DatabaseFileName);

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
