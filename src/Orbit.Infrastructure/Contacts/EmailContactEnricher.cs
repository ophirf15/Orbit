using Orbit.Infrastructure.Email;

namespace Orbit.Infrastructure.Contacts;

/// <summary>
/// Turns an ingested email into people/org observations with provenance (heuristic only).
/// </summary>
public sealed class EmailContactEnricher
{
    private readonly ContactStore _contacts;

    public EmailContactEnricher(ContactStore contacts) => _contacts = contacts;

    public ContactEnrichmentResult Enrich(
        string emailId,
        IReadOnlyList<ParsedEmailParticipant> participants,
        string? bodyText,
        IReadOnlyList<string>? projectIds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(emailId);
        var personIds = new List<string>();
        var suggestionCountBefore = _contacts.CountPendingMergeSuggestions();

        var from = participants.FirstOrDefault(p =>
            string.Equals(p.Role, "from", StringComparison.OrdinalIgnoreCase));
        var signature = SignatureHeuristic.Parse(bodyText);

        foreach (var participant in participants)
        {
            var email = ContactResolution.NormalizeEmail(participant.Address);
            if (email is null)
            {
                continue;
            }

            var personId = _contacts.UpsertPersonByEmail(
                email,
                participant.DisplayName,
                emailId,
                ContactSourceKinds.EmailParticipant);
            personIds.Add(personId);
            _contacts.SetEmailParticipantPerson(emailId, email, personId);

            if (_contacts.IsExcludedFromTracking(personId))
            {
                continue;
            }

            var domain = ContactResolution.ExtractDomain(email);
            string? orgId = null;
            if (domain is not null && !ContactResolution.IsFreeMailDomain(domain))
            {
                orgId = _contacts.EnsureOrganizationForDomain(domain, emailId);
                var titleForMember = from is not null
                    && string.Equals(
                        ContactResolution.NormalizeEmail(from.Address),
                        email,
                        StringComparison.Ordinal)
                    ? signature.Title
                    : null;
                _contacts.EnsureMembership(personId, orgId, titleForMember, emailId,
                    titleForMember is null
                        ? ContactSourceKinds.DomainInference
                        : ContactSourceKinds.SignatureHeuristic);
            }

            MaybeSuggestMerge(personId, participant.DisplayName, email, domain, emailId);

            if (projectIds is { Count: > 0 })
            {
                _contacts.LinkPersonToProjects(personId, projectIds);
            }
        }

        // Signature phones / title apply to the From person only.
        if (from is not null)
        {
            var fromEmail = ContactResolution.NormalizeEmail(from.Address);
            if (fromEmail is not null)
            {
                var fromPersonId = _contacts.FindPersonIdByEmail(fromEmail);
                if (fromPersonId is not null && !_contacts.IsExcludedFromTracking(fromPersonId))
                {
                    ApplySignature(fromPersonId, signature, emailId, projectIds);
                }
            }
        }

        return new ContactEnrichmentResult
        {
            EmailId = emailId,
            PersonIds = personIds.Distinct(StringComparer.Ordinal).ToList(),
            SuggestionCount = Math.Max(0, _contacts.CountPendingMergeSuggestions() - suggestionCountBefore),
        };
    }

    private void ApplySignature(
        string personId,
        SignatureHeuristic.SignatureFacts signature,
        string emailId,
        IReadOnlyList<string>? projectIds)
    {
        if (!string.IsNullOrWhiteSpace(signature.MobilePhone))
        {
            _contacts.EnsurePhoneMethod(
                personId,
                ContactMethodTypes.Mobile,
                signature.MobilePhone,
                emailId,
                ContactSourceKinds.SignatureHeuristic);
        }

        if (!string.IsNullOrWhiteSpace(signature.DirectPhone))
        {
            _contacts.EnsurePhoneMethod(
                personId,
                ContactMethodTypes.Phone,
                signature.DirectPhone,
                emailId,
                ContactSourceKinds.SignatureHeuristic);
        }

        if (!string.IsNullOrWhiteSpace(signature.OfficePhone))
        {
            _contacts.EnsurePhoneMethod(
                personId,
                ContactMethodTypes.Phone,
                signature.OfficePhone,
                emailId,
                ContactSourceKinds.SignatureHeuristic);
        }

        if (!string.IsNullOrWhiteSpace(signature.Title) || !string.IsNullOrWhiteSpace(signature.OrganizationName))
        {
            // Title already applied via membership when domain org exists; if signature org name
            // differs and no domain org, leave for Hermes. Re-apply title when membership exists.
            var detail = _contacts.GetPerson(personId);
            if (detail?.OrganizationId is not null && !string.IsNullOrWhiteSpace(signature.Title))
            {
                _contacts.EnsureMembership(
                    personId,
                    detail.OrganizationId,
                    signature.Title,
                    emailId,
                    ContactSourceKinds.SignatureHeuristic);
            }
        }

        if (projectIds is { Count: > 0 })
        {
            _contacts.LinkPersonToProjects(personId, projectIds);
        }
    }

    private void MaybeSuggestMerge(
        string personId,
        string? displayName,
        string email,
        string? domain,
        string emailId)
    {
        if (string.IsNullOrWhiteSpace(displayName) || domain is null)
        {
            return;
        }

        var sameName = _contacts.FindPeopleByDisplayName(displayName);
        foreach (var (otherId, _, otherEmail) in sameName)
        {
            if (string.Equals(otherId, personId, StringComparison.Ordinal))
            {
                continue;
            }

            var otherDomain = ContactResolution.ExtractDomain(otherEmail);
            if (!string.Equals(otherDomain, domain, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(
                    ContactResolution.NormalizeEmail(otherEmail),
                    email,
                    StringComparison.Ordinal))
            {
                continue;
            }

            // Same display name + same org domain + different emails → uncertain merge.
            _contacts.CreateMergeSuggestion(
                personId,
                otherId,
                $"Same name '{displayName}' and domain '{domain}' with different emails",
                emailId);
        }
    }
}
