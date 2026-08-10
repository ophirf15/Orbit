namespace Orbit.Core.Agent;

/// <summary>Mutations the workbench detail agent can apply via app-side tokens / local intent.</summary>
public sealed class WorkbenchAgentMutation
{
    public string? Title { get; init; }

    public string? Status { get; init; }

    public string? NextAction { get; init; }

    public string? Body { get; init; }

    public bool DeleteTask { get; init; }

    /// <summary>"waits_for" when this task depends on the other, "feeds" when the other depends on this.</summary>
    public string? LinkDirection { get; init; }

    /// <summary>Title fragment of the counterpart task; the app resolves it against the project's tasks.</summary>
    public string? LinkTaskQuery { get; init; }

    /// <summary>What the waiting task needs from the other one.</summary>
    public string? LinkExpects { get; init; }

    public bool HasTaskUpdate =>
        !string.IsNullOrWhiteSpace(Title)
        || !string.IsNullOrWhiteSpace(Status)
        || !string.IsNullOrWhiteSpace(NextAction)
        || !string.IsNullOrWhiteSpace(Body);

    public bool HasLinkRequest =>
        !string.IsNullOrWhiteSpace(LinkTaskQuery) && !string.IsNullOrWhiteSpace(LinkDirection);
}

/// <summary>
/// Parses agent control tokens and local “apply that title” intents for the detail-panel agent.
/// The in-app Hermes stream has no MCP tool loop, so the app executes these mutations.
/// </summary>
public static class WorkbenchAgentActions
{
    public const string UpdateTaskToken = "ORBIT_UPDATE_TASK";
    public const string DeleteTaskToken = "ORBIT_DELETE_TASK";
    public const string LinkTaskToken = "ORBIT_LINK_TASK";

    public static bool TryParseReply(
        string agentReply,
        out WorkbenchAgentMutation? mutation,
        out string displayText)
    {
        mutation = null;
        displayText = (agentReply ?? string.Empty).Trim();
        if (displayText.Length == 0)
        {
            return false;
        }

        var normalized = displayText.Replace("\r\n", "\n", StringComparison.Ordinal);
        var delete = normalized.Contains(DeleteTaskToken, StringComparison.OrdinalIgnoreCase);
        var inLink = normalized.Contains(LinkTaskToken, StringComparison.OrdinalIgnoreCase);
        string? title = null;
        string? status = null;
        string? next = null;
        string? body = null;
        string? linkDirection = null;
        string? linkTask = null;
        string? linkExpects = null;
        var inUpdate = false;

        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Equals(UpdateTaskToken, StringComparison.OrdinalIgnoreCase))
            {
                inUpdate = true;
                continue;
            }

            if (line.Equals(DeleteTaskToken, StringComparison.OrdinalIgnoreCase)
                || line.Equals(LinkTaskToken, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (inLink)
            {
                if (line.StartsWith("DIRECTION:", StringComparison.OrdinalIgnoreCase))
                {
                    linkDirection = NormalizeLinkDirection(CleanField(line["DIRECTION:".Length..]));
                    continue;
                }

                if (line.StartsWith("TASK:", StringComparison.OrdinalIgnoreCase))
                {
                    linkTask = CleanField(line["TASK:".Length..]);
                    continue;
                }

                if (line.StartsWith("EXPECTS:", StringComparison.OrdinalIgnoreCase))
                {
                    linkExpects = CleanField(line["EXPECTS:".Length..]);
                    continue;
                }
            }

            if (!inUpdate && !line.StartsWith("TITLE:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Allow TITLE:/STATUS: even without the token line (models sometimes omit it).
            if (line.StartsWith("TITLE:", StringComparison.OrdinalIgnoreCase))
            {
                title = CleanField(line["TITLE:".Length..]);
                inUpdate = true;
            }
            else if (line.StartsWith("STATUS:", StringComparison.OrdinalIgnoreCase))
            {
                status = NormalizeStatus(CleanField(line["STATUS:".Length..]));
                inUpdate = true;
            }
            else if (line.StartsWith("NEXT:", StringComparison.OrdinalIgnoreCase)
                     || line.StartsWith("NOTE:", StringComparison.OrdinalIgnoreCase)
                     || line.StartsWith("SUBTITLE:", StringComparison.OrdinalIgnoreCase))
            {
                var idx = line.IndexOf(':');
                next = CleanField(line[(idx + 1)..]);
                inUpdate = true;
            }
            else if (line.StartsWith("BODY:", StringComparison.OrdinalIgnoreCase)
                     || line.StartsWith("SUMMARY:", StringComparison.OrdinalIgnoreCase))
            {
                var idx = line.IndexOf(':');
                body = CleanField(line[(idx + 1)..]);
                inUpdate = true;
            }
        }

        var hasLink = !string.IsNullOrWhiteSpace(linkTask) && !string.IsNullOrWhiteSpace(linkDirection);
        if (!delete && !hasLink && title is null && status is null && next is null && body is null)
        {
            return false;
        }

        mutation = new WorkbenchAgentMutation
        {
            Title = string.IsNullOrWhiteSpace(title) ? null : CaptureClarify.SanitizeTitle(title),
            Status = status,
            NextAction = string.IsNullOrWhiteSpace(next) ? null : Truncate(next!, 160),
            Body = string.IsNullOrWhiteSpace(body) ? null : body,
            DeleteTask = delete,
            LinkDirection = linkDirection,
            LinkTaskQuery = string.IsNullOrWhiteSpace(linkTask) ? null : linkTask,
            LinkExpects = string.IsNullOrWhiteSpace(linkExpects) ? null : Truncate(linkExpects!, 120),
        };
        displayText = StripControlLines(normalized);
        return mutation.DeleteTask || mutation.HasTaskUpdate || mutation.HasLinkRequest;
    }

    private static string? NormalizeLinkDirection(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim().ToLowerInvariant().Replace(' ', '_');
        return value switch
        {
            "waits_for" or "waits" or "blocked_by" or "depends_on" or "upstream" => "waits_for",
            "feeds" or "feeds_into" or "blocks" or "downstream" => "feeds",
            _ => null,
        };
    }

    /// <summary>
    /// Resolves the counterpart task by title fragment. Requires an unambiguous match so the
    /// agent can't silently link the wrong task.
    /// </summary>
    public static bool TryResolveLinkTarget(
        string query,
        IEnumerable<(string TaskId, string Title)> candidates,
        out string taskId)
    {
        taskId = string.Empty;
        var needle = (query ?? string.Empty).Trim();
        if (needle.Length < 3)
        {
            return false;
        }

        var pool = candidates.ToList();
        var exact = pool
            .Where(c => string.Equals(c.Title.Trim(), needle, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exact.Count == 1)
        {
            taskId = exact[0].TaskId;
            return true;
        }

        var contains = pool
            .Where(c => c.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)
                        || needle.Contains(c.Title.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (contains.Count == 1)
        {
            taskId = contains[0].TaskId;
            return true;
        }

        return false;
    }

    /// <summary>
    /// User asked to apply/set a title — resolve the title from an explicit phrase or recent agent proposals.
    /// </summary>
    public static bool TryResolveApplyTitle(
        string userText,
        string? lastAssistantReply,
        out string title) =>
        TryResolveApplyTitle(userText, lastAssistantReply is null ? [] : [lastAssistantReply], out title);

    public static bool TryResolveApplyTitle(
        string userText,
        IEnumerable<string> assistantRepliesNewestFirst,
        out string title)
    {
        title = string.Empty;
        var text = (userText ?? string.Empty).Trim();
        if (text.Length == 0 || !LooksLikeTitleUpdateRequest(text))
        {
            return false;
        }

        if (TryExtractExplicitTitle(text, out var explicitTitle))
        {
            title = CaptureClarify.SanitizeTitle(explicitTitle);
            return title.Length > 0;
        }

        foreach (var reply in assistantRepliesNewestFirst)
        {
            if (string.IsNullOrWhiteSpace(reply))
            {
                continue;
            }

            if (TryPickTitleCandidate(reply, out var candidate))
            {
                title = CaptureClarify.SanitizeTitle(candidate);
                return title.Length > 0;
            }
        }

        return false;
    }

    public static bool LooksLikeTitleUpdateRequest(string text)
    {
        var lower = (text ?? string.Empty).Trim().ToLowerInvariant();
        if (lower.Length == 0)
        {
            return false;
        }

        if (lower.Contains("title:", StringComparison.Ordinal)
            || lower.StartsWith("rename ", StringComparison.Ordinal)
            || lower.Contains("rename this", StringComparison.Ordinal)
            || lower.Contains("rename the", StringComparison.Ordinal))
        {
            return true;
        }

        var mentionsTitle = lower.Contains("title", StringComparison.Ordinal)
                            || lower.Contains("headline", StringComparison.Ordinal)
                            || lower.Contains("name of this", StringComparison.Ordinal);
        var applyVerb = lower.Contains("apply", StringComparison.Ordinal)
                        || lower.Contains("set ", StringComparison.Ordinal)
                        || lower.StartsWith("set", StringComparison.Ordinal)
                        || lower.Contains("use that", StringComparison.Ordinal)
                        || lower.Contains("use this", StringComparison.Ordinal)
                        || lower.Contains("change", StringComparison.Ordinal)
                        || lower.Contains("update", StringComparison.Ordinal)
                        || lower.Contains("make that", StringComparison.Ordinal)
                        || lower.Contains("make it", StringComparison.Ordinal)
                        || lower.Contains("save that", StringComparison.Ordinal);

        if (mentionsTitle && applyVerb)
        {
            return true;
        }

        // Short confirmations after the agent proposed a rewrite.
        return lower is "apply that" or "use that" or "set it" or "do it" or "yes apply" or "yes, apply"
               or "apply" or "use it" or "that's good" or "thats good" or "perfect, apply";
    }

    public static bool LooksLikeStatusUpdateRequest(string text, out string? status)
    {
        status = null;
        var lower = (text ?? string.Empty).Trim().ToLowerInvariant();
        if (!(lower.Contains("status", StringComparison.Ordinal)
              || lower.Contains("mark ", StringComparison.Ordinal)
              || lower.Contains("mark as", StringComparison.Ordinal)
              || lower.Contains("mark it", StringComparison.Ordinal)
              || lower.Contains("set to", StringComparison.Ordinal)))
        {
            return false;
        }

        if (lower.Contains("blocked", StringComparison.Ordinal))
        {
            status = "blocked";
        }
        else if (lower.Contains("waiting", StringComparison.Ordinal))
        {
            status = "waiting";
        }
        else if (lower.Contains("active", StringComparison.Ordinal) || lower.Contains("in progress", StringComparison.Ordinal))
        {
            status = "active";
        }
        else if (lower.Contains("not started", StringComparison.Ordinal)
                 || lower.Contains("not_started", StringComparison.Ordinal)
                 || (lower.Contains("status", StringComparison.Ordinal) && lower.Contains("new", StringComparison.Ordinal)))
        {
            status = "not_started";
        }
        else
        {
            return false;
        }

        return true;
    }

    public static string StripControlLines(string raw)
    {
        var lines = raw
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l =>
            {
                var t = l.Trim();
                if (t.Length == 0)
                {
                    return true;
                }

                if (t.Equals(UpdateTaskToken, StringComparison.OrdinalIgnoreCase)
                    || t.Equals(DeleteTaskToken, StringComparison.OrdinalIgnoreCase)
                    || t.Equals(LinkTaskToken, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (t.StartsWith("TITLE:", StringComparison.OrdinalIgnoreCase)
                    || t.StartsWith("STATUS:", StringComparison.OrdinalIgnoreCase)
                    || t.StartsWith("NEXT:", StringComparison.OrdinalIgnoreCase)
                    || t.StartsWith("NOTE:", StringComparison.OrdinalIgnoreCase)
                    || t.StartsWith("SUBTITLE:", StringComparison.OrdinalIgnoreCase)
                    || t.StartsWith("BODY:", StringComparison.OrdinalIgnoreCase)
                    || t.StartsWith("SUMMARY:", StringComparison.OrdinalIgnoreCase)
                    || t.StartsWith("DIRECTION:", StringComparison.OrdinalIgnoreCase)
                    || t.StartsWith("TASK:", StringComparison.OrdinalIgnoreCase)
                    || t.StartsWith("EXPECTS:", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return true;
            });

        var text = string.Join("\n", lines).Trim();
        return string.IsNullOrWhiteSpace(text) ? "Done." : text;
    }

    private static bool TryExtractExplicitTitle(string userText, out string title)
    {
        title = string.Empty;
        var patterns = new[]
        {
            "title:",
            "title to:",
            "title to ",
            "title as ",
            "rename to ",
            "rename it to ",
            "set it to ",
            "change it to ",
            "call it ",
        };

        var lower = userText.ToLowerInvariant();
        foreach (var p in patterns)
        {
            var idx = lower.IndexOf(p, StringComparison.Ordinal);
            if (idx < 0)
            {
                continue;
            }

            var rest = userText[(idx + p.Length)..].Trim().Trim('"', '\'', '.', ' ');
            if (rest.Length >= 3)
            {
                title = rest;
                return true;
            }
        }

        return false;
    }

    private static bool TryPickTitleCandidate(string assistantReply, out string title)
    {
        title = string.Empty;
        var lines = assistantReply
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim().TrimStart('-', '*', '•', ' '))
            .Where(l => l.Length > 0)
            .Where(l => !l.Equals(UpdateTaskToken, StringComparison.OrdinalIgnoreCase))
            .Where(l => !l.StartsWith("TITLE:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Prefer an explicit TITLE: if present.
        foreach (var line in assistantReply.Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("TITLE:", StringComparison.OrdinalIgnoreCase))
            {
                title = CleanField(line["TITLE:".Length..]);
                return title.Length > 0;
            }
        }

        // Prefer the last short non-question line (latest proposal).
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (line.EndsWith('?'))
            {
                continue;
            }

            if (line.Length is < 8 or > 120)
            {
                continue;
            }

            if (line.StartsWith("I ", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Sure", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Yes", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("What ", StringComparison.OrdinalIgnoreCase)
                || line.Contains("would you like", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            title = line.Trim('"', '\'');
            return true;
        }

        return false;
    }

    private static string? NormalizeStatus(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var s = raw.Trim().ToLowerInvariant().Replace(' ', '_');
        return s switch
        {
            "blocked" => "blocked",
            "waiting" => "waiting",
            "active" or "in_progress" or "in-progress" => "active",
            "not_started" or "new" or "todo" => "not_started",
            _ => null,
        };
    }

    private static string CleanField(string value) =>
        value.Trim().Trim('"', '\'');

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
