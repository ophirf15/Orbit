using System.Text.Json;
using Orbit.Core.Host;
using Orbit.Core.Host.Auth;
using Orbit.Core.Host.Events;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Malleability;

namespace Orbit.Core.Host.Api;

/// <summary>
/// Runtime schema/layout tools (operator-safe) plus gated developer/source tools.
/// </summary>
public static class MalleabilityEndpoints
{
    public static IEndpointRouteBuilder MapMalleabilityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(HostEndpoints.CustomFields, ListCustomFields);
        app.MapGet(HostEndpoints.CustomFieldValues, ListCustomFieldValues);
        app.MapGet(HostEndpoints.Layouts, ListLayouts);
        app.MapGet(HostEndpoints.LayoutById, GetLayout);
        app.MapGet(HostEndpoints.LayoutRevisions, ListLayoutRevisions);

        app.MapPost(HostEndpoints.AgentToolAddCustomField, AddCustomField);
        app.MapPost(HostEndpoints.AgentToolSetCustomFieldValue, SetCustomFieldValue);
        app.MapPost(HostEndpoints.AgentToolUpdateCustomFieldLabel, UpdateCustomFieldLabel);
        app.MapPost(HostEndpoints.AgentToolSaveLayout, SaveLayout);
        app.MapPost(HostEndpoints.AgentToolApplyLayout, ApplyLayout);
        app.MapPost(HostEndpoints.AgentToolRevertLayout, RevertLayout);

        app.MapPost(HostEndpoints.AgentToolDevCreateBranch, DevCreateBranch);
        app.MapPost(HostEndpoints.AgentToolDevWriteFile, DevWriteFile);
        app.MapPost(HostEndpoints.AgentToolDevBuild, DevBuild);

        return app;
    }

    private static IResult ListCustomFields(string? entityType, CustomFieldStore fields, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var list = fields.ListDefinitions(entityType);
        return Results.Json(new { requestId, fields = list });
    }

    private static IResult ListCustomFieldValues(
        string? entityType,
        string? entityId,
        CustomFieldStore fields,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        if (string.IsNullOrWhiteSpace(entityType) || string.IsNullOrWhiteSpace(entityId))
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Query params entityType and entityId are required.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var defs = fields.ListDefinitions(entityType);
        var values = fields.ListValues(entityType, entityId);
        var valueMap = values.ToDictionary(v => v.FieldKey, v => v, StringComparer.OrdinalIgnoreCase);
        return Results.Json(new
        {
            requestId,
            entityType,
            entityId,
            fields = defs.Select(d => new
            {
                key = d.Key,
                label = string.IsNullOrWhiteSpace(d.Label) ? d.Key : d.Label,
                fieldType = d.FieldType,
                valueJson = valueMap.TryGetValue(d.Key, out var v) ? v.ValueJson : null,
            }),
        });
    }

    private static IResult ListLayouts(LayoutStore layouts, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        return Results.Json(new { requestId, layouts = layouts.List() });
    }

    private static IResult GetLayout(string id, LayoutStore layouts, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var layout = layouts.Get(id);
        if (layout is null)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.NotFound, "Layout was not found.", requestId),
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(new { requestId, layout });
    }

    private static IResult ListLayoutRevisions(string id, LayoutStore layouts, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        if (layouts.Get(id) is null)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.NotFound, "Layout was not found.", requestId),
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(new { requestId, revisions = layouts.ListRevisions(id) });
    }

    private static IResult AddCustomField(AddCustomFieldBody? body, CustomFieldStore fields, EventHub hub, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            if (body is null
                || string.IsNullOrWhiteSpace(body.EntityType)
                || string.IsNullOrWhiteSpace(body.Key)
                || string.IsNullOrWhiteSpace(body.FieldType))
            {
                return Results.Json(
                    ApiErrors.Create(
                        ApiErrorCodes.BadRequest,
                        "Body fields 'entityType', 'key', and 'fieldType' are required.",
                        requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var validationJson = body.ValidationJson
                ?? (body.Validation is null ? null : JsonSerializer.Serialize(body.Validation));
            var displayJson = body.DisplayJson
                ?? (body.Display is null ? null : JsonSerializer.Serialize(body.Display));

            var field = fields.AddField(
                body.EntityType,
                body.Key,
                body.FieldType,
                validationJson,
                displayJson,
                body.Actor ?? "agent",
                MapProvenance(body.Provenance));

            hub.Publish(new OrbitEvent
            {
                Type = "custom_field.added",
                Payload = new { fieldId = field.Id, entityType = field.EntityType, key = field.Key },
            });

            return Results.Json(
                new { tool = "orbit_add_custom_field", requestId, field },
                statusCode: StatusCodes.Status201Created);
        }
        catch (ArgumentException ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static IResult SetCustomFieldValue(
        SetCustomFieldValueBody? body,
        CustomFieldStore fields,
        EventHub hub,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            if (body is null
                || string.IsNullOrWhiteSpace(body.EntityType)
                || string.IsNullOrWhiteSpace(body.EntityId)
                || string.IsNullOrWhiteSpace(body.FieldKey)
                || body.Value is null
                || body.Value.Value.ValueKind is JsonValueKind.Undefined)
            {
                return Results.Json(
                    ApiErrors.Create(
                        ApiErrorCodes.BadRequest,
                        "Body fields 'entityType', 'entityId', 'fieldKey', and 'value' are required.",
                        requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var stored = fields.SetValue(
                body.EntityType,
                body.EntityId,
                body.FieldKey,
                body.Value.Value,
                body.Actor ?? "agent",
                MapProvenance(body.Provenance));

            hub.Publish(new OrbitEvent
            {
                Type = "custom_field.value_set",
                Payload = new
                {
                    entityType = stored.EntityType,
                    entityId = stored.EntityId,
                    fieldKey = stored.FieldKey,
                },
            });

            return Results.Json(new { tool = "orbit_set_custom_field_value", requestId, value = stored });
        }
        catch (ArgumentException ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static IResult UpdateCustomFieldLabel(
        UpdateCustomFieldLabelBody? body,
        CustomFieldStore fields,
        EventHub hub,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            if (body is null
                || string.IsNullOrWhiteSpace(body.EntityType)
                || string.IsNullOrWhiteSpace(body.FieldKey)
                || string.IsNullOrWhiteSpace(body.Label))
            {
                return Results.Json(
                    ApiErrors.Create(
                        ApiErrorCodes.BadRequest,
                        "Body fields 'entityType', 'fieldKey', and 'label' are required.",
                        requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var field = fields.UpdateLabel(body.EntityType, body.FieldKey, body.Label);
            hub.Publish(new OrbitEvent
            {
                Type = "custom_field.label_updated",
                Payload = new { entityType = field.EntityType, key = field.Key, label = field.Label },
            });
            return Results.Json(new { tool = "orbit_update_custom_field_label", requestId, field });
        }
        catch (ArgumentException ex)
        {
            var notFound = ex.ParamName == "key";
            return Results.Json(
                ApiErrors.Create(
                    notFound ? ApiErrorCodes.NotFound : ApiErrorCodes.BadRequest,
                    ex.Message,
                    requestId),
                statusCode: notFound ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest);
        }
    }

    private static IResult SaveLayout(SaveLayoutBody? body, LayoutStore layouts, EventHub hub, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Name) || string.IsNullOrWhiteSpace(body.SchemaJson))
            {
                var schemaFromObject = body?.Schema is null ? null : JsonSerializer.Serialize(body.Schema);
                if (body is null || string.IsNullOrWhiteSpace(body.Name) || string.IsNullOrWhiteSpace(schemaFromObject ?? body.SchemaJson))
                {
                    return Results.Json(
                        ApiErrors.Create(
                            ApiErrorCodes.BadRequest,
                            "Body fields 'name' and 'schemaJson' (or 'schema') are required.",
                            requestId),
                        statusCode: StatusCodes.Status400BadRequest);
                }
            }

            var schemaJson = !string.IsNullOrWhiteSpace(body!.SchemaJson)
                ? body.SchemaJson
                : JsonSerializer.Serialize(body.Schema);

            var layout = layouts.Save(
                body.Name!,
                schemaJson!,
                body.LayoutId,
                body.Actor ?? "agent",
                MapProvenance(body.Provenance));

            hub.Publish(new OrbitEvent
            {
                Type = "layout.saved",
                Payload = new { layoutId = layout.Id, version = layout.Version },
            });

            return Results.Json(
                new { tool = "orbit_save_layout", requestId, layout },
                statusCode: StatusCodes.Status201Created);
        }
        catch (ArgumentException ex)
        {
            var notFound = ex.ParamName == "layoutId";
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId),
                statusCode: notFound ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest);
        }
    }

    private static IResult ApplyLayout(LayoutIdBody? body, LayoutStore layouts, EventHub hub, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            if (body is null || string.IsNullOrWhiteSpace(body.LayoutId ?? body.Id))
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.BadRequest, "Body field 'layoutId' is required.", requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var layout = layouts.Apply(body.LayoutId ?? body.Id!, body.Actor ?? "agent", MapProvenance(body.Provenance));
            hub.Publish(new OrbitEvent
            {
                Type = "layout.applied",
                Payload = new { layoutId = layout.Id, version = layout.Version },
            });
            return Results.Json(new { tool = "orbit_apply_layout", requestId, layout });
        }
        catch (ArgumentException ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId),
                statusCode: StatusCodes.Status404NotFound);
        }
    }

    private static IResult RevertLayout(RevertLayoutBody? body, LayoutStore layouts, EventHub hub, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            if (body is null || string.IsNullOrWhiteSpace(body.LayoutId ?? body.Id))
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.BadRequest, "Body field 'layoutId' is required.", requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var layout = layouts.Revert(
                body.LayoutId ?? body.Id!,
                body.ToVersion,
                body.Actor ?? "agent",
                MapProvenance(body.Provenance));

            hub.Publish(new OrbitEvent
            {
                Type = "layout.reverted",
                Payload = new { layoutId = layout.Id, version = layout.Version },
            });
            return Results.Json(new { tool = "orbit_revert_layout", requestId, layout });
        }
        catch (ArgumentException ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static IResult DevCreateBranch(DevCreateBranchBody? body, DeveloperSourceService developer, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            if (body is null || string.IsNullOrWhiteSpace(body.BranchName))
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.BadRequest, "Body field 'branchName' is required.", requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var channel = body.Provenance?.Channel ?? body.Channel;
            var result = developer.CreateBranch(body.BranchName, channel);
            return Results.Json(new { tool = "orbit_dev_create_branch", requestId, result });
        }
        catch (DeveloperSourceDeniedException ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.PathDenied, ex.Message, requestId),
                statusCode: StatusCodes.Status403Forbidden);
        }
        catch (ArgumentException ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static IResult DevWriteFile(DevWriteFileBody? body, DeveloperSourceService developer, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Path) || body.Contents is null)
            {
                return Results.Json(
                    ApiErrors.Create(
                        ApiErrorCodes.BadRequest,
                        "Body fields 'path' and 'contents' are required.",
                        requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var channel = body.Provenance?.Channel ?? body.Channel;
            var result = developer.WriteFileUnderRepo(body.Path, body.Contents, channel);
            return Results.Json(new { tool = "orbit_dev_write_file", requestId, result });
        }
        catch (DeveloperSourceDeniedException ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.PathDenied, ex.Message, requestId),
                statusCode: StatusCodes.Status403Forbidden);
        }
        catch (ArgumentException ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static IResult DevBuild(DevBuildBody? body, DeveloperSourceService developer, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            var channel = body?.Provenance?.Channel ?? body?.Channel;
            var result = developer.RunDotnetBuild(channel);
            return Results.Json(
                new { tool = "orbit_dev_build", requestId, result },
                statusCode: result.Success ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
        }
        catch (DeveloperSourceDeniedException ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.PathDenied, ex.Message, requestId),
                statusCode: StatusCodes.Status403Forbidden);
        }
        catch (ArgumentException ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (TimeoutException ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId),
                statusCode: StatusCodes.Status408RequestTimeout);
        }
    }

    private static MutationProvenance? MapProvenance(MutationProvenanceBody? body)
    {
        if (body is null)
        {
            return null;
        }

        var mapped = new MutationProvenance
        {
            Actor = body.Actor,
            Channel = body.Channel,
            HermesSessionId = body.HermesSessionId,
            ExternalUserId = body.ExternalUserId,
            TelegramUserId = body.TelegramUserId,
        };
        return mapped.HasValues ? mapped : null;
    }

    private sealed class MutationProvenanceBody
    {
        public string? Actor { get; set; }

        public string? Channel { get; set; }

        public string? HermesSessionId { get; set; }

        public string? ExternalUserId { get; set; }

        public string? TelegramUserId { get; set; }
    }

    private sealed class AddCustomFieldBody
    {
        public string? EntityType { get; set; }

        public string? Key { get; set; }

        public string? FieldType { get; set; }

        public string? ValidationJson { get; set; }

        public object? Validation { get; set; }

        public string? DisplayJson { get; set; }

        public object? Display { get; set; }

        public string? Actor { get; set; }

        public MutationProvenanceBody? Provenance { get; set; }
    }

    private sealed class SetCustomFieldValueBody
    {
        public string? EntityType { get; set; }

        public string? EntityId { get; set; }

        public string? FieldKey { get; set; }

        public JsonElement? Value { get; set; }

        public string? Actor { get; set; }

        public MutationProvenanceBody? Provenance { get; set; }
    }

    private sealed class UpdateCustomFieldLabelBody
    {
        public string? EntityType { get; set; }

        public string? FieldKey { get; set; }

        public string? Label { get; set; }
    }

    private sealed class SaveLayoutBody
    {
        public string? LayoutId { get; set; }

        public string? Name { get; set; }

        public string? SchemaJson { get; set; }

        public object? Schema { get; set; }

        public string? Actor { get; set; }

        public MutationProvenanceBody? Provenance { get; set; }
    }

    private sealed class LayoutIdBody
    {
        public string? Id { get; set; }

        public string? LayoutId { get; set; }

        public string? Actor { get; set; }

        public MutationProvenanceBody? Provenance { get; set; }
    }

    private sealed class RevertLayoutBody
    {
        public string? Id { get; set; }

        public string? LayoutId { get; set; }

        public int? ToVersion { get; set; }

        public string? Actor { get; set; }

        public MutationProvenanceBody? Provenance { get; set; }
    }

    private sealed class DevCreateBranchBody
    {
        public string? BranchName { get; set; }

        public string? Channel { get; set; }

        public MutationProvenanceBody? Provenance { get; set; }
    }

    private sealed class DevWriteFileBody
    {
        public string? Path { get; set; }

        public string? Contents { get; set; }

        public string? Channel { get; set; }

        public MutationProvenanceBody? Provenance { get; set; }
    }

    private sealed class DevBuildBody
    {
        public string? Channel { get; set; }

        public MutationProvenanceBody? Provenance { get; set; }
    }
}
