using Orbit.Core.Data;
using Orbit.Infrastructure.Contacts;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Email;

namespace Orbit.Tests.Contacts;

public sealed class ContactEnrichmentTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "OrbitContactUnit", Guid.NewGuid().ToString("N"));

    private readonly SqliteConnectionFactory _factory;
    private readonly ContactStore _contacts;
    private readonly EmailContactEnricher _enricher;

    public ContactEnrichmentTests()
    {
        Directory.CreateDirectory(_root);
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        _factory = new SqliteConnectionFactory(OrbitDbPaths.GetDatabasePath(data));
        new SqliteMigrator(_factory).ApplyPendingMigrations();
        _contacts = new ContactStore(_factory);
        _enricher = new EmailContactEnricher(_contacts);
    }

    [Fact]
    public void SameEmail_ResolvesToOnePerson()
    {
        var emailId1 = Guid.NewGuid().ToString("D");
        var emailId2 = Guid.NewGuid().ToString("D");
        SeedEmail(emailId1);
        SeedEmail(emailId2);

        var participants = new[]
        {
            new ParsedEmailParticipant
            {
                Role = "from",
                Address = "alex.rivera@metrofiber.example",
                DisplayName = "Alex Rivera",
            },
        };

        var first = _enricher.Enrich(emailId1, participants, bodyText: null);
        var second = _enricher.Enrich(emailId2, participants, bodyText: null);

        Assert.Single(first.PersonIds);
        Assert.Equal(first.PersonIds[0], second.PersonIds[0]);
        Assert.Equal(1, _contacts.ListPeople().Count(p =>
            string.Equals(p.PrimaryEmail, "alex.rivera@metrofiber.example", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void SignaturePhone_CapturedWithProvenance()
    {
        var emailId = Guid.NewGuid().ToString("D");
        SeedEmail(emailId);
        var body =
            """
            Thanks,

            --
            Alex Rivera
            Account Manager
            Mobile: 415-555-0198
            MetroFiber Business
            """;

        var result = _enricher.Enrich(
            emailId,
            [
                new ParsedEmailParticipant
                {
                    Role = "from",
                    Address = "alex.rivera@metrofiber.example",
                    DisplayName = "Alex Rivera",
                },
            ],
            body);

        var personId = Assert.Single(result.PersonIds);
        var detail = _contacts.GetPerson(personId);
        Assert.NotNull(detail);
        Assert.Contains(detail!.Methods, m =>
            m.MethodType == ContactMethodTypes.Mobile
            && ContactResolution.NormalizePhone(m.Value) == "4155550198");
        Assert.Equal("Account Manager", detail.Title);
        Assert.Contains(detail.Provenance, p =>
            p.Field == "mobile"
            && p.SourceKind == ContactSourceKinds.SignatureHeuristic
            && p.SourceEmailId == emailId);
    }

    [Fact]
    public void UpdateContact_AddsMobile_WithAudit()
    {
        var personId = _contacts.UpsertPersonByEmail(
            "jen@vendor.example",
            "Jennifer Lee",
            sourceEmailId: null,
            ContactSourceKinds.UserUpdate);

        var updated = _contacts.UpdateContact(
            personId,
            new ContactPatch { Mobile = "415-555-0198" },
            provenance: "Add 415-555-0198 as Jennifer's mobile number.",
            requestedBy: "agent:hermes");

        Assert.Contains(updated.Methods, m =>
            m.MethodType == ContactMethodTypes.Mobile
            && ContactResolution.NormalizePhone(m.Value) == "4155550198");

        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT COUNT(*) FROM audit_events
            WHERE event_type = 'contact.updated' AND entity_id = $id AND actor = 'agent:hermes';
            """;
        cmd.Parameters.AddWithValue("$id", personId);
        Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    [Fact]
    public void DualProject_DoesNotDuplicatePerson()
    {
        var projectA = SeedProject("Harbor Court");
        var projectB = SeedProject("Riverview");
        var emailId = Guid.NewGuid().ToString("D");
        SeedEmail(emailId);

        var result = _enricher.Enrich(
            emailId,
            [
                new ParsedEmailParticipant
                {
                    Role = "from",
                    Address = "alex.rivera@metrofiber.example",
                    DisplayName = "Alex Rivera",
                },
            ],
            bodyText: null,
            projectIds: [projectA, projectB]);

        var personId = Assert.Single(result.PersonIds);
        var again = _enricher.Enrich(
            emailId,
            [
                new ParsedEmailParticipant
                {
                    Role = "from",
                    Address = "alex.rivera@metrofiber.example",
                    DisplayName = "Alex Rivera",
                },
            ],
            bodyText: null,
            projectIds: [projectA, projectB]);

        Assert.Equal(personId, Assert.Single(again.PersonIds));
        var detail = _contacts.GetPerson(personId);
        Assert.NotNull(detail);
        Assert.Equal(2, detail!.Projects.Count);
        Assert.Contains(detail.Projects, p => p.Id == projectA);
        Assert.Contains(detail.Projects, p => p.Id == projectB);
        Assert.Equal(1, _contacts.ListPeople().Count(p =>
            string.Equals(p.PrimaryEmail, "alex.rivera@metrofiber.example", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void SignatureHeuristic_ParsesTitleAndMobile()
    {
        var facts = SignatureHeuristic.Parse(
            """
            See you Thursday.

            --
            Pat Nguyen
            Senior Project Engineer
            Cell: (628) 555-0142
            """);

        Assert.Equal("Senior Project Engineer", facts.Title);
        Assert.Equal("6285550142", ContactResolution.NormalizePhone(facts.MobilePhone));
    }

    [Fact]
    public void SignatureHeuristic_ParsesBarePhoneLine()
    {
        var facts = SignatureHeuristic.Parse(
            """
            Thanks,

            --
            Alex Rivera
            Account Manager
            (415) 555-0198
            MetroFiber Business
            """);

        Assert.Equal("Account Manager", facts.Title);
        Assert.NotNull(facts.MobilePhone);
        Assert.Equal("4155550198", ContactResolution.NormalizePhone(facts.MobilePhone));
    }

    [Fact]
    public void CategoryAndDisposition_ListAndExcludeOnReingest()
    {
        var personId = _contacts.UpsertPersonByEmail(
            "resident@stanford.edu",
            "Housing Resident",
            sourceEmailId: null,
            ContactSourceKinds.EmailParticipant);

        Assert.Null(_contacts.GetPerson(personId)!.Category);
        Assert.Equal(ContactDispositions.Active, _contacts.GetPerson(personId)!.Disposition);

        _contacts.UpdateContact(
            personId,
            new ContactPatch { Category = ContactCategories.Client },
            provenance: "test classify",
            requestedBy: "test");
        Assert.Contains(_contacts.ListPeople(category: ContactCategories.Client), p => p.Id == personId);
        Assert.DoesNotContain(_contacts.ListPeople(category: "pending"), p => p.Id == personId);

        _contacts.UpdateContact(
            personId,
            new ContactPatch { Disposition = ContactDispositions.FlaggedResident, Category = string.Empty },
            provenance: "flag",
            requestedBy: "test");
        Assert.Contains(
            _contacts.ListPeople(disposition: ContactDispositions.FlaggedResident),
            p => p.Id == personId);
        Assert.DoesNotContain(_contacts.ListPeople(category: ContactCategories.Client), p => p.Id == personId);

        var archived = _contacts.ArchivePerson(personId, excludeAsResident: true, "exclude", "test");
        Assert.NotNull(archived);
        Assert.Equal(ContactDispositions.ExcludedResident, archived!.Disposition);
        Assert.True(_contacts.IsExcludedFromTracking(personId));

        var again = _contacts.UpsertPersonByEmail(
            "resident@stanford.edu",
            "Housing Resident",
            sourceEmailId: null,
            ContactSourceKinds.EmailParticipant);
        Assert.Equal(personId, again);
        Assert.True(_contacts.IsExcludedFromTracking(again));
        Assert.Empty(_contacts.ListPeople());
    }

    private void SeedEmail(string emailId)
    {
        var now = DateTime.UtcNow.ToString("O");
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO email_artifacts (id, subject, body_preview, created_at, updated_at)
            VALUES ($id, 'Test', 'preview', $t, $t);
            """;
        cmd.Parameters.AddWithValue("$id", emailId);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.ExecuteNonQuery();
    }

    private string SeedProject(string name)
    {
        var id = Guid.NewGuid().ToString("D");
        var now = DateTime.UtcNow.ToString("O");
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "INSERT INTO projects (id, name, status, created_at, updated_at) VALUES ($id, $name, 'active', $t, $t);";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.ExecuteNonQuery();
        return id;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // best-effort
        }
    }
}
