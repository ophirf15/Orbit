using Orbit.Core.Data;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Data.Demo;
using Orbit.Infrastructure.Suggestions;

namespace Orbit.Tests.Suggestions;

public sealed class SuggestionHygieneTests
{
    [Fact]
    public void Create_SameGroupKey_DoesNotDuplicate_KeepsHigherConfidence()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var suggestions = new SuggestionStore(factory);
        var key = SuggestionHygiene.MergeIntoTaskKey("task-1", "email-1");

        var first = suggestions.Create(new CreateSuggestionRequest
        {
            SuggestionType = SuggestionTypes.MergeIntoTask,
            Summary = "First",
            GroupKey = key,
            Confidence = 0.4,
            PayloadJson = """{"taskId":"task-1","text":"a","field":"body","sourceId":"email-1"}""",
        });

        var second = suggestions.Create(new CreateSuggestionRequest
        {
            SuggestionType = SuggestionTypes.MergeIntoTask,
            Summary = "Second stronger",
            GroupKey = key,
            Confidence = 0.7,
            PayloadJson = """{"taskId":"task-1","text":"b","field":"body","sourceId":"email-1"}""",
        });

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("Second stronger", second.Summary);
        Assert.Equal(0.7, second.Confidence);
        Assert.Single(suggestions.List(SuggestionStatuses.Pending));
    }

    [Fact]
    public void BatchDecide_Reject_MarksAllRejected()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var suggestions = new SuggestionStore(factory);
        var ids = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            var created = suggestions.Create(new CreateSuggestionRequest
            {
                SuggestionType = SuggestionTypes.LinkTasks,
                Summary = $"Link {i}",
                GroupKey = $"a{i}|b{i}|relates",
                Confidence = 0.6,
            });
            ids.Add(created.Id);
        }

        var results = suggestions.BatchDecide(ids, "reject", actor: "test");
        Assert.Equal(10, results.Count);
        Assert.All(results, r => Assert.True(r.Ok));
        Assert.Empty(suggestions.List(SuggestionStatuses.Pending));
        Assert.Equal(10, suggestions.List(SuggestionStatuses.Rejected).Count);
    }

    [Fact]
    public void ExpireOlderThan_MarksAgedPendingExpired()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var suggestions = new SuggestionStore(factory);
        var created = suggestions.Create(new CreateSuggestionRequest
        {
            SuggestionType = SuggestionTypes.ReportingRelationship,
            Summary = "Old reporting",
            GroupKey = "p1|p2",
            Confidence = 0.4,
        });

        BackdateCreatedAt(factory, created.Id, DateTime.UtcNow.AddDays(-20));

        var n = suggestions.ExpireOlderThan(TimeSpan.FromDays(14));
        Assert.Equal(1, n);
        var row = suggestions.Get(created.Id);
        Assert.NotNull(row);
        Assert.Equal(SuggestionStatuses.Expired, row!.Status);
        Assert.Empty(suggestions.List(SuggestionStatuses.Pending));
    }

    [Fact]
    public void WorkbenchBadge_ExcludesLowConfidenceAndReviewLimbo()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var ids = new DemoGraphSeed(factory).Seed();
        var suggestions = new SuggestionStore(factory);
        var baseline = Assert.Single(
            new WorkbenchReadStore(factory).GetSnapshot().Cells,
            c => c.Id == ids.HarborProjectId).PendingSuggestionCount;

        suggestions.Create(new CreateSuggestionRequest
        {
            SuggestionType = SuggestionTypes.MergeIntoTask,
            Summary = "Low",
            ProjectId = ids.HarborProjectId,
            GroupKey = "t|e-low",
            Confidence = 0.4,
        });
        suggestions.Create(new CreateSuggestionRequest
        {
            SuggestionType = SuggestionTypes.MergeIntoTask,
            Summary = "Null conf",
            ProjectId = ids.HarborProjectId,
            GroupKey = "t|e-null",
            Confidence = null,
        });
        suggestions.Create(new CreateSuggestionRequest
        {
            SuggestionType = SuggestionTypes.ReviewLimbo,
            Summary = "Limbo chore",
            ProjectId = ids.HarborProjectId,
            GroupKey = "limbo-1",
            Confidence = 0.9,
        });

        var afterNoise = Assert.Single(
            new WorkbenchReadStore(factory).GetSnapshot().Cells,
            c => c.Id == ids.HarborProjectId).PendingSuggestionCount;
        Assert.Equal(baseline, afterNoise);

        suggestions.Create(new CreateSuggestionRequest
        {
            SuggestionType = SuggestionTypes.LinkTasks,
            Summary = "Actionable",
            ProjectId = ids.HarborProjectId,
            GroupKey = "a|b|blocks",
            Confidence = 0.7,
        });

        var afterActionable = Assert.Single(
            new WorkbenchReadStore(factory).GetSnapshot().Cells,
            c => c.Id == ids.HarborProjectId).PendingSuggestionCount;
        Assert.Equal(baseline + 1, afterActionable);

        var low = suggestions.List(queue: SuggestionHygiene.QueueLow);
        Assert.Equal(2, low.Count);
        var review = suggestions.List(queue: SuggestionHygiene.QueueReview);
        Assert.Contains(review, s => s.Summary == "Actionable");
        Assert.DoesNotContain(review, s => s.SuggestionType == SuggestionTypes.ReviewLimbo);
    }

    private static void BackdateCreatedAt(SqliteConnectionFactory factory, string id, DateTime when)
    {
        using var connection = factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE agent_suggestions SET created_at = $t WHERE id = $id;";
        cmd.Parameters.AddWithValue("$t", when.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
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
            Path.Combine(Path.GetTempPath(), "OrbitSuggestionHygiene", Guid.NewGuid().ToString("N"));

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
