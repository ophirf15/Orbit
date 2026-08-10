using Microsoft.Data.Sqlite;
using Orbit.Core.Data;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Data.Demo;

namespace Orbit.IntegrationTests.Data;

public sealed class GraphContextSeedTests
{
    [Fact]
    public void Seed_ModelsMetroFiberServingTwoProjects_WithoutMergingTasks()
    {
        using var temp = new TempDb();
        var db = OrbitDatabase.Open(temp.DataRoot);
        var ids = db.SeedDemoIfEmpty();

        using var connection = db.Factory.CreateConnection();

        // Two serves edges with distinct project context
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT project_id FROM relationships
                WHERE source_type = $st AND source_id = $sid AND relationship_type = $rt
                ORDER BY project_id;
                """;
            cmd.Parameters.AddWithValue("$st", EntityTypes.Organization);
            cmd.Parameters.AddWithValue("$sid", ids.MetroFiberOrgId);
            cmd.Parameters.AddWithValue("$rt", RelationshipTypes.Serves);
            var projects = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                projects.Add(reader.GetString(0));
            }

            Assert.Equal(2, projects.Count);
            Assert.Contains(ids.HarborProjectId, projects);
            Assert.Contains(ids.RiverviewProjectId, projects);
            Assert.NotEqual(ids.HarborProjectId, ids.RiverviewProjectId);
        }

        // Tasks share title pattern but not identity or project context
        Assert.NotEqual(ids.HarborTaskId, ids.RiverviewTaskId);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT project_id, workstream_id FROM tasks WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", ids.HarborTaskId);
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(ids.HarborProjectId, reader.GetString(0));
            Assert.Equal(ids.HarborInternetWorkstreamId, reader.GetString(1));
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT project_id, workstream_id FROM tasks WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", ids.RiverviewTaskId);
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(ids.RiverviewProjectId, reader.GetString(0));
            Assert.Equal(ids.RiverviewInternetWorkstreamId, reader.GetString(1));
        }

        // Filtering by project does not return the other property's task
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM tasks WHERE project_id = $p AND id = $other;";
            cmd.Parameters.AddWithValue("$p", ids.HarborProjectId);
            cmd.Parameters.AddWithValue("$other", ids.RiverviewTaskId);
            Assert.Equal(0, Convert.ToInt32(cmd.ExecuteScalar()));
        }
    }

    [Fact]
    public void Seed_ContactBelongsToOrgAndMultipleProjectContexts()
    {
        using var temp = new TempDb();
        var db = OrbitDatabase.Open(temp.DataRoot);
        var ids = db.SeedDemoIfEmpty();

        using var connection = db.Factory.CreateConnection();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                "SELECT COUNT(*) FROM organization_memberships WHERE person_id = $p AND organization_id = $o;";
            cmd.Parameters.AddWithValue("$p", ids.ContactPersonId);
            cmd.Parameters.AddWithValue("$o", ids.MetroFiberOrgId);
            Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar()));
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT COUNT(DISTINCT project_id) FROM relationships
                WHERE source_type = $st AND source_id = $sid AND relationship_type = $rt;
                """;
            cmd.Parameters.AddWithValue("$st", EntityTypes.Person);
            cmd.Parameters.AddWithValue("$sid", ids.ContactPersonId);
            cmd.Parameters.AddWithValue("$rt", RelationshipTypes.InvolvedIn);
            Assert.Equal(2, Convert.ToInt32(cmd.ExecuteScalar()));
        }
    }

    [Fact]
    public void Seed_EmailLinksTwoProjects_ExtractionsActionsOnSeparateWorkstreams()
    {
        using var temp = new TempDb();
        var db = OrbitDatabase.Open(temp.DataRoot);
        var ids = db.SeedDemoIfEmpty();

        using var connection = db.Factory.CreateConnection();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM email_project_links WHERE email_artifact_id = $e;";
            cmd.Parameters.AddWithValue("$e", ids.SharedEmailId);
            Assert.Equal(2, Convert.ToInt32(cmd.ExecuteScalar()));
        }

        string WorkstreamFor(string extractionId)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT workstream_id FROM email_extractions WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", extractionId);
            return (string)(cmd.ExecuteScalar() ?? throw new InvalidOperationException("missing extraction"));
        }

        var harborWs = WorkstreamFor(ids.HarborExtractionId);
        var riverviewWs = WorkstreamFor(ids.RiverviewExtractionId);
        Assert.Equal(ids.HarborInternetWorkstreamId, harborWs);
        Assert.Equal(ids.RiverviewInternetWorkstreamId, riverviewWs);
        Assert.NotEqual(harborWs, riverviewWs);
    }

    [Fact]
    public void RelationshipEdge_RequiresDistinctProjectContext_ForSharedVendor()
    {
        using var temp = new TempDb();
        var db = OrbitDatabase.Open(temp.DataRoot);
        var ids = db.SeedDemoIfEmpty();

        using var connection = db.Factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT r1.id, r2.id
            FROM relationships r1
            JOIN relationships r2 ON r1.source_id = r2.source_id AND r1.relationship_type = r2.relationship_type
            WHERE r1.id = $a AND r2.id = $b
              AND r1.project_id != r2.project_id;
            """;
        cmd.Parameters.AddWithValue("$a", ids.MetroFiberServesHarborRelId);
        cmd.Parameters.AddWithValue("$b", ids.MetroFiberServesRiverviewRelId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read(), "Expected two MetroFiber serves edges with different project_id context.");
    }

    private sealed class TempDb : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "OrbitGraphTests", Guid.NewGuid().ToString("N"));

        public string DataRoot => Path.Combine(Root, "data");

        public TempDb() => Directory.CreateDirectory(DataRoot);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
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
