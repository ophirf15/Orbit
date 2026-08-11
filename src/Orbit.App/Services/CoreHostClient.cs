using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Orbit.Core.Settings;
using Orbit.Infrastructure.Settings;
using Orbit_App.ViewModels;

namespace Orbit_App.Services;

public enum CoreHostConnectionState
{
    Unknown = 0,
    Connected = 1,
    Degraded = 2,
}

public sealed class CoreHostStatus
{
    public CoreHostConnectionState State { get; init; }

    public string Message { get; init; } = string.Empty;

    public string? Version { get; init; }

    public string BaseUrl { get; init; } = string.Empty;
}

public sealed class CoreHostClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public CoreHostClient(OrbitSettings settings, JsonOrbitSettingsStore store, HttpMessageHandler? handler = null)
    {
        _baseUrl = string.IsNullOrWhiteSpace(settings.CoreHostBaseUrl)
            ? OrbitSettingsDefaults.CoreHostBaseUrl
            : settings.CoreHostBaseUrl.TrimEnd('/');

        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = new Uri(_baseUrl + "/");
        _http.Timeout = TimeSpan.FromSeconds(8);

        var key = store.ReadCoreHostApiKey(settings);
        if (!string.IsNullOrWhiteSpace(key))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
    }

    public string BaseUrl => _baseUrl;

    /// <summary>Set when <see cref="IngestEmailAsync"/> returns null.</summary>
    public string? LastEmailIngestError { get; private set; }

    private static async Task<string?> ReadErrorMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString();
            }

            if (doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var nested)
                && nested.ValueKind == JsonValueKind.String)
            {
                return nested.GetString();
            }

            return body.Length > 240 ? body[..240] : body;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<bool> TryHealthAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync("v1/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>True when the running host advertises a named capability in /v1/health.</summary>
    public async Task<bool> HasHealthFeatureAsync(string feature, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(feature))
        {
            return false;
        }

        try
        {
            using var response = await _http.GetAsync("v1/health", ct);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("features", out var features)
                || features.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var item in features.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String
                    && string.Equals(item.GetString(), feature, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<string?> TryGetVersionAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync("v1/version", ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("version", out var version))
            {
                return version.GetString();
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<WorkbenchVm?> GetWorkbenchAsync(string? projectId = null, CancellationToken ct = default)
    {
        var url = string.IsNullOrWhiteSpace(projectId)
            ? "v1/workbench"
            : $"v1/workbench?projectId={Uri.EscapeDataString(projectId)}";
        using var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<WorkbenchDto>(stream, JsonOptions, ct);
        if (dto is null)
        {
            return null;
        }

        return new WorkbenchVm
        {
            Scope = dto.Scope is null
                ? null
                : new WorkbenchScopeVm
                {
                    Kind = dto.Scope.Kind ?? "project",
                    ProjectId = dto.Scope.ProjectId,
                    ProjectName = dto.Scope.ProjectName,
                },
            Cells = (dto.Cells ?? []).Select(c => new ProjectCellVm
            {
                Id = c.Id ?? string.Empty,
                Name = c.Name ?? string.Empty,
                Code = c.Code,
                Summary = c.Summary,
                Status = c.Status ?? string.Empty,
                CellKind = string.IsNullOrWhiteSpace(c.CellKind) ? "project" : c.CellKind!,
                Lines = (c.Lines ?? []).Select(l => new CellLineVm
                {
                    TaskId = l.TaskId ?? string.Empty,
                    Title = l.Title ?? string.Empty,
                    Status = l.Status ?? string.Empty,
                    NextAction = l.NextAction,
                    Body = l.Body,
                }).ToList(),
                OpenBlockerCount = c.OpenBlockerCount,
                TopBlockerSummary = c.TopBlockerSummary,
                UpcomingMeetingTitle = c.UpcomingMeetingTitle,
                UpcomingMeetingStartsAt = c.UpcomingMeetingStartsAt,
                PendingSuggestionCount = c.PendingSuggestionCount,
                RecentActivityAt = c.RecentActivityAt,
                AccentColor = c.AccentColor,
                SortOrder = c.SortOrder,
                BoardX = c.BoardX ?? 0,
                BoardY = c.BoardY ?? 0,
                BoardW = c.BoardW ?? 0,
                BoardH = c.BoardH ?? 0,
                HasSavedLayout = c.BoardX is not null && c.BoardY is not null && c.BoardW is not null && c.BoardH is not null,
                DossierEmpty = c.DossierEmpty,
                MissingNextAction = c.MissingNextAction,
            }).ToList(),
            Limbo = (dto.Limbo ?? []).Select(n => new LimboNoteVm
            {
                Id = n.Id ?? string.Empty,
                OriginalText = n.OriginalText ?? string.Empty,
                CreatedAt = n.CreatedAt ?? string.Empty,
                SuggestionId = n.SuggestionId,
                SuggestionSummary = n.SuggestionSummary,
            }).ToList(),
        };
    }

    public async Task<CaptureResponseVm?> CreateNoteAsync(string text, string? projectId, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(
            "v1/notes",
            new { text, projectId },
            ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<CaptureDto>(stream, JsonOptions, ct);
        if (dto is null)
        {
            return null;
        }

        return new CaptureResponseVm
        {
            NoteId = dto.NoteId ?? string.Empty,
            TaskId = dto.TaskId,
            OriginalText = dto.OriginalText ?? text,
            ProjectId = dto.ProjectId,
            IsLimbo = dto.IsLimbo,
        };
    }

    public async Task<ProjectCreateResult?> CreateProjectAsync(
        string? name,
        string? summary = null,
        CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(
            "v1/projects",
            new { name, summary },
            ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<ProjectCreateDto>(stream, JsonOptions, ct);
        if (dto is null || string.IsNullOrWhiteSpace(dto.Id))
        {
            return null;
        }

        return new ProjectCreateResult
        {
            Id = dto.Id!,
            Name = dto.Name ?? name ?? "Untitled project",
            Summary = dto.Summary,
            Status = dto.Status ?? "active",
        };
    }

    /// <summary>Creates a project named after the folder, then sets that path as home.</summary>
    public async Task<ProjectCreateFromFolderResult?> CreateProjectFromFolderAsync(
        string folderPath,
        CancellationToken ct = default)
    {
        var name = Orbit.Infrastructure.Data.ProjectNaming.FromFolderPath(folderPath);
        var created = await CreateProjectAsync(name, ct: ct);
        if (created is null)
        {
            return null;
        }

        var home = await SetProjectHomeFolderAsync(created.Id, folderPath, ct);
        return new ProjectCreateFromFolderResult
        {
            Project = created,
            Home = home,
        };
    }

    public async Task<bool> UpdateProjectAsync(
        string projectId,
        string? name = null,
        string? summary = null,
        string? code = null,
        bool clearCode = false,
        object? dossier = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId)
            || (name is null && summary is null && code is null && !clearCode && dossier is null))
        {
            return false;
        }

        var payload = new Dictionary<string, object?>();
        if (name is not null)
        {
            payload["name"] = name;
        }

        if (summary is not null)
        {
            payload["summary"] = summary;
        }

        if (clearCode)
        {
            payload["clearCode"] = true;
        }
        else if (code is not null)
        {
            payload["code"] = code;
        }

        if (dossier is not null)
        {
            payload["dossier"] = dossier;
        }

        using var response = await _http.PatchAsJsonAsync(
            $"v1/projects/{Uri.EscapeDataString(projectId)}",
            payload,
            ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<IReadOnlyList<ProjectAliasVm>> ListProjectAliasesAsync(
        string projectId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return [];
        }

        using var response = await _http.GetAsync(
            $"v1/projects/{Uri.EscapeDataString(projectId)}/aliases",
            ct);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("aliases", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<ProjectAliasVm>();
        foreach (var el in arr.EnumerateArray())
        {
            var id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var alias = el.TryGetProperty("alias", out var aliasEl) ? aliasEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(alias))
            {
                continue;
            }

            list.Add(new ProjectAliasVm { Id = id, Alias = alias });
        }

        return list;
    }

    public async Task<ProjectAliasVm?> AddProjectAliasAsync(
        string projectId,
        string alias,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(alias))
        {
            return null;
        }

        using var response = await _http.PostAsJsonAsync(
            $"v1/projects/{Uri.EscapeDataString(projectId)}/aliases",
            new { alias },
            ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<ProjectAliasDto>(stream, JsonOptions, ct);
        if (dto is null || string.IsNullOrWhiteSpace(dto.Id) || string.IsNullOrWhiteSpace(dto.Alias))
        {
            return null;
        }

        return new ProjectAliasVm { Id = dto.Id, Alias = dto.Alias };
    }

    public async Task<bool> RemoveProjectAliasAsync(
        string projectId,
        string aliasId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(aliasId))
        {
            return false;
        }

        using var response = await _http.DeleteAsync(
            $"v1/projects/{Uri.EscapeDataString(projectId)}/aliases/{Uri.EscapeDataString(aliasId)}",
            ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> UpdateNoteAsync(string noteId, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(noteId))
        {
            return false;
        }

        using var response = await _http.PatchAsJsonAsync(
            $"v1/notes/{Uri.EscapeDataString(noteId)}",
            new { text },
            ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<(string Id, string Title, string ProjectId)?> CreateTaskAsync(
        string title,
        string projectId,
        string? nextAction = null,
        string? body = null,
        string? status = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        using var response = await _http.PostAsJsonAsync(
            "v1/agent/tools/orbit_create_task",
            new
            {
                title,
                projectId,
                nextAction,
                body,
                status,
                actor = "user",
            },
            ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("task", out var task))
        {
            return null;
        }

        var id = task.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        var taskTitle = task.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : title;
        var pid = task.TryGetProperty("projectId", out var pidEl) ? pidEl.GetString() : projectId;
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return (id!, taskTitle ?? title, pid ?? projectId);
    }

    public async Task<bool> SetBlockerAsync(
        string summary,
        string? projectId = null,
        string? taskId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return false;
        }

        using var response = await _http.PostAsJsonAsync(
            "v1/agent/tools/orbit_set_blocker",
            new
            {
                summary,
                projectId,
                taskId,
                actor = "user",
            },
            ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> UpdateTaskAsync(
        string taskId,
        string? title = null,
        string? status = null,
        string? nextAction = null,
        string? body = null,
        string? dueAt = null,
        int? priority = null,
        int? urgency = null,
        string? projectId = null,
        string? workstreamId = null,
        bool clearWorkstream = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return false;
        }

        using var response = await _http.PostAsJsonAsync(
            "v1/agent/tools/orbit_update_task",
            BuildUpdateTaskBody(
                taskId, title, status, nextAction, body, dueAt, priority, urgency,
                projectId, workstreamId, clearWorkstream),
            ct);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Moves a task to another project via <c>orbit_update_task</c> (audited).</summary>
    public async Task<(bool Ok, string? Error)> MoveTaskAsync(
        string taskId,
        string projectId,
        string? workstreamId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(projectId))
        {
            return (false, "Task and project are required.");
        }

        try
        {
            using var response = await _http.PostAsJsonAsync(
                "v1/agent/tools/orbit_update_task",
                BuildUpdateTaskBody(
                    taskId,
                    projectId: projectId,
                    workstreamId: workstreamId),
                ct);
            if (response.IsSuccessStatusCode)
            {
                return (true, null);
            }

            var detail = await response.Content.ReadAsStringAsync(ct);
            var hint = $"Move failed ({(int)response.StatusCode}).";
            if (!string.IsNullOrWhiteSpace(detail) && detail.Length < 240)
            {
                hint = $"{hint} {detail.Trim()}";
            }

            return (false, hint);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static Dictionary<string, object?> BuildUpdateTaskBody(
        string taskId,
        string? title = null,
        string? status = null,
        string? nextAction = null,
        string? body = null,
        string? dueAt = null,
        int? priority = null,
        int? urgency = null,
        string? projectId = null,
        string? workstreamId = null,
        bool clearWorkstream = false)
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = taskId,
            ["actor"] = "user",
        };
        if (title is not null)
        {
            payload["title"] = title;
        }

        if (status is not null)
        {
            payload["status"] = status;
        }

        if (nextAction is not null)
        {
            payload["nextAction"] = nextAction;
        }

        if (body is not null)
        {
            payload["body"] = body;
        }

        if (dueAt is not null)
        {
            payload["dueAt"] = dueAt;
        }

        if (priority is not null)
        {
            payload["priority"] = priority;
        }

        if (urgency is not null)
        {
            payload["urgency"] = urgency;
        }

        if (projectId is not null)
        {
            payload["projectId"] = projectId;
        }

        if (workstreamId is not null)
        {
            payload["workstreamId"] = workstreamId;
        }

        if (clearWorkstream)
        {
            payload["clearWorkstream"] = true;
        }

        return payload;
    }

    public async Task<bool> EnsureCostFieldAsync(string entityType, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(
            "v1/agent/tools/orbit_add_custom_field",
            new
            {
                entityType,
                key = "cost",
                fieldType = "number",
                display = new { label = "Cost" },
                actor = "user",
            },
            ct);
        // 200/201 or already-exists 400 are both fine for ensure.
        return response.IsSuccessStatusCode || (int)response.StatusCode == 400;
    }

    public async Task<bool> ArchiveEntityAsync(string entityType, string entityId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(entityType) || string.IsNullOrWhiteSpace(entityId))
        {
            return false;
        }

        using var response = await _http.PostAsJsonAsync(
            "v1/archive",
            new { entityType, entityId, actor = "user" },
            ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<ProjectMergePreviewVm?> PreviewMergeProjectAsync(
        string sourceProjectId,
        string targetProjectId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceProjectId) || string.IsNullOrWhiteSpace(targetProjectId))
        {
            return null;
        }

        using var response = await _http.PostAsJsonAsync(
            "v1/projects/merge/preview",
            new { sourceProjectId, targetProjectId },
            ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<MergePreviewEnvelopeDto>(stream, JsonOptions, ct);
        return dto?.Preview is null ? null : MapMergePreview(dto.Preview);
    }

    public async Task<ProjectMergeResultVm?> MergeProjectAsync(
        string sourceProjectId,
        string targetProjectId,
        bool force = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceProjectId) || string.IsNullOrWhiteSpace(targetProjectId))
        {
            return null;
        }

        using var response = await _http.PostAsJsonAsync(
            "v1/projects/merge",
            new { sourceProjectId, targetProjectId, force, actor = "user" },
            ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<MergeResultEnvelopeDto>(stream, JsonOptions, ct);
        return dto?.Merge is null ? null : MapMergeResult(dto.Merge);
    }

    public async Task<IReadOnlyList<CustomFieldRowVm>> GetCustomFieldsAsync(
        string entityType,
        string entityId,
        CancellationToken ct = default)
    {
        var url =
            $"v1/custom-fields/values?entityType={Uri.EscapeDataString(entityType)}&entityId={Uri.EscapeDataString(entityId)}";
        using var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<CustomFieldValuesDto>(stream, JsonOptions, ct);
        return (dto?.Fields ?? [])
            .Where(f => !string.IsNullOrWhiteSpace(f.Key))
            .Select(f => new CustomFieldRowVm
            {
                Key = f.Key!,
                Label = string.IsNullOrWhiteSpace(f.Label) ? f.Key! : f.Label!,
                FieldType = f.FieldType ?? "text",
                Value = UnwrapJsonString(f.ValueJson),
            })
            .ToList();
    }

    public async Task<bool> EnsureCustomFieldAsync(
        string entityType,
        string key,
        string fieldType = "text",
        string? label = null,
        CancellationToken ct = default)
    {
        object body = string.IsNullOrWhiteSpace(label)
            ? new { entityType, key, fieldType, actor = "user" }
            : new
            {
                entityType,
                key,
                fieldType,
                actor = "user",
                display = new { label },
            };
        using var response = await _http.PostAsJsonAsync(
            "v1/agent/tools/orbit_add_custom_field",
            body,
            ct);
        // 409 / already exists still ok for ensure
        return response.IsSuccessStatusCode || (int)response.StatusCode is 400 or 409;
    }

    public async Task<bool> UpdateCustomFieldLabelAsync(
        string entityType,
        string fieldKey,
        string label,
        CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(
            "v1/agent/tools/orbit_update_custom_field_label",
            new { entityType, fieldKey, label },
            ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> SetCustomFieldValueAsync(
        string entityType,
        string entityId,
        string fieldKey,
        string value,
        CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(
            "v1/agent/tools/orbit_set_custom_field_value",
            new
            {
                entityType,
                entityId,
                fieldKey,
                value = JsonSerializer.SerializeToElement(value ?? string.Empty),
                actor = "user",
            },
            ct);
        return response.IsSuccessStatusCode;
    }

    private static string UnwrapJsonString(string? valueJson)
    {
        if (string.IsNullOrWhiteSpace(valueJson) || valueJson == "null")
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(valueJson);
            return doc.RootElement.ValueKind switch
            {
                JsonValueKind.String => doc.RootElement.GetString() ?? string.Empty,
                JsonValueKind.Number => doc.RootElement.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => valueJson,
            };
        }
        catch (JsonException)
        {
            return valueJson;
        }
    }

    public async Task<bool> AcceptSuggestionAsync(
        string suggestionId,
        string? applyProjectId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(suggestionId))
        {
            return false;
        }

        using var response = await _http.PostAsJsonAsync(
            $"v1/suggestions/{Uri.EscapeDataString(suggestionId)}/accept",
            new { actor = "user", applyProjectId },
            ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> AcceptSuggestionAlwaysAsync(
        string suggestionId,
        string? applyProjectId = null,
        string? ruleName = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(suggestionId))
        {
            return false;
        }

        using var response = await _http.PostAsJsonAsync(
            $"v1/suggestions/{Uri.EscapeDataString(suggestionId)}/always",
            new { actor = "user", applyProjectId, ruleName },
            ct);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Force-fail any operator_runs left in <c>running</c> (Host crash / mid-push restart).</summary>
    public async Task<int> ClearStuckOperatorRunsAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(
                "v1/operator/runs/clear-stuck",
                new { },
                ct);
            if (!response.IsSuccessStatusCode)
            {
                return 0;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("abandoned", out var abandoned)
                && abandoned.TryGetInt32(out var n))
            {
                return n;
            }

            return 0;
        }
        catch
        {
            return 0;
        }
    }

    public async Task<OperatorDashboardVm?> GetOperatorDashboardAsync(CancellationToken ct = default)
    {
        try
        {
            using var runsResponse = await _http.GetAsync("v1/operator/runs", ct);
            using var rulesResponse = await _http.GetAsync("v1/operator/rules?enabledOnly=true", ct);
            using var memoryResponse = await _http.GetAsync("v1/operator/memory", ct);
            using var suggestionsResponse = await _http.GetAsync("v1/suggestions?status=pending", ct);

            var runs = runsResponse.IsSuccessStatusCode
                ? await DeserializeAsync<OperatorRunsDto>(runsResponse, ct)
                : null;
            var rules = rulesResponse.IsSuccessStatusCode
                ? await DeserializeAsync<OperatorRulesDto>(rulesResponse, ct)
                : null;
            var memory = memoryResponse.IsSuccessStatusCode
                ? await DeserializeAsync<OperatorMemoryDto>(memoryResponse, ct)
                : null;
            var suggestions = suggestionsResponse.IsSuccessStatusCode
                ? await DeserializeAsync<SuggestionListDto>(suggestionsResponse, ct)
                : null;

            return new OperatorDashboardVm
            {
                LatestBriefing = runs?.Runs?
                    .FirstOrDefault(r =>
                        !string.Equals(r.Status, "running", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(r.BriefingSummary))
                    ?.BriefingSummary,
                LatestRunStatus = runs?.Runs?.FirstOrDefault()?.Status,
                LatestTrigger = runs?.Runs?.FirstOrDefault()?.TriggerKind,
                LatestRunId = runs?.Runs?.FirstOrDefault()?.Id,
                LatestPayloadJson = runs?.Runs?.FirstOrDefault()?.TriggerPayloadJson,
                LatestCreatedAt = runs?.Runs?.FirstOrDefault()?.CreatedAt,
                RecentRuns = (runs?.Runs ?? [])
                    .Select(r => new OperatorRunVm
                    {
                        Id = r.Id ?? string.Empty,
                        TriggerKind = r.TriggerKind ?? string.Empty,
                        Status = r.Status ?? string.Empty,
                        BriefingSummary = r.BriefingSummary,
                        TriggerPayloadJson = r.TriggerPayloadJson,
                        CreatedAt = r.CreatedAt,
                    })
                    .ToList(),
                Rules = (rules?.Rules ?? [])
                    .Select(r => new OperatorRuleVm
                    {
                        Id = r.Id ?? string.Empty,
                        Name = r.Name ?? string.Empty,
                        TriggerKind = r.TriggerKind ?? string.Empty,
                        ActionKind = r.ActionKind ?? string.Empty,
                    })
                    .ToList(),
                Memory = (memory?.Memory ?? [])
                    .Select(m => new OperatorMemoryVm
                    {
                        Id = m.Id ?? string.Empty,
                        Scope = m.Scope ?? "global",
                        Kind = m.Kind ?? string.Empty,
                        Text = m.Text ?? string.Empty,
                    })
                    .ToList(),
                PendingSuggestions = (suggestions?.Suggestions ?? [])
                    .Where(s => !string.IsNullOrWhiteSpace(s.Id))
                    .Where(s => IsMergeApproval(s.SuggestionType))
                    .Select(s => new PendingSuggestionVm
                    {
                        Id = s.Id!,
                        SuggestionType = s.SuggestionType ?? string.Empty,
                        Summary = s.Summary ?? string.Empty,
                        ProjectId = s.ProjectId,
                        TaskId = s.TaskId,
                        PayloadJson = s.PayloadJson,
                        Confidence = s.Confidence,
                    })
                    .ToList(),
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Approve merges, not thinking — hide limbo "please look" chores.</summary>
    private static bool IsMergeApproval(string? suggestionType)
    {
        if (string.IsNullOrWhiteSpace(suggestionType))
        {
            return false;
        }

        return suggestionType switch
        {
            "review_limbo" => false,
            "assign_to_project" => true,
            "assign_project" => true,
            "merge_into_task" => true,
            "disambiguate_email_claim" => true,
            "link_tasks" => true,
            "contact_merge" => true,
            "reporting_relationship" => true,
            "dependency_ready" => true,
            _ => true,
        };
    }

    private async Task<T?> DeserializeAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
    }

    public async Task<bool> RejectSuggestionAsync(string suggestionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(suggestionId))
        {
            return false;
        }

        using var response = await _http.PostAsJsonAsync(
            $"v1/suggestions/{Uri.EscapeDataString(suggestionId)}/reject",
            new { actor = "user" },
            ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<TaskLinksVm> GetTaskDependenciesAsync(string taskId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return new TaskLinksVm();
        }

        using var response = await _http.GetAsync(
            $"v1/agent/tools/orbit_get_task_dependencies?taskId={Uri.EscapeDataString(taskId)}",
            ct);
        if (!response.IsSuccessStatusCode)
        {
            return new TaskLinksVm();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<TaskDependenciesDto>(stream, JsonOptions, ct);
        return new TaskLinksVm
        {
            WaitingOn = (dto?.WaitingOn ?? []).Select(MapEdge).ToList(),
            Feeds = (dto?.Feeds ?? []).Select(MapEdge).ToList(),
        };
    }

    public async Task<bool> LinkTasksAsync(
        string predecessorTaskId,
        string successorTaskId,
        string dependencyType,
        string? expects = null,
        string? reason = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(predecessorTaskId) || string.IsNullOrWhiteSpace(successorTaskId))
        {
            return false;
        }

        using var response = await _http.PostAsJsonAsync(
            "v1/agent/tools/orbit_link_tasks",
            new
            {
                predecessorTaskId,
                successorTaskId,
                dependencyType,
                expects,
                reason,
                actor = "user",
            },
            ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> UnlinkTasksAsync(string dependencyId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dependencyId))
        {
            return false;
        }

        using var response = await _http.PostAsJsonAsync(
            "v1/agent/tools/orbit_unlink_tasks",
            new { dependencyId, actor = "user" },
            ct);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Asks the host to re-run link detection for a task, returning any fresh proposals.</summary>
    public async Task<int> SuggestTaskLinksAsync(string taskId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return 0;
        }

        using var response = await _http.PostAsJsonAsync(
            "v1/agent/tools/orbit_suggest_task_links",
            new { id = taskId },
            ct);
        if (!response.IsSuccessStatusCode)
        {
            return 0;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<SuggestLinksDto>(stream, JsonOptions, ct);
        return dto?.Suggestions?.Length ?? 0;
    }

    public async Task<IReadOnlyList<PendingSuggestionVm>> GetPendingSuggestionsAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("v1/suggestions?status=pending", ct);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<SuggestionListDto>(stream, JsonOptions, ct);
        return (dto?.Suggestions ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s.Id))
            .Select(s => new PendingSuggestionVm
            {
                Id = s.Id!,
                SuggestionType = s.SuggestionType ?? string.Empty,
                Summary = s.Summary ?? string.Empty,
                ProjectId = s.ProjectId,
                TaskId = s.TaskId,
                Confidence = s.Confidence,
                PayloadJson = s.PayloadJson,
            })
            .ToList();
    }

    private static TaskLinkVm MapEdge(TaskDependencyEdgeDto dto) => new()
    {
        DependencyId = dto.DependencyId ?? string.Empty,
        DependencyType = dto.DependencyType ?? string.Empty,
        TaskId = dto.TaskId ?? string.Empty,
        Title = dto.Title ?? string.Empty,
        Status = dto.Status ?? string.Empty,
        NextAction = dto.NextAction,
        Expects = dto.Expects,
        Reason = dto.Reason,
        Satisfied = dto.Satisfied,
    };

    private sealed class TaskDependenciesDto
    {
        public TaskDependencyEdgeDto[]? WaitingOn { get; set; }

        public TaskDependencyEdgeDto[]? Feeds { get; set; }
    }

    private sealed class TaskDependencyEdgeDto
    {
        public string? DependencyId { get; set; }

        public string? DependencyType { get; set; }

        public string? TaskId { get; set; }

        public string? Title { get; set; }

        public string? Status { get; set; }

        public string? NextAction { get; set; }

        public string? Expects { get; set; }

        public string? Reason { get; set; }

        public bool Satisfied { get; set; }
    }

    private sealed class SuggestLinksDto
    {
        public object[]? Suggestions { get; set; }
    }

    private sealed class SuggestionListDto
    {
        public PendingSuggestionDto[]? Suggestions { get; set; }
    }

    private sealed class PendingSuggestionDto
    {
        public string? Id { get; set; }

        public string? SuggestionType { get; set; }

        public string? Summary { get; set; }

        public string? ProjectId { get; set; }

        public string? TaskId { get; set; }

        public string? PayloadJson { get; set; }

        public double? Confidence { get; set; }
    }

    public async Task SubscribeCalendarIcsAsync(string pathOrUrl, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(
            "v1/calendar/sources/subscribe",
            new { path = pathOrUrl },
            ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<string?> SyncCalendarAsync(CancellationToken ct = default)
    {
        using var response = await _http.PostAsync("v1/calendar/sync", content: null, ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            return $"Sync failed ({(int)response.StatusCode}): {err}";
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;
        var sources = root.TryGetProperty("sourcesUpserted", out var s) ? s.GetInt32() : 0;
        var events = root.TryGetProperty("eventsUpserted", out var e) ? e.GetInt32() : 0;
        var statuses = root.TryGetProperty("providerStatuses", out var p) && p.ValueKind == JsonValueKind.Array
            ? string.Join("; ", p.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)))
            : string.Empty;
        return $"Synced {sources} source(s), {events} event(s). {statuses}".Trim();
    }

    public async Task<string?> GetCalendarSourcesSummaryAsync(CancellationToken ct = default)
    {
        var sources = await ListCalendarSourcesAsync(ct);
        if (sources is null)
        {
            return null;
        }

        if (sources.Count == 0)
        {
            return "Calendar sources: (none)";
        }

        var lines = new List<string> { "Calendar sources:" };
        foreach (var src in sources)
        {
            var identity = string.Join(" / ", new[] { src.MailboxName, src.CalendarName }.Where(x => !string.IsNullOrWhiteSpace(x)));
            var label = string.IsNullOrWhiteSpace(identity) ? src.Name : $"{src.Name} ({identity})";
            var included = src.Enabled ? "included" : "excluded";
            var line = $"- [{src.Provider}] {label} — {included} · {src.LastSyncStatus ?? "unknown"}";
            if (!string.IsNullOrWhiteSpace(src.LastSyncError))
            {
                line += $" ({src.LastSyncError})";
            }

            lines.Add(line);
        }

        return string.Join("\n", lines);
    }

    public async Task<IReadOnlyList<CalendarSourceVm>?> ListCalendarSourcesAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("v1/calendar/sources", ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<CalendarSourcesEnvelopeDto>(stream, JsonOptions, ct);
        return (dto?.Sources ?? [])
            .Select(s => new CalendarSourceVm
            {
                Id = s.Id ?? string.Empty,
                Name = s.Name ?? string.Empty,
                Provider = s.Provider,
                MailboxName = s.MailboxName,
                CalendarName = s.CalendarName,
                Enabled = s.Enabled,
                LastSyncStatus = s.LastSyncStatus,
                LastSyncError = s.LastSyncError,
            })
            .Where(s => !string.IsNullOrWhiteSpace(s.Id))
            .ToList();
    }

    public async Task<bool> SetCalendarSourceEnabledAsync(string sourceId, bool enabled, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return false;
        }

        using var response = await _http.PatchAsJsonAsync(
            $"v1/calendar/sources/{Uri.EscapeDataString(sourceId)}",
            new { enabled },
            ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<string?> CreateSyncSnapshotAsync(CancellationToken ct = default)
    {
        using var response = await _http.PostAsync("v1/sync/snapshot", content: null, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            return $"Snapshot failed ({(int)response.StatusCode}): {body}";
        }

        using var doc = JsonDocument.Parse(body);
        string? id = null;
        var rev = "?";
        if (doc.RootElement.TryGetProperty("snapshot", out var snap))
        {
            id = snap.TryGetProperty("snapshotId", out var sid) ? sid.GetString() : null;
            if (snap.TryGetProperty("revision", out var r))
            {
                rev = r.GetInt64().ToString();
            }
        }

        return $"Created snapshot {id} (revision {rev}).";
    }

    public async Task<string?> RestoreSyncSnapshotAsync(string snapshotId, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(
            "v1/sync/restore",
            new { snapshotId },
            ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            return $"Restore failed ({(int)response.StatusCode}): {body}";
        }

        return $"Restored snapshot {snapshotId}.";
    }

    public async Task<string?> GetSyncStatusSummaryAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("v1/sync/status", ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("status", out var status))
        {
            return "Sync status: (unknown)";
        }

        var kind = status.TryGetProperty("kind", out var k) ? k.GetString() : "Unknown";
        var message = status.TryGetProperty("message", out var m) ? m.GetString() : string.Empty;
        var local = status.TryGetProperty("localRevision", out var lr) ? lr.GetInt64() : 0;
        var cloud = status.TryGetProperty("latestCloudRevision", out var cr) && cr.ValueKind != JsonValueKind.Null
            ? cr.GetInt64().ToString()
            : "—";
        var conflict = status.TryGetProperty("conflict", out var c) && c.ValueKind == JsonValueKind.Object
            ? c.TryGetProperty("message", out var cm) ? cm.GetString() : "conflict"
            : null;

        var line = $"Sync: {kind} — {message} (local rev {local}, cloud rev {cloud})";
        if (!string.IsNullOrWhiteSpace(conflict))
        {
            line += "\nConflict: " + conflict;
        }

        return line;
    }

    /// <summary>Exports a redacted diagnostics JSON or zip under the Host generated root.</summary>
    public async Task<string?> ExportDiagnosticsAsync(string format = "json", CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(
            "v1/diagnostics/export",
            new { format },
            ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            return $"Diagnostics export failed ({(int)response.StatusCode}): {body}";
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var path = doc.RootElement.TryGetProperty("path", out var p) ? p.GetString() : null;
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Diagnostics export finished but no path was returned.";
        }

        return $"Diagnostics exported (redacted): {path}";
    }

    public async Task<IReadOnlyList<SyncSnapshotListItem>> ListSyncSnapshotsAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("v1/sync/snapshots", ct);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("snapshots", out var snaps) || snaps.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<SyncSnapshotListItem>();
        foreach (var snap in snaps.EnumerateArray())
        {
            var id = snap.TryGetProperty("snapshotId", out var sid) ? sid.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var rev = snap.TryGetProperty("revision", out var r) ? r.GetInt64() : 0;
            var device = snap.TryGetProperty("deviceName", out var d) ? d.GetString() : null;
            var created = snap.TryGetProperty("createdAt", out var c) ? c.GetString() : null;
            list.Add(new SyncSnapshotListItem
            {
                SnapshotId = id!,
                Display = $"rev {rev} — {id} — {device} — {created}",
            });
        }

        return list;
    }

    public async Task<IReadOnlyList<string>> GetUpcomingMeetingLinesAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("v1/calendar/context?days=7&limit=5", ct);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("meetings", out var meetings) || meetings.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var lines = new List<string>();
        foreach (var m in meetings.EnumerateArray())
        {
            var title = m.TryGetProperty("title", out var t) ? t.GetString() : null;
            var starts = m.TryGetProperty("startsAt", out var s) ? s.GetString() : null;
            var source = m.TryGetProperty("sourceName", out var sn) ? sn.GetString() : null;
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var when = string.IsNullOrWhiteSpace(starts) ? "" : " · " + starts;
            var from = string.IsNullOrWhiteSpace(source) ? "" : " · " + source;
            lines.Add($"{title}{when}{from}");
        }

        return lines;
    }

    public async Task<RemoteActivityVm?> GetRemoteActivityAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("v1/activity/remote?conversationLimit=10&auditLimit=20", ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<RemoteActivityDto>(stream, JsonOptions, ct);
        if (dto is null)
        {
            return null;
        }

        return new RemoteActivityVm
        {
            Conversations = (dto.Conversations ?? []).Select(c => new RemoteConversationVm
            {
                Id = c.Id ?? string.Empty,
                Title = string.IsNullOrWhiteSpace(c.Title) ? "Telegram session" : c.Title!,
                HermesSessionId = c.HermesSessionId,
                UpdatedAt = c.UpdatedAt ?? string.Empty,
                SummaryLine = FormatRemoteConversation(c),
            }).ToList(),
            Changes = (dto.AuditEvents ?? []).Select(a => new RemoteChangeVm
            {
                Id = a.Id ?? string.Empty,
                Summary = string.IsNullOrWhiteSpace(a.Summary) ? (a.EventType ?? "change") : a.Summary!,
                DetailLine = FormatRemoteChange(a),
                ConversationHint = a.HermesSessionId,
            }).ToList(),
        };
    }

    private static string FormatRemoteConversation(RemoteConversationDto c)
    {
        var title = string.IsNullOrWhiteSpace(c.Title) ? "Telegram" : c.Title;
        var session = string.IsNullOrWhiteSpace(c.HermesSessionId) ? "no session" : c.HermesSessionId;
        var when = string.IsNullOrWhiteSpace(c.UpdatedAt) ? string.Empty : $" · {c.UpdatedAt}";
        return $"{title} · {session}{when}";
    }

    private static string FormatRemoteChange(RemoteAuditEventDto a)
    {
        var summary = string.IsNullOrWhiteSpace(a.Summary) ? a.EventType : a.Summary;
        var actor = string.IsNullOrWhiteSpace(a.Actor) ? string.Empty : $" · {a.Actor}";
        var when = string.IsNullOrWhiteSpace(a.CreatedAt) ? string.Empty : $" · {a.CreatedAt}";
        return $"{summary}{actor}{when}";
    }

    public async Task<CellLineVm?> GetTaskAsync(string taskId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return null;
        }

        using var response = await _http.GetAsync($"v1/tasks/{Uri.EscapeDataString(taskId.Trim())}", ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<TaskByIdDto>(stream, JsonOptions, ct);
        if (dto is null || string.IsNullOrWhiteSpace(dto.TaskId))
        {
            return null;
        }

        return new CellLineVm
        {
            TaskId = dto.TaskId!,
            ProjectId = dto.ProjectId,
            Title = dto.Title ?? string.Empty,
            Status = dto.Status ?? string.Empty,
            NextAction = dto.NextAction,
            Body = dto.Body,
            DueAt = dto.DueAt,
            Priority = dto.Priority,
            Urgency = dto.Urgency,
            SourceKind = dto.SourceKind,
            SourceConfidence = dto.SourceConfidence,
            SourceMatchReason = dto.SourceMatchReason,
        };
    }

    public async Task<LimboNoteVm?> GetLimboNoteAsync(string noteId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(noteId))
        {
            return null;
        }

        using var response = await _http.GetAsync($"v1/notes/limbo/{Uri.EscapeDataString(noteId.Trim())}", ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<LimboNoteByIdDto>(stream, JsonOptions, ct);
        if (dto is null || string.IsNullOrWhiteSpace(dto.Id))
        {
            return null;
        }

        return new LimboNoteVm
        {
            Id = dto.Id!,
            OriginalText = dto.OriginalText ?? string.Empty,
            CreatedAt = dto.CreatedAt ?? string.Empty,
            SuggestionId = dto.SuggestionId,
            SuggestionSummary = dto.SuggestionSummary,
        };
    }

    public async Task<ProjectContextVm?> GetProjectContextAsync(string projectId, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"v1/projects/{Uri.EscapeDataString(projectId)}/context", ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<ProjectContextDto>(stream, JsonOptions, ct);
        if (dto is null)
        {
            return null;
        }

        return new ProjectContextVm
        {
            Id = dto.Id ?? projectId,
            Name = dto.Name ?? string.Empty,
            Summary = dto.Summary,
            Code = dto.Code,
            Dossier = dto.Dossier is null
                ? null
                : new ProjectDossierVm
                {
                    Version = dto.Dossier.Version,
                    Address = dto.Dossier.Address,
                    OwnerClient = dto.Dossier.OwnerClient,
                    Phase = dto.Dossier.Phase,
                    Portfolio = dto.Dossier.Portfolio,
                    LinkedFolder = dto.Dossier.LinkedFolder,
                    CurrentPriorities = dto.Dossier.CurrentPriorities ?? [],
                    MailboxSources = dto.Dossier.MailboxSources ?? [],
                    CalendarSources = dto.Dossier.CalendarSources ?? [],
                    Empty = dto.Dossier.Empty,
                },
            DossierEmpty = dto.DossierEmpty || dto.Dossier is null || dto.Dossier.Empty,
            Aliases = (dto.Aliases ?? [])
                .Where(a => !string.IsNullOrWhiteSpace(a.Id) && !string.IsNullOrWhiteSpace(a.Alias))
                .Select(a => new ProjectAliasVm { Id = a.Id!, Alias = a.Alias! })
                .ToList(),
            Tasks = (dto.Tasks ?? []).Select(t => new CellLineVm
            {
                TaskId = t.TaskId ?? string.Empty,
                Title = t.Title ?? string.Empty,
                Status = t.Status ?? string.Empty,
                NextAction = t.NextAction,
                Body = t.Body,
                DueAt = t.DueAt,
                Priority = t.Priority,
                Urgency = t.Urgency,
            }).ToList(),
            CompletedTasks = (dto.CompletedTasks ?? []).Select(t => new CellLineVm
            {
                TaskId = t.TaskId ?? string.Empty,
                Title = t.Title ?? string.Empty,
                Status = t.Status ?? string.Empty,
                NextAction = t.NextAction,
                Body = t.Body,
                DueAt = t.DueAt,
                Priority = t.Priority,
                Urgency = t.Urgency,
            }).ToList(),
            Notes = (dto.Notes ?? [])
                .Where(n => !string.IsNullOrWhiteSpace(n.Id) && !string.IsNullOrWhiteSpace(n.OriginalText))
                .Select(n => new ContextNoteVm
                {
                    Id = n.Id!,
                    Text = n.OriginalText!,
                    CreatedAt = n.CreatedAt ?? string.Empty,
                })
                .ToList(),
            Blockers = (dto.Blockers ?? []).Select(b => b.Summary ?? string.Empty).Where(s => s.Length > 0).ToList(),
            Contacts = (dto.Contacts ?? [])
                .Where(c => !string.IsNullOrWhiteSpace(c.PersonId) || !string.IsNullOrWhiteSpace(c.DisplayName))
                .Select(c => new ContextContactVm
                {
                    PersonId = c.PersonId ?? string.Empty,
                    DisplayName = c.DisplayName ?? string.Empty,
                    Title = c.Title,
                    OrganizationName = c.OrganizationName,
                })
                .Where(c => c.DisplayName.Length > 0)
                .ToList(),
            Meetings = (dto.Meetings ?? []).Select(m =>
            {
                var title = m.Title ?? "Meeting";
                return string.IsNullOrWhiteSpace(m.StartsAt) ? title : $"{title} · {m.StartsAt}";
            }).ToList(),
            Suggestions = (dto.Suggestions ?? [])
                .Where(s => !string.IsNullOrWhiteSpace(s.Id) && !string.IsNullOrWhiteSpace(s.Summary))
                .Select(s => new ContextSuggestionVm
                {
                    Id = s.Id!,
                    Summary = s.Summary!,
                    Status = s.Status ?? "pending",
                })
                .ToList(),
            Files = (dto.Files ?? [])
                .Where(f => !string.IsNullOrWhiteSpace(f.Id))
                .Select(f => new ContextFileVm
                {
                    Id = f.Id!,
                    DisplayName = f.DisplayName ?? f.Path ?? f.Id!,
                    Path = f.Path ?? string.Empty,
                })
                .ToList(),
        };
    }

    public async Task<CaptureResponseVm?> AssignLimboNoteAsync(
        string noteId,
        string projectId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(noteId) || string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        using var response = await _http.PostAsJsonAsync(
            $"v1/notes/{Uri.EscapeDataString(noteId)}/assign",
            new { projectId },
            ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<CaptureDto>(stream, JsonOptions, ct);
        if (dto is null)
        {
            return null;
        }

        return new CaptureResponseVm
        {
            NoteId = dto.NoteId ?? noteId,
            TaskId = dto.TaskId,
            OriginalText = dto.OriginalText ?? string.Empty,
            ProjectId = dto.ProjectId ?? projectId,
            IsLimbo = dto.IsLimbo,
        };
    }

    public async Task<IReadOnlyList<Views.ProjectItem>> GetProjectsAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("v1/projects", ct);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<ProjectsDto>(stream, JsonOptions, ct);
        return (dto?.Projects ?? [])
            .Select(p => new Views.ProjectItem { Id = p.Id ?? string.Empty, Name = p.Name ?? string.Empty })
            .Where(p => p.Id.Length > 0)
            .ToList();
    }

    public async Task<bool> SetProjectAccentAsync(string projectId, string? accentColor, CancellationToken ct = default)
    {
        using var response = await _http.PatchAsJsonAsync(
            $"v1/projects/{Uri.EscapeDataString(projectId)}/accent",
            new { accentColor },
            ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> SetWorkbenchCellLayoutAsync(
        string entityId,
        string cellKind,
        double x,
        double y,
        double width,
        double height,
        int sortOrder = 0,
        CancellationToken ct = default)
    {
        using var response = await _http.PatchAsJsonAsync(
            $"v1/workbench/cells/{Uri.EscapeDataString(entityId)}/layout",
            new { cellKind, x, y, width, height, sortOrder },
            ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<IReadOnlyList<Views.FolderItem>> GetProjectFoldersAsync(string projectId, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"v1/projects/{Uri.EscapeDataString(projectId)}/folders", ct);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<FoldersDto>(stream, JsonOptions, ct);
        return (dto?.Folders ?? [])
            .Select(f => new Views.FolderItem
            {
                Id = f.Id ?? string.Empty,
                RootPath = f.RootPath ?? string.Empty,
                IsHome = f.IsHome,
            })
            .Where(f => f.Id.Length > 0)
            .ToList();
    }

    public async Task<ProjectHomeFolderResult?> GetProjectHomeFolderAsync(
        string projectId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        using var response = await _http.GetAsync(
            $"v1/projects/{Uri.EscapeDataString(projectId)}/home-folder",
            ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<HomeFolderResponseDto>(stream, JsonOptions, ct);
        if (dto?.Home is null || string.IsNullOrWhiteSpace(dto.Home.Id))
        {
            return null;
        }

        return new ProjectHomeFolderResult
        {
            Id = dto.Home.Id!,
            RootPath = dto.Home.RootPath ?? string.Empty,
            OrbitSandboxPath = dto.Home.OrbitSandboxPath,
            Availability = dto.Home.Availability,
        };
    }

    public async Task<ProjectHomeFolderResult?> SetProjectHomeFolderAsync(
        string projectId,
        string path,
        CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(
            $"v1/projects/{Uri.EscapeDataString(projectId)}/home-folder",
            new { path },
            ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<SetHomeFolderDto>(stream, JsonOptions, ct);
        if (dto is null || string.IsNullOrWhiteSpace(dto.Id))
        {
            return null;
        }

        return new ProjectHomeFolderResult
        {
            Id = dto.Id!,
            RootPath = dto.RootPath ?? path,
            OrbitSandboxPath = dto.OrbitSandboxPath,
            Availability = dto.Availability,
            IndexedCount = dto.IndexedCount,
        };
    }

    public async Task<AttachFolderResult?> AttachProjectFolderAsync(string projectId, string path, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(
            $"v1/projects/{Uri.EscapeDataString(projectId)}/folders",
            new { path },
            ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<AttachFolderDto>(stream, JsonOptions, ct);
        return dto is null
            ? null
            : new AttachFolderResult
            {
                IndexedCount = dto.IndexedCount,
                Reindex = MapReindexSummary(dto.Reindex, dto.IndexedCount),
            };
    }

    public async Task<ReindexFolderResult> ReindexFolderAsync(string projectId, string folderId, CancellationToken ct = default)
    {
        using var response = await _http.PostAsync(
            $"v1/projects/{Uri.EscapeDataString(projectId)}/folders/{Uri.EscapeDataString(folderId)}/reindex",
            content: null,
            ct);
        if (!response.IsSuccessStatusCode)
        {
            return new ReindexFolderResult();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<ReindexDto>(stream, JsonOptions, ct);
        return MapReindexSummary(dto?.Reindex, dto?.IndexedCount ?? 0);
    }

    public async Task<IReadOnlyList<Views.FileHitItem>> SearchFilesAsync(string query, string? projectId, CancellationToken ct = default)
    {
        var url = string.IsNullOrWhiteSpace(query)
            ? "v1/files/search?"
            : $"v1/files/search?q={Uri.EscapeDataString(query)}&";
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            url += $"projectId={Uri.EscapeDataString(projectId)}";
        }
        else
        {
            url = url.TrimEnd('&', '?');
        }

        using var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<SearchDto>(stream, JsonOptions, ct);
        return (dto?.Results ?? [])
            .Select(r => new Views.FileHitItem
            {
                Id = r.Id ?? string.Empty,
                DisplayName = r.DisplayName ?? string.Empty,
                Path = r.Path ?? string.Empty,
                RelativePath = r.Path ?? string.Empty,
            })
            .Where(r => r.Id.Length > 0)
            .ToList();
    }

    public async Task<IReadOnlyList<Views.SearchHitItem>> GlobalSearchAsync(
        string query,
        string? focusProjectId = null,
        CancellationToken ct = default)
    {
        var url = $"v1/search?q={Uri.EscapeDataString(query)}";
        if (!string.IsNullOrWhiteSpace(focusProjectId))
        {
            url += $"&focusProjectId={Uri.EscapeDataString(focusProjectId)}";
        }

        using var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<GlobalSearchDto>(stream, JsonOptions, ct);
        return (dto?.Results ?? [])
            .Select(r => new Views.SearchHitItem
            {
                EntityType = r.EntityType ?? string.Empty,
                EntityId = r.EntityId ?? string.Empty,
                Title = r.Title ?? string.Empty,
                Snippet = r.Snippet ?? string.Empty,
                Score = r.Score,
                ProjectId = r.ProjectId,
                Path = r.Path,
            })
            .Where(r => r.EntityId.Length > 0)
            .ToList();
    }

    public async Task<EmailDetailVm?> GetEmailAsync(string emailId, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"v1/emails/{Uri.EscapeDataString(emailId)}", ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<EmailDetailDto>(stream, JsonOptions, ct);
        if (dto is null)
        {
            return null;
        }

        return new EmailDetailVm
        {
            Id = dto.Id ?? string.Empty,
            Subject = dto.Subject,
            BodyPreview = dto.BodyPreview,
            SentAt = dto.SentAt,
        };
    }

    public Task<IReadOnlyList<Views.FileHitItem>> ListProjectFilesAsync(string projectId, CancellationToken ct = default) =>
        SearchFilesAsync(string.Empty, projectId, ct);

    public async Task<string?> PreviewFileAsync(string fileId, CancellationToken ct = default)
    {
        using var response = await _http.PostAsync($"v1/files/{Uri.EscapeDataString(fileId)}/preview", content: null, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<PreviewDto>(stream, JsonOptions, ct);
        return dto?.PreviewText;
    }

    public async Task<bool> OpenFileExternallyAsync(string fileId, CancellationToken ct = default)
    {
        using var response = await _http.PostAsync($"v1/files/{Uri.EscapeDataString(fileId)}/open", content: null, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<EmailIngestResult?> IngestEmailAsync(
        string path,
        IReadOnlyList<string>? projectIds = null,
        string? memo = null,
        CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(
            "v1/emails/ingest",
            new { path, projectIds = projectIds ?? Array.Empty<string>(), memo },
            ct);
        if (!response.IsSuccessStatusCode)
        {
            LastEmailIngestError = await ReadErrorMessageAsync(response, ct)
                ?? $"Ingest HTTP {(int)response.StatusCode}";
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<EmailIngestDto>(stream, JsonOptions, ct);
        if (dto is null)
        {
            LastEmailIngestError = "Ingest returned an empty body.";
            return null;
        }

        LastEmailIngestError = null;

        return new EmailIngestResult
        {
            Id = dto.Id ?? string.Empty,
            Subject = dto.Subject,
            SentAt = dto.SentAt,
            InternetMessageId = dto.InternetMessageId,
            ConversationId = dto.ConversationId,
            BodyPreview = dto.BodyPreview,
            RawPath = dto.RawPath,
            WasExisting = dto.WasExisting,
            Participants = (dto.Participants ?? [])
                .Select(p => new EmailParticipantResult
                {
                    Role = p.Role ?? string.Empty,
                    Address = p.Address ?? string.Empty,
                    DisplayName = p.DisplayName,
                })
                .ToList(),
            ProjectIds = dto.ProjectIds ?? [],
            Attachments = (dto.Attachments ?? [])
                .Select(a => new EmailAttachmentResult
                {
                    FileName = a.FileName ?? string.Empty,
                    Path = a.Path ?? string.Empty,
                    SizeBytes = a.SizeBytes,
                })
                .ToList(),
        };
    }

    public async Task<bool> OpenEmailInOutlookAsync(string emailId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(emailId))
        {
            return false;
        }

        using var response = await _http.PostAsync(
            $"v1/emails/{Uri.EscapeDataString(emailId)}/open",
            content: null,
            ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<IReadOnlyList<TaskEmailThreadVm>> GetTaskEmailThreadsAsync(
        string taskId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return [];
        }

        using var response = await _http.GetAsync(
            $"v1/tasks/{Uri.EscapeDataString(taskId)}/email-threads",
            ct);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<TaskEmailThreadsDto>(stream, JsonOptions, ct);
        return (dto?.Threads ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t.Id))
            .Select(t => new TaskEmailThreadVm
            {
                Id = t.Id!,
                TaskId = t.TaskId ?? taskId,
                ConversationId = t.ConversationId ?? string.Empty,
                AnchorEmailId = t.AnchorEmailId,
                Subject = t.Subject,
                LatestSentAt = t.LatestSentAt,
                MessageCount = t.MessageCount,
            })
            .ToList();
    }

    public async Task<bool> LinkEmailThreadAsync(
        string taskId,
        string conversationId,
        string? anchorEmailId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(conversationId))
        {
            return false;
        }

        using var response = await _http.PostAsJsonAsync(
            $"v1/tasks/{Uri.EscapeDataString(taskId)}/email-threads",
            new { conversationId, anchorEmailId, actor = "user" },
            ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<IReadOnlyList<ContactListResult>> GetContactsAsync(
        string? category = null,
        string? disposition = null,
        CancellationToken ct = default)
    {
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(category))
        {
            qs.Add($"category={Uri.EscapeDataString(category)}");
        }

        if (!string.IsNullOrWhiteSpace(disposition))
        {
            qs.Add($"disposition={Uri.EscapeDataString(disposition)}");
        }

        var path = qs.Count == 0 ? "v1/contacts" : "v1/contacts?" + string.Join("&", qs);
        using var response = await _http.GetAsync(path, ct);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<ContactsListDto>(stream, JsonOptions, ct);
        return (dto?.Contacts ?? [])
            .Select(c => new ContactListResult
            {
                Id = c.Id ?? string.Empty,
                DisplayName = c.DisplayName ?? string.Empty,
                Title = c.Title,
                OrganizationName = c.OrganizationName,
                PrimaryEmail = c.PrimaryEmail,
                PrimaryPhone = c.PrimaryPhone,
                Category = c.Category,
                Disposition = c.Disposition ?? "active",
            })
            .Where(c => c.Id.Length > 0)
            .ToList();
    }

    public async Task<ContactDetailResult?> GetContactAsync(string contactId, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"v1/contacts/{Uri.EscapeDataString(contactId)}", ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<ContactDetailDto>(stream, JsonOptions, ct);
        if (dto is null)
        {
            return null;
        }

        return new ContactDetailResult
        {
            Id = dto.Id ?? contactId,
            DisplayName = dto.DisplayName ?? string.Empty,
            Title = dto.Title,
            OrganizationId = dto.OrganizationId,
            OrganizationName = dto.OrganizationName,
            Category = dto.Category,
            Disposition = dto.Disposition ?? "active",
            ReportsToPersonId = dto.ReportsToPersonId,
            ReportsToDisplayName = dto.ReportsToDisplayName,
            Methods = (dto.Methods ?? [])
                .Select(m => new ContactMethodResult
                {
                    MethodType = m.MethodType ?? string.Empty,
                    Value = m.Value ?? string.Empty,
                })
                .ToList(),
            Projects = (dto.Projects ?? [])
                .Select(p => new ContactProjectResult
                {
                    Id = p.Id ?? string.Empty,
                    Name = p.Name ?? string.Empty,
                })
                .Where(p => p.Id.Length > 0)
                .ToList(),
            RecentEmails = (dto.RecentEmails ?? [])
                .Select(e => new ContactEmailResult
                {
                    Id = e.Id ?? string.Empty,
                    Subject = e.Subject,
                    SentAt = e.SentAt,
                    Role = e.Role,
                })
                .ToList(),
            Provenance = (dto.Provenance ?? [])
                .Select(p => new ContactProvenanceResult
                {
                    Field = p.Field ?? string.Empty,
                    Value = p.Value ?? string.Empty,
                    SourceKind = p.SourceKind ?? string.Empty,
                    SourceEmailId = p.SourceEmailId,
                })
                .ToList(),
        };
    }

    public async Task<ContactDetailResult?> UpdateContactAsync(
        string contactId,
        object patch,
        string? provenance = null,
        string? requestedBy = null,
        CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(
            $"v1/contacts/{Uri.EscapeDataString(contactId)}",
            new { patch, provenance, requestedBy },
            ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<ContactDetailDto>(stream, JsonOptions, ct);
        if (dto is null)
        {
            return null;
        }

        return new ContactDetailResult
        {
            Id = dto.Id ?? contactId,
            DisplayName = dto.DisplayName ?? string.Empty,
            Title = dto.Title,
            OrganizationId = dto.OrganizationId,
            OrganizationName = dto.OrganizationName,
            Category = dto.Category,
            Disposition = dto.Disposition ?? "active",
            ReportsToPersonId = dto.ReportsToPersonId,
            ReportsToDisplayName = dto.ReportsToDisplayName,
            Methods = (dto.Methods ?? [])
                .Select(m => new ContactMethodResult
                {
                    MethodType = m.MethodType ?? string.Empty,
                    Value = m.Value ?? string.Empty,
                })
                .ToList(),
            Projects = (dto.Projects ?? [])
                .Select(p => new ContactProjectResult
                {
                    Id = p.Id ?? string.Empty,
                    Name = p.Name ?? string.Empty,
                })
                .Where(p => p.Id.Length > 0)
                .ToList(),
            RecentEmails = (dto.RecentEmails ?? [])
                .Select(e => new ContactEmailResult
                {
                    Id = e.Id ?? string.Empty,
                    Subject = e.Subject,
                    SentAt = e.SentAt,
                    Role = e.Role,
                })
                .ToList(),
            Provenance = (dto.Provenance ?? [])
                .Select(p => new ContactProvenanceResult
                {
                    Field = p.Field ?? string.Empty,
                    Value = p.Value ?? string.Empty,
                    SourceKind = p.SourceKind ?? string.Empty,
                    SourceEmailId = p.SourceEmailId,
                })
                .ToList(),
        };
    }

    public async Task<bool> ArchiveContactAsync(
        string contactId,
        bool excludeAsResident = false,
        string? provenance = null,
        string? requestedBy = null,
        CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(
            $"v1/contacts/{Uri.EscapeDataString(contactId)}/archive",
            new { excludeAsResident, provenance, requestedBy },
            ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<PulseVm?> GetPulseAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("v1/pulse", ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<PulseEnvelopeDto>(stream, JsonOptions, ct);
        return dto?.Pulse is null ? null : MapPulse(dto.Pulse);
    }

    public async Task<PulseVm?> RefreshPulseAsync(CancellationToken ct = default)
    {
        using var response = await _http.PostAsync("v1/pulse/refresh", content: null, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<PulseEnvelopeDto>(stream, JsonOptions, ct);
        return dto?.Pulse is null ? null : MapPulse(dto.Pulse);
    }

    public async Task<OrbitVm?> GetOrbitAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("v1/orbit", ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<OrbitEnvelopeDto>(stream, JsonOptions, ct);
        if (dto is null)
        {
            return null;
        }

        return new OrbitVm
        {
            IgnitionCompleted = dto.IgnitionCompleted,
            Projects = (dto.Projects ?? []).Select(MapOrbitProject).ToList(),
        };
    }

    public async Task<IReadOnlyList<IgnitionProjectVm>?> IgnitionFromListAsync(
        IReadOnlyList<string> names,
        CancellationToken ct = default)
    {
        if (names.Count == 0)
        {
            return null;
        }

        using var response = await _http.PostAsJsonAsync(
            "v1/orbit/ignition/from-list",
            new { names },
            ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<IgnitionProjectsEnvelopeDto>(stream, JsonOptions, ct);
        return (dto?.Projects ?? []).Select(MapIgnitionProject).ToList();
    }

    public async Task<IReadOnlyList<IgnitionProjectVm>?> IgnitionFromProjectsRootAsync(
        string rootPath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }

        using var response = await _http.PostAsJsonAsync(
            "v1/orbit/ignition/from-projects-root",
            new { rootPath },
            ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<IgnitionProjectsEnvelopeDto>(stream, JsonOptions, ct);
        return (dto?.Projects ?? []).Select(MapIgnitionProject).ToList();
    }

    public async Task<IgnitionConfirmVm?> IgnitionConfirmAsync(CancellationToken ct = default)
    {
        using var response = await _http.PostAsync("v1/orbit/ignition/confirm", content: null, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<IgnitionConfirmEnvelopeDto>(stream, JsonOptions, ct);
        if (dto is null)
        {
            return null;
        }

        return new IgnitionConfirmVm
        {
            IgnitionCompleted = dto.IgnitionCompleted,
            SnapshotId = dto.Snapshot?.Id,
            DayBrief = dto.Snapshot?.DayBrief,
            CreatedAt = dto.Snapshot?.CreatedAt,
        };
    }

    public async Task<ConcernVm?> GetConcernAsync(string concernId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(concernId))
        {
            return null;
        }

        using var response = await _http.GetAsync(
            $"v1/concerns/{Uri.EscapeDataString(concernId.Trim())}",
            ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<ConcernEnvelopeDto>(stream, JsonOptions, ct);
        if (dto?.Concern is null || string.IsNullOrWhiteSpace(dto.Concern.TaskId))
        {
            return null;
        }

        return MapConcern(dto.Concern);
    }

    private static PulseVm MapPulse(PulseDto dto) => new()
    {
        DayBrief = dto.DayBrief,
        HermesHint = dto.HermesHint,
        GeneratedAt = dto.GeneratedAt ?? string.Empty,
        BriefIsSynthetic = dto.BriefIsSynthetic,
        Concerns = (dto.Concerns ?? []).Select(MapPulseConcern).ToList(),
        UnmatchedMail = (dto.UnmatchedMail ?? []).Select(MapUnmatchedMail).ToList(),
        Briefing = dto.Briefing is null ? null : MapBriefing(dto.Briefing),
        LastOperatorRun = dto.LastOperatorRun is null ? null : MapPulseOperatorRun(dto.LastOperatorRun),
    };

    private static PulseBriefingVm MapBriefing(PulseBriefingDto dto) => new()
    {
        UpcomingMeetings = (dto.UpcomingMeetings ?? []).Select(m => new PulseBriefingMeetingVm
        {
            Id = m.Id ?? string.Empty,
            Title = m.Title ?? string.Empty,
            StartsAt = m.StartsAt,
            SourceName = m.SourceName,
        }).ToList(),
        TopActions = (dto.TopActions ?? []).Select(a => new PulseBriefingActionVm
        {
            TaskId = a.TaskId ?? string.Empty,
            ProjectId = a.ProjectId ?? string.Empty,
            ProjectName = a.ProjectName ?? string.Empty,
            Title = a.Title ?? string.Empty,
            NextAction = a.NextAction,
        }).ToList(),
        WaitingOn = (dto.WaitingOn ?? []).Select(w => new PulseBriefingWaitingVm
        {
            TaskId = w.TaskId ?? string.Empty,
            ProjectName = w.ProjectName ?? string.Empty,
            Title = w.Title ?? string.Empty,
            Status = w.Status ?? string.Empty,
            UpdatedAt = w.UpdatedAt ?? string.Empty,
            AgeHours = w.AgeHours,
        }).ToList(),
        Alerts = (dto.Alerts ?? []).Select(a => new PulseBriefingAlertVm
        {
            Kind = a.Kind ?? string.Empty,
            Message = a.Message ?? string.Empty,
            ProjectId = a.ProjectId,
        }).ToList(),
        RecentChanges = (dto.RecentChanges ?? []).Select(c => new PulseBriefingChangeVm
        {
            Revision = c.Revision,
            EntityType = c.EntityType ?? string.Empty,
            EntityId = c.EntityId ?? string.Empty,
            ChangeKind = c.ChangeKind ?? string.Empty,
            SourceEvent = c.SourceEvent,
            CreatedAt = c.CreatedAt ?? string.Empty,
        }).ToList(),
        ChangeCursor = dto.ChangeCursor,
    };

    private static ViewModels.OperatorRunVm MapPulseOperatorRun(OperatorRunDto dto) => new()
    {
        Id = dto.Id ?? string.Empty,
        TriggerKind = dto.TriggerKind ?? string.Empty,
        Status = dto.Status ?? string.Empty,
        BriefingSummary = dto.BriefingSummary,
        ErrorText = dto.ErrorText,
        CreatedAt = dto.CreatedAt ?? string.Empty,
        CompletedAt = dto.CompletedAt,
    };

    private static PulseConcernVm MapPulseConcern(PulseConcernDto dto) => new()
    {
        TaskId = dto.TaskId ?? string.Empty,
        ProjectId = dto.ProjectId ?? string.Empty,
        ProjectName = dto.ProjectName ?? string.Empty,
        Title = dto.Title ?? string.Empty,
        Status = dto.Status ?? string.Empty,
        NextAction = dto.NextAction,
        BodyExcerpt = dto.BodyExcerpt,
        UpdatedAt = dto.UpdatedAt ?? string.Empty,
        SourceKind = dto.SourceKind,
        SourceConfidence = dto.SourceConfidence,
        SourceMatchReason = dto.SourceMatchReason,
    };

    private static PulseUnmatchedMailVm MapUnmatchedMail(PulseUnmatchedMailDto dto) => new()
    {
        SuggestionId = dto.SuggestionId ?? string.Empty,
        Summary = dto.Summary ?? string.Empty,
        EmailId = dto.EmailId,
        Subject = dto.Subject,
        Snippet = dto.Snippet,
        Confidence = dto.Confidence,
        CreatedAt = dto.CreatedAt ?? string.Empty,
    };

    private static ProjectMergePreviewVm MapMergePreview(MergePreviewDto dto) => new()
    {
        SourceProjectId = dto.SourceProjectId ?? string.Empty,
        SourceName = dto.SourceName ?? string.Empty,
        TargetProjectId = dto.TargetProjectId ?? string.Empty,
        TargetName = dto.TargetName ?? string.Empty,
        TaskCount = dto.TaskCount,
        NoteCount = dto.NoteCount,
        WorkstreamCount = dto.WorkstreamCount,
        FileLinkCount = dto.FileLinkCount,
        EmailLinkCount = dto.EmailLinkCount,
        ContactLinkCount = dto.ContactLinkCount,
        AliasCount = dto.AliasCount,
        BlockerCount = dto.BlockerCount,
        FolderCount = dto.FolderCount,
        Warnings = dto.Warnings ?? [],
    };

    private static ProjectMergeResultVm MapMergeResult(MergeResultDto dto) => new()
    {
        SourceProjectId = dto.SourceProjectId ?? string.Empty,
        SourceName = dto.SourceName ?? string.Empty,
        TargetProjectId = dto.TargetProjectId ?? string.Empty,
        TargetName = dto.TargetName ?? string.Empty,
        ArchivedSource = dto.ArchivedSource,
        MergedAt = dto.MergedAt ?? string.Empty,
    };

    private static OrbitProjectVm MapOrbitProject(OrbitProjectDto dto) => new()
    {
        Id = dto.Id ?? string.Empty,
        Name = dto.Name ?? string.Empty,
        Summary = dto.Summary,
        Status = dto.Status ?? string.Empty,
        InOrbit = dto.InOrbit,
        OpenConcernCount = dto.OpenConcernCount,
        TopNextAction = dto.TopNextAction,
        DossierEmpty = dto.DossierEmpty,
        MissingNextAction = dto.MissingNextAction,
    };

    private static IgnitionProjectVm MapIgnitionProject(IgnitionProjectDto dto) => new()
    {
        Id = dto.Id ?? string.Empty,
        Name = dto.Name ?? string.Empty,
        Created = dto.Created,
        HomeFolderPath = dto.HomeFolderPath,
        Error = dto.Error,
    };

    private static ConcernVm MapConcern(ConcernDto dto) => new()
    {
        TaskId = dto.TaskId!,
        ProjectId = dto.ProjectId,
        Title = dto.Title ?? string.Empty,
        Status = dto.Status ?? string.Empty,
        NextAction = dto.NextAction,
        Body = dto.Body,
    };

    private static ReindexFolderResult MapReindexSummary(ReindexSummaryDto? dto, int fallbackIndexedCount)
    {
        if (dto is null)
        {
            return new ReindexFolderResult { IndexedCount = fallbackIndexedCount };
        }

        return new ReindexFolderResult
        {
            IndexedCount = dto.IndexedCount > 0 ? dto.IndexedCount : fallbackIndexedCount,
            SoftSkippedDirectoryCount = dto.SoftSkippedDirectoryCount,
            OfflinePlaceholderCount = dto.OfflinePlaceholderCount,
            SampleRelativePaths = dto.SampleRelativePaths ?? [],
            SoftSkippedDirectories = dto.SoftSkippedDirectories ?? [],
            Warning = dto.Warning,
        };
    }

    public void Dispose() => _http.Dispose();

    public sealed class AttachFolderResult
    {
        public int IndexedCount { get; init; }

        public ReindexFolderResult Reindex { get; init; } = new();
    }

    public sealed class ReindexFolderResult
    {
        public int IndexedCount { get; init; }

        public int SoftSkippedDirectoryCount { get; init; }

        public int OfflinePlaceholderCount { get; init; }

        public IReadOnlyList<string> SampleRelativePaths { get; init; } = [];

        public IReadOnlyList<string> SoftSkippedDirectories { get; init; } = [];

        public string? Warning { get; init; }
    }

    public sealed class ProjectHomeFolderResult
    {
        public string Id { get; init; } = string.Empty;

        public string RootPath { get; init; } = string.Empty;

        public string? OrbitSandboxPath { get; init; }

        public string? Availability { get; init; }

        public int IndexedCount { get; init; }
    }

    public sealed class ProjectCreateResult
    {
        public string Id { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public string? Summary { get; init; }

        public string Status { get; init; } = "active";
    }

    public sealed class CalendarSourceVm
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string? Provider { get; set; }

        public string? MailboxName { get; set; }

        public string? CalendarName { get; set; }

        public bool Enabled { get; set; }

        public string? LastSyncStatus { get; set; }

        public string? LastSyncError { get; set; }

        public string DisplayLabel
        {
            get
            {
                var identity = string.Join(
                    " / ",
                    new[] { MailboxName, CalendarName }.Where(x => !string.IsNullOrWhiteSpace(x)));
                return string.IsNullOrWhiteSpace(identity) ? Name : $"{Name} ({identity})";
            }
        }
    }

    public sealed class ProjectCreateFromFolderResult
    {
        public required ProjectCreateResult Project { get; init; }

        public ProjectHomeFolderResult? Home { get; init; }
    }

    public sealed class EmailDetailVm
    {
        public string Id { get; init; } = string.Empty;

        public string? Subject { get; init; }

        public string? BodyPreview { get; init; }

        public string? SentAt { get; init; }
    }

    public sealed class EmailIngestResult
    {
        public string Id { get; init; } = string.Empty;

        public string? Subject { get; init; }

        public string? SentAt { get; init; }

        public string? InternetMessageId { get; init; }

        public string? ConversationId { get; init; }

        public string? BodyPreview { get; init; }

        public string? RawPath { get; init; }

        public bool WasExisting { get; init; }

        public IReadOnlyList<EmailParticipantResult> Participants { get; init; } = [];

        public IReadOnlyList<string> ProjectIds { get; init; } = [];

        public IReadOnlyList<EmailAttachmentResult> Attachments { get; init; } = [];
    }

    public sealed class EmailParticipantResult
    {
        public string Role { get; init; } = string.Empty;

        public string Address { get; init; } = string.Empty;

        public string? DisplayName { get; init; }
    }

    public sealed class EmailAttachmentResult
    {
        public string FileName { get; init; } = string.Empty;

        public string Path { get; init; } = string.Empty;

        public long SizeBytes { get; init; }
    }

    private sealed class WorkbenchDto
    {
        public WorkbenchScopeDto? Scope { get; set; }

        public List<CellDto>? Cells { get; set; }

        public List<LimboDto>? Limbo { get; set; }
    }

    private sealed class WorkbenchScopeDto
    {
        public string? Kind { get; set; }

        public string? ProjectId { get; set; }

        public string? ProjectName { get; set; }
    }

    private sealed class CellDto
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? Code { get; set; }

        public string? Summary { get; set; }

        public string? Status { get; set; }

        public string? CellKind { get; set; }

        public List<LineDto>? Lines { get; set; }

        public int OpenBlockerCount { get; set; }

        public string? TopBlockerSummary { get; set; }

        public string? UpcomingMeetingTitle { get; set; }

        public string? UpcomingMeetingStartsAt { get; set; }

        public int PendingSuggestionCount { get; set; }

        public string? RecentActivityAt { get; set; }

        public string? AccentColor { get; set; }

        public int SortOrder { get; set; }

        public double? BoardX { get; set; }

        public double? BoardY { get; set; }

        public double? BoardW { get; set; }

        public double? BoardH { get; set; }

        public bool DossierEmpty { get; set; }

        public bool MissingNextAction { get; set; }
    }

    private sealed class LineDto
    {
        public string? TaskId { get; set; }

        public string? Title { get; set; }

        public string? Status { get; set; }

        public string? NextAction { get; set; }

        public string? Body { get; set; }

        public string? DueAt { get; set; }

        public int? Priority { get; set; }

        public int? Urgency { get; set; }
    }

    private sealed class TaskByIdDto
    {
        public string? TaskId { get; set; }

        public string? ProjectId { get; set; }

        public string? Title { get; set; }

        public string? Status { get; set; }

        public string? NextAction { get; set; }

        public string? Body { get; set; }

        public string? DueAt { get; set; }

        public int? Priority { get; set; }

        public int? Urgency { get; set; }

        public string? SourceKind { get; set; }

        public double? SourceConfidence { get; set; }

        public string? SourceMatchReason { get; set; }
    }

    private sealed class LimboNoteByIdDto
    {
        public string? Id { get; set; }

        public string? OriginalText { get; set; }

        public string? CreatedAt { get; set; }

        public string? SuggestionId { get; set; }

        public string? SuggestionSummary { get; set; }
    }

    private sealed class CustomFieldValuesDto
    {
        public List<CustomFieldValueDto>? Fields { get; set; }
    }

    private sealed class CustomFieldValueDto
    {
        public string? Key { get; set; }

        public string? Label { get; set; }

        public string? FieldType { get; set; }

        public string? ValueJson { get; set; }
    }

    private sealed class LimboDto
    {
        public string? Id { get; set; }

        public string? OriginalText { get; set; }

        public string? CreatedAt { get; set; }

        public string? SuggestionId { get; set; }

        public string? SuggestionSummary { get; set; }
    }

    private sealed class CaptureDto
    {
        public string? NoteId { get; set; }

        public string? TaskId { get; set; }

        public string? OriginalText { get; set; }

        public string? ProjectId { get; set; }

        public bool IsLimbo { get; set; }
    }

    private sealed class ProjectCreateDto
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? Summary { get; set; }

        public string? Status { get; set; }

        public string? CreatedAt { get; set; }
    }

    private sealed class ProjectContextDto
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? Summary { get; set; }

        public string? Code { get; set; }

        public ProjectDossierDto? Dossier { get; set; }

        public bool DossierEmpty { get; set; } = true;

        public List<ProjectAliasDto>? Aliases { get; set; }

        public List<LineDto>? Tasks { get; set; }

        public List<LineDto>? CompletedTasks { get; set; }

        public List<NoteDto>? Notes { get; set; }

        public List<BlockerDto>? Blockers { get; set; }

        public List<ContactDto>? Contacts { get; set; }

        public List<MeetingDto>? Meetings { get; set; }

        public List<SuggestionDto>? Suggestions { get; set; }

        public List<FileDto>? Files { get; set; }
    }

    private sealed class ProjectDossierDto
    {
        public int Version { get; set; }

        public string? Address { get; set; }

        public string? OwnerClient { get; set; }

        public string? Phase { get; set; }

        public string? Portfolio { get; set; }

        public string? LinkedFolder { get; set; }

        public List<string>? CurrentPriorities { get; set; }

        public List<string>? MailboxSources { get; set; }

        public List<string>? CalendarSources { get; set; }

        public bool Empty { get; set; } = true;
    }

    private sealed class ProjectAliasDto
    {
        public string? Id { get; set; }

        public string? Alias { get; set; }
    }

    private sealed class NoteDto
    {
        public string? Id { get; set; }

        public string? OriginalText { get; set; }

        public string? CreatedAt { get; set; }
    }

    private sealed class BlockerDto
    {
        public string? Summary { get; set; }
    }

    private sealed class ContactDto
    {
        public string? PersonId { get; set; }

        public string? DisplayName { get; set; }

        public string? Title { get; set; }

        public string? OrganizationName { get; set; }
    }

    private sealed class MeetingDto
    {
        public string? Title { get; set; }

        public string? StartsAt { get; set; }
    }

    private sealed class SuggestionDto
    {
        public string? Id { get; set; }

        public string? Summary { get; set; }

        public string? Status { get; set; }
    }

    private sealed class FileDto
    {
        public string? Id { get; set; }

        public string? DisplayName { get; set; }

        public string? Path { get; set; }
    }

    private sealed class ProjectsDto
    {
        public List<ProjectDto>? Projects { get; set; }
    }

    private sealed class ProjectDto
    {
        public string? Id { get; set; }

        public string? Name { get; set; }
    }

    private sealed class FoldersDto
    {
        public List<FolderDto>? Folders { get; set; }
    }

    private sealed class FolderDto
    {
        public string? Id { get; set; }

        public string? RootPath { get; set; }

        public bool IsHome { get; set; }
    }

    private sealed class HomeFolderResponseDto
    {
        public HomeFolderDto? Home { get; set; }
    }

    private sealed class HomeFolderDto
    {
        public string? Id { get; set; }

        public string? RootPath { get; set; }

        public string? OrbitSandboxPath { get; set; }

        public string? Availability { get; set; }
    }

    private sealed class SetHomeFolderDto
    {
        public string? Id { get; set; }

        public string? RootPath { get; set; }

        public string? OrbitSandboxPath { get; set; }

        public string? Availability { get; set; }

        public int IndexedCount { get; set; }
    }

    private sealed class AttachFolderDto
    {
        public int IndexedCount { get; set; }

        public ReindexSummaryDto? Reindex { get; set; }
    }

    private sealed class ReindexDto
    {
        public int IndexedCount { get; set; }

        public ReindexSummaryDto? Reindex { get; set; }
    }

    private sealed class ReindexSummaryDto
    {
        public int IndexedCount { get; set; }

        public int SoftSkippedDirectoryCount { get; set; }

        public int OfflinePlaceholderCount { get; set; }

        public List<string>? SampleRelativePaths { get; set; }

        public List<string>? SoftSkippedDirectories { get; set; }

        public string? Warning { get; set; }
    }

    private sealed class SearchDto
    {
        public List<SearchHitDto>? Results { get; set; }
    }

    private sealed class SearchHitDto
    {
        public string? Id { get; set; }

        public string? DisplayName { get; set; }

        public string? Path { get; set; }
    }

    private sealed class GlobalSearchDto
    {
        public List<GlobalSearchHitDto>? Results { get; set; }
    }

    private sealed class GlobalSearchHitDto
    {
        public string? EntityType { get; set; }

        public string? EntityId { get; set; }

        public string? Title { get; set; }

        public string? Snippet { get; set; }

        public double Score { get; set; }

        public string? ProjectId { get; set; }

        public string? Path { get; set; }
    }

    private sealed class EmailDetailDto
    {
        public string? Id { get; set; }

        public string? Subject { get; set; }

        public string? BodyPreview { get; set; }

        public string? SentAt { get; set; }
    }

    private sealed class PreviewDto
    {
        public string? PreviewText { get; set; }
    }

    private sealed class EmailIngestDto
    {
        public string? Id { get; set; }

        public string? Subject { get; set; }

        public string? SentAt { get; set; }

        public string? InternetMessageId { get; set; }

        public string? ConversationId { get; set; }

        public string? BodyPreview { get; set; }

        public string? RawPath { get; set; }

        public bool WasExisting { get; set; }

        public List<EmailParticipantDto>? Participants { get; set; }

        public List<string>? ProjectIds { get; set; }

        public List<EmailAttachmentDto>? Attachments { get; set; }
    }

    private sealed class TaskEmailThreadsDto
    {
        public List<TaskEmailThreadDto>? Threads { get; set; }
    }

    private sealed class TaskEmailThreadDto
    {
        public string? Id { get; set; }

        public string? TaskId { get; set; }

        public string? ConversationId { get; set; }

        public string? AnchorEmailId { get; set; }

        public string? Subject { get; set; }

        public string? LatestSentAt { get; set; }

        public int MessageCount { get; set; }
    }

    private sealed class EmailParticipantDto
    {
        public string? Role { get; set; }

        public string? Address { get; set; }

        public string? DisplayName { get; set; }
    }

    private sealed class EmailAttachmentDto
    {
        public string? FileName { get; set; }

        public string? Path { get; set; }

        public long SizeBytes { get; set; }
    }

    private sealed class ContactsListDto
    {
        public List<ContactListDto>? Contacts { get; set; }
    }

    private sealed class ContactListDto
    {
        public string? Id { get; set; }

        public string? DisplayName { get; set; }

        public string? Title { get; set; }

        public string? OrganizationName { get; set; }

        public string? PrimaryEmail { get; set; }

        public string? PrimaryPhone { get; set; }

        public string? Category { get; set; }

        public string? Disposition { get; set; }
    }

    private sealed class ContactDetailDto
    {
        public string? Id { get; set; }

        public string? DisplayName { get; set; }

        public string? Title { get; set; }

        public string? OrganizationId { get; set; }

        public string? OrganizationName { get; set; }

        public string? Category { get; set; }

        public string? Disposition { get; set; }

        public string? ReportsToPersonId { get; set; }

        public string? ReportsToDisplayName { get; set; }

        public List<ContactMethodDto>? Methods { get; set; }

        public List<ContactProjectDto>? Projects { get; set; }

        public List<ContactEmailDto>? RecentEmails { get; set; }

        public List<ContactProvenanceDto>? Provenance { get; set; }
    }

    private sealed class ContactMethodDto
    {
        public string? MethodType { get; set; }

        public string? Value { get; set; }
    }

    private sealed class ContactProjectDto
    {
        public string? Id { get; set; }

        public string? Name { get; set; }
    }

    private sealed class ContactEmailDto
    {
        public string? Id { get; set; }

        public string? Subject { get; set; }

        public string? SentAt { get; set; }

        public string? Role { get; set; }
    }

    private sealed class ContactProvenanceDto
    {
        public string? Field { get; set; }

        public string? Value { get; set; }

        public string? SourceKind { get; set; }

        public string? SourceEmailId { get; set; }
    }

    private sealed class RemoteActivityDto
    {
        public List<RemoteConversationDto>? Conversations { get; set; }

        public List<RemoteAuditEventDto>? AuditEvents { get; set; }
    }

    private sealed class RemoteConversationDto
    {
        public string? Id { get; set; }

        public string? Title { get; set; }

        public string? HermesSessionId { get; set; }

        public string? UpdatedAt { get; set; }
    }

    private sealed class RemoteAuditEventDto
    {
        public string? Id { get; set; }

        public string? EventType { get; set; }

        public string? Actor { get; set; }

        public string? Summary { get; set; }

        public string? HermesSessionId { get; set; }

        public string? CreatedAt { get; set; }
    }

    private sealed class OperatorRunsDto
    {
        public List<OperatorRunDto>? Runs { get; set; }
    }

    private sealed class OperatorRunDto
    {
        public string? Id { get; set; }

        public string? TriggerKind { get; set; }

        public string? TriggerPayloadJson { get; set; }

        public string? Status { get; set; }

        public string? BriefingSummary { get; set; }

        public string? ErrorText { get; set; }

        public string? CreatedAt { get; set; }

        public string? CompletedAt { get; set; }
    }

    private sealed class OperatorRulesDto
    {
        public List<OperatorRuleDto>? Rules { get; set; }
    }

    private sealed class OperatorRuleDto
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? TriggerKind { get; set; }

        public string? ActionKind { get; set; }
    }

    private sealed class OperatorMemoryDto
    {
        public List<OperatorMemoryFactDto>? Memory { get; set; }
    }

    private sealed class OperatorMemoryFactDto
    {
        public string? Id { get; set; }

        public string? Scope { get; set; }

        public string? Kind { get; set; }

        public string? Text { get; set; }
    }

    private sealed class CalendarSourcesEnvelopeDto
    {
        public List<CalendarSourceDto>? Sources { get; set; }
    }

    private sealed class CalendarSourceDto
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? Provider { get; set; }

        public string? MailboxName { get; set; }

        public string? CalendarName { get; set; }

        public bool Enabled { get; set; }

        public string? LastSyncStatus { get; set; }

        public string? LastSyncError { get; set; }
    }

    private sealed class PulseEnvelopeDto
    {
        public PulseDto? Pulse { get; set; }
    }

    private sealed class PulseDto
    {
        public string? DayBrief { get; set; }

        public string? HermesHint { get; set; }

        public string? GeneratedAt { get; set; }

        public bool BriefIsSynthetic { get; set; }

        public List<PulseConcernDto>? Concerns { get; set; }

        public List<PulseUnmatchedMailDto>? UnmatchedMail { get; set; }

        public PulseBriefingDto? Briefing { get; set; }

        public OperatorRunDto? LastOperatorRun { get; set; }
    }

    private sealed class PulseBriefingDto
    {
        public List<PulseBriefingMeetingDto>? UpcomingMeetings { get; set; }

        public List<PulseBriefingActionDto>? TopActions { get; set; }

        public List<PulseBriefingWaitingDto>? WaitingOn { get; set; }

        public List<PulseBriefingAlertDto>? Alerts { get; set; }

        public List<PulseBriefingChangeDto>? RecentChanges { get; set; }

        public long ChangeCursor { get; set; }
    }

    private sealed class PulseBriefingMeetingDto
    {
        public string? Id { get; set; }

        public string? Title { get; set; }

        public string? StartsAt { get; set; }

        public string? SourceName { get; set; }
    }

    private sealed class PulseBriefingActionDto
    {
        public string? TaskId { get; set; }

        public string? ProjectId { get; set; }

        public string? ProjectName { get; set; }

        public string? Title { get; set; }

        public string? NextAction { get; set; }
    }

    private sealed class PulseBriefingWaitingDto
    {
        public string? TaskId { get; set; }

        public string? ProjectName { get; set; }

        public string? Title { get; set; }

        public string? Status { get; set; }

        public string? UpdatedAt { get; set; }

        public int AgeHours { get; set; }
    }

    private sealed class PulseBriefingAlertDto
    {
        public string? Kind { get; set; }

        public string? Message { get; set; }

        public string? ProjectId { get; set; }
    }

    private sealed class PulseBriefingChangeDto
    {
        public long Revision { get; set; }

        public string? EntityType { get; set; }

        public string? EntityId { get; set; }

        public string? ChangeKind { get; set; }

        public string? SourceEvent { get; set; }

        public string? CreatedAt { get; set; }
    }

    private sealed class PulseConcernDto
    {
        public string? TaskId { get; set; }

        public string? ProjectId { get; set; }

        public string? ProjectName { get; set; }

        public string? Title { get; set; }

        public string? Status { get; set; }

        public string? NextAction { get; set; }

        public string? BodyExcerpt { get; set; }

        public string? UpdatedAt { get; set; }

        public string? SourceKind { get; set; }

        public double? SourceConfidence { get; set; }

        public string? SourceMatchReason { get; set; }
    }

    private sealed class PulseUnmatchedMailDto
    {
        public string? SuggestionId { get; set; }

        public string? Summary { get; set; }

        public string? EmailId { get; set; }

        public string? Subject { get; set; }

        public string? Snippet { get; set; }

        public double? Confidence { get; set; }

        public string? CreatedAt { get; set; }
    }

    private sealed class MergePreviewEnvelopeDto
    {
        public MergePreviewDto? Preview { get; set; }
    }

    private sealed class MergePreviewDto
    {
        public string? SourceProjectId { get; set; }

        public string? SourceName { get; set; }

        public string? TargetProjectId { get; set; }

        public string? TargetName { get; set; }

        public int TaskCount { get; set; }

        public int NoteCount { get; set; }

        public int WorkstreamCount { get; set; }

        public int FileLinkCount { get; set; }

        public int EmailLinkCount { get; set; }

        public int ContactLinkCount { get; set; }

        public int AliasCount { get; set; }

        public int BlockerCount { get; set; }

        public int FolderCount { get; set; }

        public List<string>? Warnings { get; set; }
    }

    private sealed class MergeResultEnvelopeDto
    {
        public MergeResultDto? Merge { get; set; }
    }

    private sealed class MergeResultDto
    {
        public string? SourceProjectId { get; set; }

        public string? SourceName { get; set; }

        public string? TargetProjectId { get; set; }

        public string? TargetName { get; set; }

        public bool ArchivedSource { get; set; }

        public string? MergedAt { get; set; }
    }

    private sealed class OrbitEnvelopeDto
    {
        public bool IgnitionCompleted { get; set; }

        public List<OrbitProjectDto>? Projects { get; set; }
    }

    private sealed class OrbitProjectDto
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? Summary { get; set; }

        public string? Status { get; set; }

        public bool InOrbit { get; set; }

        public int OpenConcernCount { get; set; }

        public string? TopNextAction { get; set; }

        public bool DossierEmpty { get; set; }

        public bool MissingNextAction { get; set; }
    }

    private sealed class IgnitionProjectsEnvelopeDto
    {
        public List<IgnitionProjectDto>? Projects { get; set; }
    }

    private sealed class IgnitionProjectDto
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public bool Created { get; set; }

        public string? HomeFolderPath { get; set; }

        public string? Error { get; set; }
    }

    private sealed class IgnitionConfirmEnvelopeDto
    {
        public bool IgnitionCompleted { get; set; }

        public IgnitionSnapshotDto? Snapshot { get; set; }
    }

    private sealed class IgnitionSnapshotDto
    {
        public string? Id { get; set; }

        public string? DayBrief { get; set; }

        public string? CreatedAt { get; set; }
    }

    private sealed class ConcernEnvelopeDto
    {
        public ConcernDto? Concern { get; set; }
    }

    private sealed class ConcernDto
    {
        public string? TaskId { get; set; }

        public string? ProjectId { get; set; }

        public string? Title { get; set; }

        public string? Status { get; set; }

        public string? NextAction { get; set; }

        public string? Body { get; set; }
    }
}

public sealed class OperatorDashboardVm
{
    public string? LatestBriefing { get; set; }

    public string? LatestRunStatus { get; set; }

    public string? LatestTrigger { get; set; }

    public string? LatestRunId { get; set; }

    public string? LatestPayloadJson { get; set; }

    public string? LatestCreatedAt { get; set; }

    public IReadOnlyList<OperatorRunVm> RecentRuns { get; set; } = [];

    public IReadOnlyList<OperatorRuleVm> Rules { get; set; } = [];

    public IReadOnlyList<OperatorMemoryVm> Memory { get; set; } = [];

    public IReadOnlyList<PendingSuggestionVm> PendingSuggestions { get; set; } = [];
}

public sealed class OperatorRunVm
{
    public string Id { get; set; } = string.Empty;

    public string TriggerKind { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string? BriefingSummary { get; set; }

    public string? TriggerPayloadJson { get; set; }

    public string? CreatedAt { get; set; }
}

public sealed class OperatorRuleVm
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string TriggerKind { get; set; } = string.Empty;

    public string ActionKind { get; set; } = string.Empty;

    public string DisplayLine => $"{Name} · {TriggerKind} → {ActionKind}";
}

public sealed class OperatorMemoryVm
{
    public string Id { get; set; } = string.Empty;

    public string Scope { get; set; } = "global";

    public string Kind { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public string DisplayLine => $"[{Kind}/{Scope}] {Text}";
}

public sealed class ContactListResult
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string? Title { get; init; }

    public string? OrganizationName { get; init; }

    public string? PrimaryEmail { get; init; }

    public string? PrimaryPhone { get; init; }

    public string? Category { get; init; }

    public string Disposition { get; init; } = "active";
}

public sealed class ContactDetailResult
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string? Title { get; init; }

    public string? OrganizationId { get; init; }

    public string? OrganizationName { get; init; }

    public string? Category { get; init; }

    public string Disposition { get; init; } = "active";

    public string? ReportsToPersonId { get; init; }

    public string? ReportsToDisplayName { get; init; }

    public IReadOnlyList<ContactMethodResult> Methods { get; init; } = [];

    public IReadOnlyList<ContactProjectResult> Projects { get; init; } = [];

    public IReadOnlyList<ContactEmailResult> RecentEmails { get; init; } = [];

    public IReadOnlyList<ContactProvenanceResult> Provenance { get; init; } = [];
}

public sealed class ContactMethodResult
{
    public string MethodType { get; init; } = string.Empty;

    public string Value { get; init; } = string.Empty;
}

public sealed class ContactProjectResult
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;
}

public sealed class ContactEmailResult
{
    public string Id { get; init; } = string.Empty;

    public string? Subject { get; init; }

    public string? SentAt { get; init; }

    public string? Role { get; init; }
}

public sealed class ContactProvenanceResult
{
    public string Field { get; init; } = string.Empty;

    public string Value { get; init; } = string.Empty;

    public string SourceKind { get; init; } = string.Empty;

    public string? SourceEmailId { get; init; }
}

public sealed class RemoteActivityVm
{
    public IReadOnlyList<RemoteConversationVm> Conversations { get; init; } = [];

    public IReadOnlyList<RemoteChangeVm> Changes { get; init; } = [];
}

public sealed class RemoteConversationVm
{
    public string Id { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string? HermesSessionId { get; init; }

    public string UpdatedAt { get; init; } = string.Empty;

    public string SummaryLine { get; init; } = string.Empty;
}

public sealed class RemoteChangeVm
{
    public string Id { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string DetailLine { get; init; } = string.Empty;

    public string? ConversationHint { get; init; }
}

public sealed class SyncSnapshotListItem
{
    public required string SnapshotId { get; init; }

    public required string Display { get; init; }

    public override string ToString() => Display;
}
