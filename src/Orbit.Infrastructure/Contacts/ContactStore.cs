using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orbit.Core.Data;
using Orbit.Infrastructure.Data;

namespace Orbit.Infrastructure.Contacts;

public sealed class ContactStore
{
    private readonly SqliteConnectionFactory _factory;

    public ContactStore(SqliteConnectionFactory factory) => _factory = factory;

    public IReadOnlyList<ContactListItem> ListPeople(
        string? category = null,
        string? disposition = null,
        bool includeExcluded = false)
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        var where = new List<string> { "p.archived_at IS NULL" };
        if (!includeExcluded)
        {
            where.Add("COALESCE(p.disposition, 'active') != 'excluded_resident'");
        }

        if (!string.IsNullOrWhiteSpace(disposition))
        {
            where.Add("COALESCE(p.disposition, 'active') = $disposition");
            cmd.Parameters.AddWithValue("$disposition", disposition.Trim());
        }
        else
        {
            // Category browse / default list: hide resident review queue unless disposition=flagged_resident.
            where.Add("COALESCE(p.disposition, 'active') != 'flagged_resident'");

            if (string.Equals(category, "pending", StringComparison.OrdinalIgnoreCase))
            {
                where.Add("p.category IS NULL");
            }
            else if (!string.IsNullOrWhiteSpace(category))
            {
                where.Add("p.category = $category");
                cmd.Parameters.AddWithValue("$category", category.Trim());
            }
        }

        cmd.CommandText =
            $"""
            SELECT p.id, p.display_name,
                   (SELECT m.title FROM organization_memberships m
                    WHERE m.person_id = p.id AND m.archived_at IS NULL
                    ORDER BY m.updated_at DESC LIMIT 1) AS title,
                   (SELECT o.name FROM organization_memberships m
                    INNER JOIN organizations o ON o.id = m.organization_id
                    WHERE m.person_id = p.id AND m.archived_at IS NULL AND o.archived_at IS NULL
                    ORDER BY m.updated_at DESC LIMIT 1) AS org_name,
                   (SELECT cm.value FROM contact_methods cm
                    WHERE cm.person_id = p.id AND cm.method_type = 'email' AND cm.archived_at IS NULL
                    ORDER BY cm.is_primary DESC, cm.updated_at DESC LIMIT 1) AS email,
                   (SELECT cm.value FROM contact_methods cm
                    WHERE cm.person_id = p.id AND cm.method_type IN ('mobile', 'phone') AND cm.archived_at IS NULL
                    ORDER BY CASE cm.method_type WHEN 'mobile' THEN 0 ELSE 1 END, cm.is_primary DESC
                    LIMIT 1) AS phone,
                   p.category,
                   COALESCE(p.disposition, 'active') AS disposition
            FROM people p
            WHERE {string.Join(" AND ", where)}
            ORDER BY p.display_name COLLATE NOCASE;
            """;
        var list = new List<ContactListItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ContactListItem
            {
                Id = reader.GetString(0),
                DisplayName = reader.GetString(1),
                Title = reader.IsDBNull(2) ? null : reader.GetString(2),
                OrganizationName = reader.IsDBNull(3) ? null : reader.GetString(3),
                PrimaryEmail = reader.IsDBNull(4) ? null : reader.GetString(4),
                PrimaryPhone = reader.IsDBNull(5) ? null : reader.GetString(5),
                Category = reader.IsDBNull(6) ? null : reader.GetString(6),
                Disposition = reader.GetString(7),
            });
        }

        return list;
    }

    public ContactDetail? GetPerson(string personId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personId);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, display_name, given_name, family_name, notes, category,
                   COALESCE(disposition, 'active')
            FROM people
            WHERE id = $id AND archived_at IS NULL
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", personId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var detail = new ContactDetail
        {
            Id = reader.GetString(0),
            DisplayName = reader.GetString(1),
            GivenName = reader.IsDBNull(2) ? null : reader.GetString(2),
            FamilyName = reader.IsDBNull(3) ? null : reader.GetString(3),
            Notes = reader.IsDBNull(4) ? null : reader.GetString(4),
            Category = reader.IsDBNull(5) ? null : reader.GetString(5),
            Disposition = reader.GetString(6),
        };
        reader.Close();

        string? title = null;
        string? orgId = null;
        string? orgName = null;
        using (var mem = connection.CreateCommand())
        {
            mem.CommandText =
                """
                SELECT m.title, o.id, o.name
                FROM organization_memberships m
                INNER JOIN organizations o ON o.id = m.organization_id
                WHERE m.person_id = $id AND m.archived_at IS NULL AND o.archived_at IS NULL
                ORDER BY m.updated_at DESC
                LIMIT 1;
                """;
            mem.Parameters.AddWithValue("$id", personId);
            using var memReader = mem.ExecuteReader();
            if (memReader.Read())
            {
                title = memReader.IsDBNull(0) ? null : memReader.GetString(0);
                orgId = memReader.GetString(1);
                orgName = memReader.GetString(2);
            }
        }

        string? reportsToId = null;
        string? reportsToName = null;
        using (var rep = connection.CreateCommand())
        {
            rep.CommandText =
                """
                SELECT r.reports_to_person_id, p.display_name
                FROM reporting_relationships r
                INNER JOIN people p ON p.id = r.reports_to_person_id
                WHERE r.person_id = $id AND r.archived_at IS NULL AND p.archived_at IS NULL
                ORDER BY r.updated_at DESC
                LIMIT 1;
                """;
            rep.Parameters.AddWithValue("$id", personId);
            using var repReader = rep.ExecuteReader();
            if (repReader.Read())
            {
                reportsToId = repReader.GetString(0);
                reportsToName = repReader.IsDBNull(1) ? null : repReader.GetString(1);
            }
        }

        return detail with
        {
            Title = title,
            OrganizationId = orgId,
            OrganizationName = orgName,
            ReportsToPersonId = reportsToId,
            ReportsToDisplayName = reportsToName,
            Methods = ListMethods(connection, personId),
            Projects = ListProjects(connection, personId),
            RecentEmails = ListRecentEmails(connection, personId),
            Provenance = ListProvenance(connection, EntityTypes.Person, personId),
        };
    }

    public IReadOnlyList<OrganizationListItem> ListOrganizations()
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT o.id, o.name, o.kind,
                   (SELECT cm.value FROM contact_methods cm
                    WHERE cm.organization_id = o.id AND cm.method_type = 'domain' AND cm.archived_at IS NULL
                    LIMIT 1) AS domain
            FROM organizations o
            WHERE o.archived_at IS NULL
            ORDER BY o.name COLLATE NOCASE;
            """;
        var list = new List<OrganizationListItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new OrganizationListItem
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Kind = reader.IsDBNull(2) ? null : reader.GetString(2),
                Domain = reader.IsDBNull(3) ? null : reader.GetString(3),
            });
        }

        return list;
    }

    public string? FindPersonIdByEmail(string email)
    {
        var normalized = ContactResolution.NormalizeEmail(email);
        if (normalized is null)
        {
            return null;
        }

        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT cm.person_id FROM contact_methods cm
            INNER JOIN people p ON p.id = cm.person_id
            WHERE cm.method_type = 'email'
              AND lower(cm.value) = $email
              AND cm.person_id IS NOT NULL
              AND cm.archived_at IS NULL
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$email", normalized);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Returns disposition for a person id, or null if missing.</summary>
    public string? GetDisposition(string personId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personId);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT COALESCE(disposition, 'active') FROM people WHERE id = $id LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", personId);
        return cmd.ExecuteScalar() as string;
    }

    public bool IsExcludedFromTracking(string personId)
    {
        var disposition = GetDisposition(personId);
        if (string.Equals(disposition, ContactDispositions.ExcludedResident, StringComparison.Ordinal))
        {
            return true;
        }

        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT archived_at FROM people WHERE id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", personId);
        var archived = cmd.ExecuteScalar();
        return archived is not null and not DBNull;
    }

    public string? FindPersonIdByNormalizedPhone(string? phone)
    {
        var digits = ContactResolution.NormalizePhone(phone);
        if (digits is null)
        {
            return null;
        }

        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, person_id, value FROM contact_methods
            WHERE method_type IN ('phone', 'mobile')
              AND person_id IS NOT NULL
              AND archived_at IS NULL;
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var value = reader.GetString(2);
            if (string.Equals(ContactResolution.NormalizePhone(value), digits, StringComparison.Ordinal))
            {
                return reader.GetString(1);
            }
        }

        return null;
    }

    public IReadOnlyList<(string PersonId, string DisplayName, string Email)> FindPeopleByDisplayName(string displayName)
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT p.id, p.display_name,
                   (SELECT cm.value FROM contact_methods cm
                    WHERE cm.person_id = p.id AND cm.method_type = 'email' AND cm.archived_at IS NULL
                    LIMIT 1) AS email
            FROM people p
            WHERE p.archived_at IS NULL
              AND lower(p.display_name) = lower($name);
            """;
        cmd.Parameters.AddWithValue("$name", displayName.Trim());
        var list = new List<(string, string, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2)));
        }

        return list;
    }

    public string? FindOrganizationIdByDomain(string domain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT organization_id FROM contact_methods
            WHERE method_type = 'domain'
              AND lower(value) = lower($domain)
              AND organization_id IS NOT NULL
              AND archived_at IS NULL
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$domain", domain.Trim());
        var byMethod = cmd.ExecuteScalar() as string;
        if (byMethod is not null)
        {
            return byMethod;
        }

        // Fallback: org name equals domain root (demo seed "MetroFiber" vs metrofiber.example).
        var rootName = ContactResolution.OrganizationNameFromDomain(domain);
        using var byName = connection.CreateCommand();
        byName.CommandText =
            """
            SELECT id FROM organizations
            WHERE archived_at IS NULL
              AND (lower(name) = lower($domain) OR lower(name) = lower($root))
            LIMIT 1;
            """;
        byName.Parameters.AddWithValue("$domain", domain.Trim());
        byName.Parameters.AddWithValue("$root", rootName);
        return byName.ExecuteScalar() as string;
    }

    public string UpsertPersonByEmail(
        string email,
        string? displayName,
        string? sourceEmailId,
        string sourceKind)
    {
        var normalized = ContactResolution.NormalizeEmail(email)
            ?? throw new ArgumentException("A valid email address is required.", nameof(email));

        var existing = FindPersonIdByEmail(normalized);
        if (existing is not null && IsExcludedFromTracking(existing))
        {
            // Residents / archived contacts stay out of the tracked graph — do not revive.
            return existing;
        }

        var now = DateTime.UtcNow.ToString("O");
        var name = ContactResolution.DisplayNameFromParticipant(displayName, normalized);
        var (given, family) = ContactResolution.SplitName(name);

        using var connection = _factory.CreateConnection();
        using var tx = connection.BeginTransaction();

        string personId;
        if (existing is not null)
        {
            personId = existing;
            using var update = connection.CreateCommand();
            update.Transaction = tx;
            update.CommandText =
                """
                UPDATE people
                SET display_name = CASE
                      WHEN display_name IS NULL OR display_name = '' OR display_name = 'Unknown' THEN $name
                      ELSE display_name END,
                    given_name = COALESCE(given_name, $given),
                    family_name = COALESCE(family_name, $family),
                    updated_at = $t
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$id", personId);
            update.Parameters.AddWithValue("$name", name);
            update.Parameters.AddWithValue("$given", (object?)given ?? DBNull.Value);
            update.Parameters.AddWithValue("$family", (object?)family ?? DBNull.Value);
            update.Parameters.AddWithValue("$t", now);
            update.ExecuteNonQuery();
        }
        else
        {
            personId = Guid.NewGuid().ToString("D");
            using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText =
                """
                INSERT INTO people (id, display_name, given_name, family_name, category, disposition, created_at, updated_at)
                VALUES ($id, $name, $given, $family, NULL, 'active', $t, $t);
                """;
            insert.Parameters.AddWithValue("$id", personId);
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$given", (object?)given ?? DBNull.Value);
            insert.Parameters.AddWithValue("$family", (object?)family ?? DBNull.Value);
            insert.Parameters.AddWithValue("$t", now);
            insert.ExecuteNonQuery();
        }

        EnsureContactMethod(connection, tx, personId, organizationId: null, ContactMethodTypes.Email, normalized, isPrimary: true, now);
        WriteProvenance(connection, tx, EntityTypes.Person, personId, "email", normalized, sourceEmailId, sourceKind, now);
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            WriteProvenance(connection, tx, EntityTypes.Person, personId, "display_name", name, sourceEmailId, sourceKind, now);
        }

        tx.Commit();
        UpsertPersonSearchDocument(personId, name, now);
        return personId;
    }

    public string EnsureOrganizationForDomain(string domain, string? sourceEmailId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        if (ContactResolution.IsFreeMailDomain(domain))
        {
            throw new ArgumentException("Free-mail domains are not organizations.", nameof(domain));
        }

        var existing = FindOrganizationIdByDomain(domain);
        var now = DateTime.UtcNow.ToString("O");
        using var connection = _factory.CreateConnection();
        using var tx = connection.BeginTransaction();

        string orgId;
        if (existing is not null)
        {
            orgId = existing;
        }
        else
        {
            orgId = Guid.NewGuid().ToString("D");
            var name = ContactResolution.OrganizationNameFromDomain(domain);
            using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText =
                """
                INSERT INTO organizations (id, name, kind, created_at, updated_at)
                VALUES ($id, $name, 'company', $t, $t);
                """;
            insert.Parameters.AddWithValue("$id", orgId);
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$t", now);
            insert.ExecuteNonQuery();
        }

        EnsureContactMethod(connection, tx, personId: null, orgId, ContactMethodTypes.Domain, domain.ToLowerInvariant(), isPrimary: true, now);
        WriteProvenance(connection, tx, EntityTypes.Organization, orgId, "domain", domain.ToLowerInvariant(), sourceEmailId, ContactSourceKinds.DomainInference, now);
        tx.Commit();
        return orgId;
    }

    private static string EnsureOrganizationByName(
        SqliteConnection connection,
        SqliteTransaction tx,
        string name,
        string now)
    {
        using (var find = connection.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText =
                """
                SELECT id FROM organizations
                WHERE archived_at IS NULL AND LOWER(name) = LOWER($name)
                LIMIT 1;
                """;
            find.Parameters.AddWithValue("$name", name);
            var existing = find.ExecuteScalar() as string;
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }
        }

        var orgId = Guid.NewGuid().ToString("D");
        using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText =
            """
            INSERT INTO organizations (id, name, kind, created_at, updated_at)
            VALUES ($id, $name, 'company', $t, $t);
            """;
        insert.Parameters.AddWithValue("$id", orgId);
        insert.Parameters.AddWithValue("$name", name);
        insert.Parameters.AddWithValue("$t", now);
        insert.ExecuteNonQuery();
        return orgId;
    }

    public void EnsureMembership(string personId, string organizationId, string? title, string? sourceEmailId, string sourceKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personId);
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        var now = DateTime.UtcNow.ToString("O");

        using var connection = _factory.CreateConnection();
        using var tx = connection.BeginTransaction();
        using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = tx;
            upsert.CommandText =
                """
                INSERT INTO organization_memberships (id, person_id, organization_id, title, created_at, updated_at)
                VALUES ($id, $person, $org, $title, $t, $t)
                ON CONFLICT(person_id, organization_id) DO UPDATE SET
                  title = COALESCE(excluded.title, organization_memberships.title),
                  updated_at = excluded.updated_at,
                  archived_at = NULL;
                """;
            upsert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            upsert.Parameters.AddWithValue("$person", personId);
            upsert.Parameters.AddWithValue("$org", organizationId);
            upsert.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$t", now);
            upsert.ExecuteNonQuery();
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            WriteProvenance(connection, tx, EntityTypes.Person, personId, "title", title, sourceEmailId, sourceKind, now);
        }

        tx.Commit();
    }

    /// <summary>Upserts a soft reporting edge (org chart). Idempotent per person→manager pair.</summary>
    public string EnsureReportingRelationship(
        string personId,
        string reportsToPersonId,
        string? organizationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportsToPersonId);
        if (string.Equals(personId, reportsToPersonId, StringComparison.Ordinal))
        {
            throw new ArgumentException("A person cannot report to themselves.");
        }

        var now = DateTime.UtcNow.ToString("O");
        using var connection = _factory.CreateConnection();
        using var tx = connection.BeginTransaction();

        using (var find = connection.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText =
                """
                SELECT id FROM reporting_relationships
                WHERE person_id = $person
                  AND reports_to_person_id = $manager
                  AND archived_at IS NULL
                LIMIT 1;
                """;
            find.Parameters.AddWithValue("$person", personId);
            find.Parameters.AddWithValue("$manager", reportsToPersonId);
            var existing = find.ExecuteScalar() as string;
            if (!string.IsNullOrWhiteSpace(existing))
            {
                tx.Commit();
                return existing;
            }
        }

        var id = Guid.NewGuid().ToString("D");
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText =
                """
                INSERT INTO reporting_relationships (
                  id, person_id, reports_to_person_id, organization_id, created_at, updated_at)
                VALUES ($id, $person, $manager, $org, $t, $t);
                """;
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$person", personId);
            insert.Parameters.AddWithValue("$manager", reportsToPersonId);
            insert.Parameters.AddWithValue("$org", (object?)organizationId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$t", now);
            insert.ExecuteNonQuery();
        }

        tx.Commit();
        return id;
    }

    public void EnsurePhoneMethod(
        string personId,
        string methodType,
        string value,
        string? sourceEmailId,
        string sourceKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personId);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var now = DateTime.UtcNow.ToString("O");
        using var connection = _factory.CreateConnection();
        using var tx = connection.BeginTransaction();

        // Skip if same normalized digits already present.
        var digits = ContactResolution.NormalizePhone(value);
        if (digits is not null)
        {
            using var existing = connection.CreateCommand();
            existing.Transaction = tx;
            existing.CommandText =
                """
                SELECT value FROM contact_methods
                WHERE person_id = $person
                  AND method_type IN ('phone', 'mobile')
                  AND archived_at IS NULL;
                """;
            existing.Parameters.AddWithValue("$person", personId);
            using var reader = existing.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(ContactResolution.NormalizePhone(reader.GetString(0)), digits, StringComparison.Ordinal))
                {
                    reader.Close();
                    WriteProvenance(connection, tx, EntityTypes.Person, personId, methodType, value, sourceEmailId, sourceKind, now);
                    tx.Commit();
                    return;
                }
            }
        }

        EnsureContactMethod(connection, tx, personId, null, methodType, value, isPrimary: methodType == ContactMethodTypes.Mobile, now);
        WriteProvenance(connection, tx, EntityTypes.Person, personId, methodType, value, sourceEmailId, sourceKind, now);
        tx.Commit();
    }

    public void LinkPersonToProjects(string personId, IReadOnlyList<string> projectIds)
    {
        if (projectIds.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow.ToString("O");
        using var connection = _factory.CreateConnection();
        foreach (var projectId in projectIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
        {
            using var check = connection.CreateCommand();
            check.CommandText = "SELECT 1 FROM projects WHERE id = $id AND archived_at IS NULL LIMIT 1;";
            check.Parameters.AddWithValue("$id", projectId);
            if (check.ExecuteScalar() is null)
            {
                continue;
            }

            using var exists = connection.CreateCommand();
            exists.CommandText =
                """
                SELECT 1 FROM relationships
                WHERE source_type = $st AND source_id = $sid
                  AND target_type = $tt AND target_id = $tid
                  AND relationship_type = $rt
                  AND archived_at IS NULL
                LIMIT 1;
                """;
            exists.Parameters.AddWithValue("$st", EntityTypes.Person);
            exists.Parameters.AddWithValue("$sid", personId);
            exists.Parameters.AddWithValue("$tt", EntityTypes.Project);
            exists.Parameters.AddWithValue("$tid", projectId);
            exists.Parameters.AddWithValue("$rt", RelationshipTypes.InvolvedIn);
            if (exists.ExecuteScalar() is not null)
            {
                continue;
            }

            using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO relationships (
                  id, source_type, source_id, target_type, target_id, relationship_type,
                  project_id, confidence, confirmed_by_user, created_by, created_at, updated_at)
                VALUES (
                  $id, $st, $sid, $tt, $tid, $rt,
                  $project, 0.7, 0, $by, $t, $t);
                """;
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            insert.Parameters.AddWithValue("$st", EntityTypes.Person);
            insert.Parameters.AddWithValue("$sid", personId);
            insert.Parameters.AddWithValue("$tt", EntityTypes.Project);
            insert.Parameters.AddWithValue("$tid", projectId);
            insert.Parameters.AddWithValue("$rt", RelationshipTypes.InvolvedIn);
            insert.Parameters.AddWithValue("$project", projectId);
            insert.Parameters.AddWithValue("$by", CreatedByActors.System);
            insert.Parameters.AddWithValue("$t", now);
            insert.ExecuteNonQuery();
        }
    }

    public void SetEmailParticipantPerson(string emailId, string address, string personId)
    {
        var normalized = ContactResolution.NormalizeEmail(address);
        if (normalized is null)
        {
            return;
        }

        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            UPDATE email_participants
            SET person_id = $person
            WHERE email_artifact_id = $email
              AND lower(address) = $addr;
            """;
        cmd.Parameters.AddWithValue("$person", personId);
        cmd.Parameters.AddWithValue("$email", emailId);
        cmd.Parameters.AddWithValue("$addr", normalized);
        cmd.ExecuteNonQuery();
    }

    public void CreateMergeSuggestion(
        string candidatePersonId,
        string existingPersonId,
        string reason,
        string? sourceEmailId)
    {
        var now = DateTime.UtcNow.ToString("O");
        var payload = JsonSerializer.Serialize(new
        {
            candidatePersonId,
            existingPersonId,
            reason,
            sourceEmailId,
        });

        using var connection = _factory.CreateConnection();

        // Dedupe pending suggestions for the same pair.
        using (var check = connection.CreateCommand())
        {
            check.CommandText =
                """
                SELECT 1 FROM agent_suggestions
                WHERE suggestion_type = $type
                  AND status = 'pending'
                  AND archived_at IS NULL
                  AND payload_json LIKE $a
                  AND payload_json LIKE $b
                LIMIT 1;
                """;
            check.Parameters.AddWithValue("$type", ContactSuggestionTypes.ContactMerge);
            check.Parameters.AddWithValue("$a", "%" + candidatePersonId + "%");
            check.Parameters.AddWithValue("$b", "%" + existingPersonId + "%");
            if (check.ExecuteScalar() is not null)
            {
                return;
            }
        }

        var groupKey = string.CompareOrdinal(candidatePersonId, existingPersonId) <= 0
            ? $"{candidatePersonId}|{existingPersonId}"
            : $"{existingPersonId}|{candidatePersonId}";

        using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO agent_suggestions (
              id, suggestion_type, summary, payload_json, group_key, status, confidence, created_at, updated_at)
            VALUES ($id, $type, $summary, $payload, $group, 'pending', 0.55, $t, $t);
            """;
        insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        insert.Parameters.AddWithValue("$type", ContactSuggestionTypes.ContactMerge);
        insert.Parameters.AddWithValue("$summary", $"Possible contact merge: {reason}");
        insert.Parameters.AddWithValue("$payload", payload);
        insert.Parameters.AddWithValue("$group", groupKey);
        insert.Parameters.AddWithValue("$t", now);
        insert.ExecuteNonQuery();
    }

    public ContactDetail UpdateContact(
        string contactId,
        ContactPatch patch,
        string? provenance,
        string? requestedBy,
        MutationProvenance? requestProvenance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contactId);
        ArgumentNullException.ThrowIfNull(patch);

        if (GetPerson(contactId) is null)
        {
            throw new ArgumentException("Contact was not found.", nameof(contactId));
        }

        var actorSeed = requestProvenance?.ResolveActor(requestedBy) ?? requestedBy;
        var actor = string.IsNullOrWhiteSpace(actorSeed) ? CreatedByActors.User : actorSeed.Trim();
        var sourceKind = ContactSourceKinds.UserUpdate;
        var sourceNote = string.IsNullOrWhiteSpace(provenance) ? "UpdateContact" : provenance.Trim();
        var now = DateTime.UtcNow.ToString("O");
        var applied = new Dictionary<string, string>(StringComparer.Ordinal);

        using var connection = _factory.CreateConnection();
        using var tx = connection.BeginTransaction();

        if (!string.IsNullOrWhiteSpace(patch.DisplayName))
        {
            var name = patch.DisplayName.Trim();
            var (given, family) = ContactResolution.SplitName(name);
            using var update = connection.CreateCommand();
            update.Transaction = tx;
            update.CommandText =
                """
                UPDATE people
                SET display_name = $name, given_name = $given, family_name = $family, updated_at = $t
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$id", contactId);
            update.Parameters.AddWithValue("$name", name);
            update.Parameters.AddWithValue("$given", (object?)given ?? DBNull.Value);
            update.Parameters.AddWithValue("$family", (object?)family ?? DBNull.Value);
            update.Parameters.AddWithValue("$t", now);
            update.ExecuteNonQuery();
            WriteProvenance(connection, tx, EntityTypes.Person, contactId, "display_name", name, null, sourceKind, now);
            applied["displayName"] = name;
        }

        if (!string.IsNullOrWhiteSpace(patch.Email))
        {
            var email = ContactResolution.NormalizeEmail(patch.Email)
                ?? throw new ArgumentException("patch.email is not a valid email.", nameof(patch));
            EnsureContactMethod(connection, tx, contactId, null, ContactMethodTypes.Email, email, isPrimary: true, now);
            WriteProvenance(connection, tx, EntityTypes.Person, contactId, "email", email, null, sourceKind, now);
            applied["email"] = email;
        }

        if (!string.IsNullOrWhiteSpace(patch.Mobile))
        {
            EnsureContactMethod(connection, tx, contactId, null, ContactMethodTypes.Mobile, patch.Mobile.Trim(), isPrimary: true, now);
            WriteProvenance(connection, tx, EntityTypes.Person, contactId, "mobile", patch.Mobile.Trim(), null, sourceKind, now);
            applied["mobile"] = patch.Mobile.Trim();
        }

        if (!string.IsNullOrWhiteSpace(patch.Phone))
        {
            EnsureContactMethod(connection, tx, contactId, null, ContactMethodTypes.Phone, patch.Phone.Trim(), isPrimary: false, now);
            WriteProvenance(connection, tx, EntityTypes.Person, contactId, "phone", patch.Phone.Trim(), null, sourceKind, now);
            applied["phone"] = patch.Phone.Trim();
        }

        if (!string.IsNullOrWhiteSpace(patch.Title)
            || !string.IsNullOrWhiteSpace(patch.OrganizationId)
            || !string.IsNullOrWhiteSpace(patch.OrganizationName))
        {
            var orgId = patch.OrganizationId;
            if (string.IsNullOrWhiteSpace(orgId) && !string.IsNullOrWhiteSpace(patch.OrganizationName))
            {
                orgId = EnsureOrganizationByName(connection, tx, patch.OrganizationName.Trim(), now);
                applied["organizationName"] = patch.OrganizationName.Trim();
            }

            if (string.IsNullOrWhiteSpace(orgId))
            {
                using var existingOrg = connection.CreateCommand();
                existingOrg.Transaction = tx;
                existingOrg.CommandText =
                    """
                    SELECT organization_id FROM organization_memberships
                    WHERE person_id = $id AND archived_at IS NULL
                    ORDER BY updated_at DESC LIMIT 1;
                    """;
                existingOrg.Parameters.AddWithValue("$id", contactId);
                orgId = existingOrg.ExecuteScalar() as string;
            }

            if (!string.IsNullOrWhiteSpace(orgId))
            {
                using var upsert = connection.CreateCommand();
                upsert.Transaction = tx;
                upsert.CommandText =
                    """
                    INSERT INTO organization_memberships (id, person_id, organization_id, title, created_at, updated_at)
                    VALUES ($id, $person, $org, $title, $t, $t)
                    ON CONFLICT(person_id, organization_id) DO UPDATE SET
                      title = COALESCE(excluded.title, organization_memberships.title),
                      updated_at = excluded.updated_at,
                      archived_at = NULL;
                    """;
                upsert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
                upsert.Parameters.AddWithValue("$person", contactId);
                upsert.Parameters.AddWithValue("$org", orgId);
                upsert.Parameters.AddWithValue("$title", (object?)patch.Title?.Trim() ?? DBNull.Value);
                upsert.Parameters.AddWithValue("$t", now);
                upsert.ExecuteNonQuery();
                if (!string.IsNullOrWhiteSpace(patch.Title))
                {
                    WriteProvenance(connection, tx, EntityTypes.Person, contactId, "title", patch.Title.Trim(), null, sourceKind, now);
                    applied["title"] = patch.Title.Trim();
                }

                applied["organizationId"] = orgId;
            }
            else if (!string.IsNullOrWhiteSpace(patch.Title))
            {
                throw new ArgumentException("title requires an organizationId or organizationName when the contact has no membership.", nameof(patch));
            }
        }

        if (patch.Category is not null)
        {
            var cat = string.IsNullOrWhiteSpace(patch.Category) ? null : patch.Category.Trim();
            if (cat is not null && !ContactCategories.IsValid(cat))
            {
                throw new ArgumentException("patch.category must be company, client, vendor, or empty.", nameof(patch));
            }

            using var updateCat = connection.CreateCommand();
            updateCat.Transaction = tx;
            updateCat.CommandText =
                """
                UPDATE people SET category = $cat, updated_at = $t WHERE id = $id;
                """;
            updateCat.Parameters.AddWithValue("$id", contactId);
            updateCat.Parameters.AddWithValue("$cat", (object?)cat ?? DBNull.Value);
            updateCat.Parameters.AddWithValue("$t", now);
            updateCat.ExecuteNonQuery();
            WriteProvenance(
                connection,
                tx,
                EntityTypes.Person,
                contactId,
                "category",
                cat ?? "pending",
                null,
                sourceKind,
                now);
            applied["category"] = cat ?? "pending";
        }

        if (!string.IsNullOrWhiteSpace(patch.Disposition))
        {
            var disp = patch.Disposition.Trim();
            if (!ContactDispositions.IsValid(disp))
            {
                throw new ArgumentException(
                    "patch.disposition must be active, flagged_resident, or excluded_resident.",
                    nameof(patch));
            }

            using var updateDisp = connection.CreateCommand();
            updateDisp.Transaction = tx;
            updateDisp.CommandText =
                """
                UPDATE people SET disposition = $disp, updated_at = $t WHERE id = $id;
                """;
            updateDisp.Parameters.AddWithValue("$id", contactId);
            updateDisp.Parameters.AddWithValue("$disp", disp);
            updateDisp.Parameters.AddWithValue("$t", now);
            updateDisp.ExecuteNonQuery();
            WriteProvenance(connection, tx, EntityTypes.Person, contactId, "disposition", disp, null, sourceKind, now);
            applied["disposition"] = disp;
        }

        if (!string.IsNullOrWhiteSpace(patch.ReportsToPersonId))
        {
            var managerId = patch.ReportsToPersonId.Trim();
            if (string.Equals(managerId, contactId, StringComparison.Ordinal))
            {
                throw new ArgumentException("A person cannot report to themselves.", nameof(patch));
            }

            using var findMgr = connection.CreateCommand();
            findMgr.Transaction = tx;
            findMgr.CommandText = "SELECT 1 FROM people WHERE id = $id AND archived_at IS NULL LIMIT 1;";
            findMgr.Parameters.AddWithValue("$id", managerId);
            if (findMgr.ExecuteScalar() is null)
            {
                throw new ArgumentException("reportsToPersonId was not found.", nameof(patch));
            }

            using var upsertRep = connection.CreateCommand();
            upsertRep.Transaction = tx;
            upsertRep.CommandText =
                """
                INSERT INTO reporting_relationships (id, person_id, reports_to_person_id, organization_id, created_at, updated_at)
                VALUES ($id, $person, $manager, $org, $t, $t);
                """;
            upsertRep.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            upsertRep.Parameters.AddWithValue("$person", contactId);
            upsertRep.Parameters.AddWithValue("$manager", managerId);
            upsertRep.Parameters.AddWithValue("$org", (object?)patch.OrganizationId ?? DBNull.Value);
            upsertRep.Parameters.AddWithValue("$t", now);
            upsertRep.ExecuteNonQuery();
            WriteProvenance(connection, tx, EntityTypes.Person, contactId, "reports_to", managerId, null, sourceKind, now);
            applied["reportsToPersonId"] = managerId;
        }

        if (applied.Count == 0)
        {
            throw new ArgumentException("patch contained no supported fields.", nameof(patch));
        }

        using (var audit = connection.CreateCommand())
        {
            audit.Transaction = tx;
            audit.CommandText =
                """
                INSERT INTO audit_events (id, event_type, entity_type, entity_id, actor, detail_json, created_at)
                VALUES ($id, 'contact.updated', $etype, $eid, $actor, $detail, $t);
                """;
            audit.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            audit.Parameters.AddWithValue("$etype", EntityTypes.Person);
            audit.Parameters.AddWithValue("$eid", contactId);
            audit.Parameters.AddWithValue("$actor", actor);
            audit.Parameters.AddWithValue(
                "$detail",
                AuditDetailJson.Serialize(new { provenance = sourceNote, patch = applied }, requestProvenance));
            audit.Parameters.AddWithValue("$t", now);
            audit.ExecuteNonQuery();
        }

        tx.Commit();

        if (applied.TryGetValue("displayName", out var dn))
        {
            UpsertPersonSearchDocument(contactId, dn, now);
        }

        return GetPerson(contactId)
            ?? throw new InvalidOperationException("Contact was not readable after update.");
    }

    /// <summary>
    /// Soft-archives a person. When <paramref name="excludeAsResident"/> is true, also sets
    /// disposition to excluded_resident so re-ingest will not revive them as tracked contacts.
    /// </summary>
    public ContactDetail? ArchivePerson(
        string contactId,
        bool excludeAsResident = false,
        string? provenance = null,
        string? requestedBy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contactId);
        var existing = GetPerson(contactId);
        if (existing is null)
        {
            // May already be archived — still allow excluding by id lookup.
            using var probe = _factory.CreateConnection();
            using var probeCmd = probe.CreateCommand();
            probeCmd.CommandText = "SELECT 1 FROM people WHERE id = $id LIMIT 1;";
            probeCmd.Parameters.AddWithValue("$id", contactId);
            if (probeCmd.ExecuteScalar() is null)
            {
                return null;
            }
        }

        var now = DateTime.UtcNow.ToString("O");
        var actor = string.IsNullOrWhiteSpace(requestedBy) ? CreatedByActors.User : requestedBy.Trim();
        var sourceNote = string.IsNullOrWhiteSpace(provenance) ? "ArchivePerson" : provenance.Trim();

        using var connection = _factory.CreateConnection();
        using var tx = connection.BeginTransaction();
        using (var update = connection.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText =
                """
                UPDATE people
                SET archived_at = COALESCE(archived_at, $t),
                    disposition = CASE WHEN $exclude = 1 THEN 'excluded_resident' ELSE COALESCE(disposition, 'active') END,
                    updated_at = $t
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$id", contactId);
            update.Parameters.AddWithValue("$t", now);
            update.Parameters.AddWithValue("$exclude", excludeAsResident ? 1 : 0);
            update.ExecuteNonQuery();
        }

        if (excludeAsResident)
        {
            WriteProvenance(
                connection,
                tx,
                EntityTypes.Person,
                contactId,
                "disposition",
                ContactDispositions.ExcludedResident,
                null,
                ContactSourceKinds.UserUpdate,
                now);
        }

        using (var audit = connection.CreateCommand())
        {
            audit.Transaction = tx;
            audit.CommandText =
                """
                INSERT INTO audit_events (id, event_type, entity_type, entity_id, actor, detail_json, created_at)
                VALUES ($id, 'contact.archived', $etype, $eid, $actor, $detail, $t);
                """;
            audit.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            audit.Parameters.AddWithValue("$etype", EntityTypes.Person);
            audit.Parameters.AddWithValue("$eid", contactId);
            audit.Parameters.AddWithValue("$actor", actor);
            audit.Parameters.AddWithValue(
                "$detail",
                JsonSerializer.Serialize(new { provenance = sourceNote, excludeAsResident }));
            audit.Parameters.AddWithValue("$t", now);
            audit.ExecuteNonQuery();
        }

        tx.Commit();
        return existing is null
            ? new ContactDetail
            {
                Id = contactId,
                DisplayName = contactId,
                Disposition = excludeAsResident
                    ? ContactDispositions.ExcludedResident
                    : ContactDispositions.Active,
            }
            : existing with
            {
                Disposition = excludeAsResident
                    ? ContactDispositions.ExcludedResident
                    : existing.Disposition,
            };
    }

    public int CountPendingMergeSuggestions()
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT COUNT(*) FROM agent_suggestions
            WHERE suggestion_type = $type AND status = 'pending' AND archived_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("$type", ContactSuggestionTypes.ContactMerge);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void EnsureContactMethod(
        SqliteConnection connection,
        SqliteTransaction tx,
        string? personId,
        string? organizationId,
        string methodType,
        string value,
        bool isPrimary,
        string now)
    {
        using (var find = connection.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText =
                """
                SELECT id FROM contact_methods
                WHERE method_type = $type
                  AND lower(value) = lower($value)
                  AND (($person IS NOT NULL AND person_id = $person)
                       OR ($org IS NOT NULL AND organization_id = $org))
                  AND archived_at IS NULL
                LIMIT 1;
                """;
            find.Parameters.AddWithValue("$type", methodType);
            find.Parameters.AddWithValue("$value", value);
            find.Parameters.AddWithValue("$person", (object?)personId ?? DBNull.Value);
            find.Parameters.AddWithValue("$org", (object?)organizationId ?? DBNull.Value);
            if (find.ExecuteScalar() is string)
            {
                return;
            }
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText =
            """
            INSERT INTO contact_methods (
              id, person_id, organization_id, method_type, value, is_primary, created_at, updated_at)
            VALUES ($id, $person, $org, $type, $value, $primary, $t, $t);
            """;
        insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        insert.Parameters.AddWithValue("$person", (object?)personId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$org", (object?)organizationId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$type", methodType);
        insert.Parameters.AddWithValue("$value", value);
        insert.Parameters.AddWithValue("$primary", isPrimary ? 1 : 0);
        insert.Parameters.AddWithValue("$t", now);
        insert.ExecuteNonQuery();
    }

    private static void WriteProvenance(
        SqliteConnection connection,
        SqliteTransaction tx,
        string entityType,
        string entityId,
        string field,
        string value,
        string? sourceEmailId,
        string sourceKind,
        string now)
    {
        using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText =
            """
            INSERT INTO contact_fact_provenance (
              id, entity_type, entity_id, field, value, source_email_id, source_kind, created_at)
            VALUES ($id, $etype, $eid, $field, $value, $email, $kind, $t);
            """;
        insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        insert.Parameters.AddWithValue("$etype", entityType);
        insert.Parameters.AddWithValue("$eid", entityId);
        insert.Parameters.AddWithValue("$field", field);
        insert.Parameters.AddWithValue("$value", value);
        insert.Parameters.AddWithValue("$email", (object?)sourceEmailId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$kind", sourceKind);
        insert.Parameters.AddWithValue("$t", now);
        insert.ExecuteNonQuery();
    }

    private static IReadOnlyList<ContactMethodItem> ListMethods(SqliteConnection connection, string personId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, method_type, value, label, is_primary
            FROM contact_methods
            WHERE person_id = $id AND archived_at IS NULL
            ORDER BY
              CASE method_type WHEN 'email' THEN 0 WHEN 'mobile' THEN 1 WHEN 'phone' THEN 2 ELSE 3 END,
              is_primary DESC, value COLLATE NOCASE;
            """;
        cmd.Parameters.AddWithValue("$id", personId);
        var list = new List<ContactMethodItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ContactMethodItem
            {
                Id = reader.GetString(0),
                MethodType = reader.GetString(1),
                Value = reader.GetString(2),
                Label = reader.IsDBNull(3) ? null : reader.GetString(3),
                IsPrimary = reader.GetInt64(4) != 0,
            });
        }

        return list;
    }

    private static IReadOnlyList<ContactProjectItem> ListProjects(SqliteConnection connection, string personId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT DISTINCT p.id, p.name
            FROM relationships r
            INNER JOIN projects p ON p.id = r.target_id
            WHERE r.source_type = $st
              AND r.source_id = $sid
              AND r.target_type = $tt
              AND r.relationship_type = $rt
              AND r.archived_at IS NULL
              AND p.archived_at IS NULL
            ORDER BY p.name COLLATE NOCASE;
            """;
        cmd.Parameters.AddWithValue("$st", EntityTypes.Person);
        cmd.Parameters.AddWithValue("$sid", personId);
        cmd.Parameters.AddWithValue("$tt", EntityTypes.Project);
        cmd.Parameters.AddWithValue("$rt", RelationshipTypes.InvolvedIn);
        var list = new List<ContactProjectItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ContactProjectItem
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
            });
        }

        return list;
    }

    private static IReadOnlyList<ContactEmailSnippet> ListRecentEmails(SqliteConnection connection, string personId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT e.id, e.subject, e.sent_at, e.body_preview, ep.role
            FROM email_participants ep
            INNER JOIN email_artifacts e ON e.id = ep.email_artifact_id
            WHERE ep.person_id = $id AND e.archived_at IS NULL
            ORDER BY COALESCE(e.sent_at, e.created_at) DESC
            LIMIT 10;
            """;
        cmd.Parameters.AddWithValue("$id", personId);
        var list = new List<ContactEmailSnippet>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ContactEmailSnippet
            {
                Id = reader.GetString(0),
                Subject = reader.IsDBNull(1) ? null : reader.GetString(1),
                SentAt = reader.IsDBNull(2) ? null : reader.GetString(2),
                BodyPreview = reader.IsDBNull(3) ? null : reader.GetString(3),
                Role = reader.IsDBNull(4) ? null : reader.GetString(4),
            });
        }

        return list;
    }

    private static IReadOnlyList<ContactProvenanceItem> ListProvenance(
        SqliteConnection connection,
        string entityType,
        string entityId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, field, value, source_email_id, source_kind, created_at
            FROM contact_fact_provenance
            WHERE entity_type = $etype AND entity_id = $eid
            ORDER BY created_at DESC
            LIMIT 40;
            """;
        cmd.Parameters.AddWithValue("$etype", entityType);
        cmd.Parameters.AddWithValue("$eid", entityId);
        var list = new List<ContactProvenanceItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ContactProvenanceItem
            {
                Id = reader.GetString(0),
                Field = reader.GetString(1),
                Value = reader.GetString(2),
                SourceEmailId = reader.IsDBNull(3) ? null : reader.GetString(3),
                SourceKind = reader.GetString(4),
                CreatedAt = reader.GetString(5),
            });
        }

        return list;
    }

    private void UpsertPersonSearchDocument(string personId, string displayName, string now)
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO search_documents (id, entity_type, entity_id, project_id, title, body, updated_at)
            VALUES ($id, 'person', $id, NULL, $title, '', $t)
            ON CONFLICT(id) DO UPDATE SET
              title = excluded.title,
              updated_at = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$id", personId);
        cmd.Parameters.AddWithValue("$title", displayName);
        cmd.Parameters.AddWithValue("$t", now);
        try
        {
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Search table may be mid-rebuild; enrichment still succeeds.
        }
    }
}
