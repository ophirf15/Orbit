using Orbit.Core.Agent;
using Orbit.Core.Operator;
using Orbit.Core.Settings;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Data.Demo;
using Orbit.Infrastructure.Email;
using Orbit.Infrastructure.Operator;

namespace Orbit.Tests.Operator;

public sealed class DutyOperatorTests
{
    [Fact]
    public void Migration_CreatesOperatorTables()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        using var connection = factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name IN ('operator_rules', 'operator_memory', 'operator_runs')
            ORDER BY name;
            """;
        var names = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        Assert.Equal(["operator_memory", "operator_rules", "operator_runs"], names);
    }

    [Fact]
    public void StandingRule_CreateTask_AppliesWithoutConfirm()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var ids = new DemoGraphSeed(factory).Seed();
        var rules = new StandingRulesStore(factory);
        var engine = new StandingRuleEngine(
            rules,
            new OrbitMutationStore(factory),
            new TaskEmailThreadStore(factory),
            new NoteWriteStore(factory));

        rules.Create(new CreateOperatorRuleRequest
        {
            Name = "MetroFiber follow-up",
            TriggerKind = OperatorTriggers.EmailIngested,
            ActionKind = OperatorActions.CreateTask,
            MatchJson = $"{{\"projectId\":\"{ids.HarborProjectId}\",\"subjectContains\":\"MetroFiber\"}}",
            ParamsJson = $"{{\"projectId\":\"{ids.HarborProjectId}\",\"titleTemplate\":\"Follow up: {{subject}}\"}}",
            Enabled = true,
            RequireConfirm = false,
        });

        var results = engine.ApplyMatching(
            OperatorTriggers.EmailIngested,
            new OperatorMatchContext
            {
                ProjectId = ids.HarborProjectId,
                Subject = "MetroFiber install window",
            });

        var applied = Assert.Single(results, r => r.Applied);
        Assert.False(string.IsNullOrWhiteSpace(applied.EntityId));

        using var connection = factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT title FROM tasks WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", applied.EntityId!);
        Assert.Equal("Follow up: MetroFiber install window", cmd.ExecuteScalar() as string);
    }

    [Fact]
    public void StandingRule_RequireConfirm_SkipsApply()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var ids = new DemoGraphSeed(factory).Seed();
        var rules = new StandingRulesStore(factory);
        var engine = new StandingRuleEngine(
            rules,
            new OrbitMutationStore(factory),
            new TaskEmailThreadStore(factory),
            new NoteWriteStore(factory));

        rules.Create(new CreateOperatorRuleRequest
        {
            Name = "Needs confirm",
            TriggerKind = OperatorTriggers.EmailIngested,
            ActionKind = OperatorActions.CreateTask,
            ParamsJson = $"{{\"projectId\":\"{ids.HarborProjectId}\",\"title\":\"Should not create\"}}",
            RequireConfirm = true,
        });

        var results = engine.ApplyMatching(
            OperatorTriggers.EmailIngested,
            new OperatorMatchContext { ProjectId = ids.HarborProjectId });

        Assert.Contains(results, r => r.SkippedConfirm && !r.Applied);
        using var connection = factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM tasks WHERE title = 'Should not create';";
        Assert.Equal(0, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    [Fact]
    public void OperatorMemory_RememberAndList()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var memory = new OperatorMemoryStore(factory);
        memory.Remember(new RememberRequest
        {
            Kind = OperatorMemoryKinds.Preference,
            Scope = "global",
            Text = "Prefer concise briefings",
            Source = "test",
        });
        memory.Remember(new RememberRequest
        {
            Kind = OperatorMemoryKinds.ProjectFact,
            Scope = "harbor",
            Text = "Harbor Court fiber is blocked on PG&E",
            Source = "test",
        });

        var facts = memory.List();
        Assert.Equal(2, facts.Count);
        Assert.Contains(facts, f => f.Text == "Prefer concise briefings");
        Assert.Contains(facts, f => f.Text == "Harbor Court fiber is blocked on PG&E");
    }

    [Fact]
    public void OperatorPromptBuilder_IsSlim_NoPersonaNoMemoryDump()
    {
        var prompt = OperatorPromptBuilder.Build(OperatorTriggers.DutyScan, """{"window":"morning"}""");
        Assert.Contains("Trigger: duty.scan", prompt, StringComparison.Ordinal);
        Assert.Contains("SOUL.md", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("You are Hermes", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Enabled standing rules", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Operator memory", prompt, StringComparison.Ordinal);

        var emailPrompt = OperatorPromptBuilder.Build(
            OperatorTriggers.EmailIngested,
            """{"emailId":"e1"}""",
            emailSnapshotJson: """{"id":"e1","subject":"Hello"}""");
        Assert.Contains("Email snapshot", emailPrompt, StringComparison.Ordinal);
        Assert.Contains("Hello", emailPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("You are Hermes", emailPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void OperatorRunStore_CooldownHelpers()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var runs = new OperatorRunStore(factory);
        Assert.Null(runs.LastCompletedUtc());
        Assert.Equal(0, runs.CountRunning());

        var run = runs.Start(OperatorTriggers.EmailIngested, "{}");
        Assert.Equal(1, runs.CountRunning());
        runs.Complete(run.Id, OperatorRunStatuses.Completed, briefingSummary: "Do X then Y");
        Assert.Equal(0, runs.CountRunning());
        Assert.NotNull(runs.LastCompletedUtc());
        Assert.Contains("Do X", runs.ListRecent(1)[0].BriefingSummary);
    }

    [Fact]
    public void HermesPairing_DeriveDashboardUrl_Unchanged()
    {
        Assert.Equal("http://127.0.0.1:9119", HermesPairing.DeriveDashboardUrl("http://127.0.0.1:8642"));
        Assert.Equal("http://192.168.1.19:9119", HermesPairing.DeriveDashboardUrl("http://192.168.1.19:8642/"));
    }

    private static SqliteConnectionFactory OpenMigrated(TempDb temp)
    {
        var factory = new SqliteConnectionFactory(temp.DbPath);
        new SqliteMigrator(factory).ApplyPendingMigrations();
        return factory;
    }

    private sealed class TempDb : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "OrbitOperatorTests", Guid.NewGuid().ToString("N"));

        public string DbPath => Path.Combine(Root, "orbit.db");

        public TempDb() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // ignore
            }
        }
    }
}
