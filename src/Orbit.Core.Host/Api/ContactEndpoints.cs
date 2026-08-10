using Microsoft.AspNetCore.Mvc;
using Orbit.Core.Host;
using Orbit.Core.Host.Auth;
using Orbit.Core.Host.Events;
using Orbit.Infrastructure.Contacts;

namespace Orbit.Core.Host.Api;

public static class ContactEndpoints
{
    public static IEndpointRouteBuilder MapContactEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(HostEndpoints.Contacts, (string? category, string? disposition, ContactStore contacts, HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            var people = contacts.ListPeople(category, disposition);
            return Results.Json(new
            {
                contacts = people.Select(MapListItem),
                requestId,
            });
        });

        app.MapGet($"{HostEndpoints.Contacts}/{{contactId}}", (string contactId, ContactStore contacts, HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            var detail = contacts.GetPerson(contactId);
            if (detail is null)
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.BadRequest, "Contact was not found.", requestId),
                    statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Json(MapDetail(detail, requestId));
        });

        app.MapPost($"{HostEndpoints.Contacts}/{{contactId}}", UpdateContactHandler);
        app.MapMethods($"{HostEndpoints.Contacts}/{{contactId}}", ["PATCH"], UpdateContactHandler);
        app.MapPost($"{HostEndpoints.Contacts}/{{contactId}}/archive", ArchiveContactHandler);
        // DELETE must not bind a JSON body — that breaks endpoint metadata for the whole Host.
        app.MapDelete($"{HostEndpoints.Contacts}/{{contactId}}", (
            string contactId,
            ContactStore contacts,
            EventHub hub,
            HttpContext http) => ArchiveContactCore(contactId, body: null, contacts, hub, http));

        app.MapGet(HostEndpoints.Organizations, (ContactStore contacts, HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            var orgs = contacts.ListOrganizations();
            return Results.Json(new
            {
                organizations = orgs.Select(o => new
                {
                    id = o.Id,
                    name = o.Name,
                    kind = o.Kind,
                    domain = o.Domain,
                }),
                requestId,
            });
        });

        return app;
    }

    private static IResult UpdateContactHandler(
        string contactId,
        [FromBody] UpdateContactRequest? body,
        ContactStore contacts,
        EventHub hub,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            if (body?.Patch is null)
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.BadRequest, "Provide patch with at least one field.", requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var detail = contacts.UpdateContact(contactId, body.Patch, body.Provenance, body.RequestedBy);
            hub.Publish(new OrbitEvent
            {
                Type = "contact.updated",
                Payload = new { contactId = detail.Id, category = detail.Category, disposition = detail.Disposition },
            });

            return Results.Json(MapDetail(detail, requestId));
        }
        catch (ArgumentException ex)
        {
            var status = ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                ? StatusCodes.Status404NotFound
                : StatusCodes.Status400BadRequest;
            return Results.Json(ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId), statusCode: status);
        }
    }

    private static IResult ArchiveContactHandler(
        string contactId,
        [FromBody] ArchiveContactRequest? body,
        ContactStore contacts,
        EventHub hub,
        HttpContext http) =>
        ArchiveContactCore(contactId, body, contacts, hub, http);

    private static IResult ArchiveContactCore(
        string contactId,
        ArchiveContactRequest? body,
        ContactStore contacts,
        EventHub hub,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var exclude = body?.ExcludeAsResident == true;
        var detail = contacts.ArchivePerson(contactId, exclude, body?.Provenance, body?.RequestedBy);
        if (detail is null)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Contact was not found.", requestId),
                statusCode: StatusCodes.Status404NotFound);
        }

        hub.Publish(new OrbitEvent
        {
            Type = "contact.archived",
            Payload = new { contactId, excludeAsResident = exclude },
        });

        return Results.Json(new
        {
            ok = true,
            id = contactId,
            excludeAsResident = exclude,
            disposition = detail.Disposition,
            requestId,
        });
    }

    private static object MapListItem(ContactListItem item) =>
        new
        {
            id = item.Id,
            displayName = item.DisplayName,
            title = item.Title,
            organizationName = item.OrganizationName,
            primaryEmail = item.PrimaryEmail,
            primaryPhone = item.PrimaryPhone,
            category = item.Category,
            disposition = item.Disposition,
        };

    private static object MapDetail(ContactDetail detail, string requestId) =>
        new
        {
            id = detail.Id,
            displayName = detail.DisplayName,
            givenName = detail.GivenName,
            familyName = detail.FamilyName,
            notes = detail.Notes,
            title = detail.Title,
            organizationId = detail.OrganizationId,
            organizationName = detail.OrganizationName,
            category = detail.Category,
            disposition = detail.Disposition,
            reportsToPersonId = detail.ReportsToPersonId,
            reportsToDisplayName = detail.ReportsToDisplayName,
            methods = detail.Methods.Select(m => new
            {
                id = m.Id,
                methodType = m.MethodType,
                value = m.Value,
                label = m.Label,
                isPrimary = m.IsPrimary,
            }),
            projects = detail.Projects.Select(p => new { id = p.Id, name = p.Name }),
            recentEmails = detail.RecentEmails.Select(e => new
            {
                id = e.Id,
                subject = e.Subject,
                sentAt = e.SentAt,
                bodyPreview = e.BodyPreview,
                role = e.Role,
            }),
            provenance = detail.Provenance.Select(p => new
            {
                id = p.Id,
                field = p.Field,
                value = p.Value,
                sourceEmailId = p.SourceEmailId,
                sourceKind = p.SourceKind,
                createdAt = p.CreatedAt,
            }),
            requestId,
        };

    private sealed class ArchiveContactRequest
    {
        public bool? ExcludeAsResident { get; set; }

        public string? Provenance { get; set; }

        public string? RequestedBy { get; set; }
    }
}
