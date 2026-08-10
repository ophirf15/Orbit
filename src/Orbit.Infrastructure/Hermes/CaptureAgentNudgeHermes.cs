using System.Text;
using Orbit.Agent.Contracts.Hermes;
using Orbit.Core.Agent;
using Orbit.Core.Settings;

namespace Orbit.Infrastructure.Hermes;

/// <summary>Optional Hermes enrichment for capture nudges / task summaries.</summary>
public static class CaptureAgentNudgeHermes
{
    public static async Task<string> ResolveAsync(
        string captureText,
        string? projectName,
        string? hermesBaseUrl,
        string? hermesApiKey,
        CancellationToken ct = default)
    {
        var local = CaptureAgentNudge.Format(CaptureAgentNudge.BuildLocal(captureText, projectName));
        try
        {
            if (!HermesUrlValidation.TryValidate(hermesBaseUrl, out var url, out _))
            {
                return local;
            }

            using var client = new HermesHttpClient(new Uri(url!), hermesApiKey);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));

            var session = await client.EnsureSessionAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
            var prompt =
                $"""
                You are Orbit's inline workbench agent. A user just captured a line on project "{projectName ?? "unknown"}":
                "{captureText.Trim()}"

                Reply with EXACTLY 2 to 4 short lines (no bullets, no markdown, no preamble). Cover:
                1) a clearer rewrite of the line (start with "Reword: ")
                2) one clarifying question
                3) what context would help next (file, contact, email, or blocker)
                Keep each line under 90 characters.
                """;

            var hermes = await CollectChatAsync(client, session, prompt, timeout.Token).ConfigureAwait(false);
            hermes = NormalizeNudge(hermes);
            return string.IsNullOrWhiteSpace(hermes) ? local : hermes;
        }
        catch (Exception)
        {
            return local;
        }
    }

    public static async Task<string> SummarizeTaskAsync(
        string projectName,
        string taskTitle,
        string status,
        IReadOnlyList<string> notes,
        string? hermesBaseUrl,
        string? hermesApiKey,
        CancellationToken ct = default)
    {
        var local = CaptureAgentNudge.BuildTaskSummaryLocal(projectName, taskTitle, status, notes);
        try
        {
            if (!HermesUrlValidation.TryValidate(hermesBaseUrl, out var url, out _))
            {
                return local;
            }

            using var client = new HermesHttpClient(new Uri(url!), hermesApiKey);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            var session = await client.EnsureSessionAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
            var noteBlurb = notes.Count == 0
                ? "(no notes yet)"
                : string.Join(" | ", notes.Take(5));
            var prompt =
                $"""
                Summarize this Orbit task in 2-3 short sentences for a workbench drawer.
                Project: {projectName}
                Task: {taskTitle}
                Status: {status}
                Notes: {noteBlurb}
                No markdown. Mention next useful action if obvious.
                """;

            var hermes = (await CollectChatAsync(client, session, prompt, timeout.Token).ConfigureAwait(false)).Trim();
            return string.IsNullOrWhiteSpace(hermes) ? local : hermes;
        }
        catch (Exception)
        {
            return local;
        }
    }

    private static async Task<string> CollectChatAsync(
        HermesHttpClient client,
        HermesSession session,
        string userPrompt,
        CancellationToken ct)
    {
        var buffer = new StringBuilder();
        await foreach (var delta in client.StreamChatAsync(
                           new HermesChatRequest
                           {
                               SessionId = session.SessionId,
                               SessionKey = session.SessionKey,
                               Stream = true,
                               Messages =
                               [
                                   new HermesChatMessage { Role = "system", Content = "Be terse. No tools." },
                                   new HermesChatMessage { Role = "user", Content = userPrompt },
                               ],
                           },
                           ct))
        {
            if (delta.Kind == HermesChatDeltaKind.Error)
            {
                return string.Empty;
            }

            if (delta.Kind == HermesChatDeltaKind.Content && !string.IsNullOrEmpty(delta.Text))
            {
                buffer.Append(delta.Text);
            }

            if (delta.Kind == HermesChatDeltaKind.Done)
            {
                break;
            }
        }

        return buffer.ToString();
    }

    private static string NormalizeNudge(string raw)
    {
        var lines = raw
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.TrimStart('-', '*', '•', ' ', '\t'))
            .Where(l => l.Length > 0)
            .Take(4)
            .Select(l => l.Length <= 110 ? l : l[..109] + "…")
            .ToList();
        return string.Join("\n", lines);
    }
}
