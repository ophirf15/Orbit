using System.Text;
using Orbit.Agent.Contracts.Hermes;
using Orbit.Core.Agent;
using Orbit.Core.Settings;

namespace Orbit.Infrastructure.Hermes;

/// <summary>Hermes-backed clarify turns for workbench capture.</summary>
public static class CaptureClarifyHermes
{
    public static async Task<CaptureClarifyResult> OpenAsync(
        string captureText,
        string? projectName,
        string? hermesBaseUrl,
        string? hermesApiKey,
        List<HermesChatMessage> history,
        CancellationToken ct = default)
    {
        var local = CaptureClarify.Open(captureText, projectName);
        try
        {
            if (!TryClient(hermesBaseUrl, hermesApiKey, out var client))
            {
                return local;
            }

            using (client)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var session = await client.EnsureSessionAsync(cancellationToken: timeout.Token).ConfigureAwait(false);

                history.Clear();
                history.Add(new HermesChatMessage
                {
                    Role = "system",
                    Content = SystemPrompt(projectName, captureText),
                });
                history.Add(new HermesChatMessage
                {
                    Role = "user",
                    Content = "Open the clarify: ask one short question (optionally suggest a reword). Do not DONE yet unless the capture is already perfect.",
                });

                var raw = await CollectAsync(client, session, history, timeout.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return local;
                }

                history.Add(new HermesChatMessage { Role = "assistant", Content = raw });
                return CaptureClarify.TryParseAgentComplete(raw)
                       ?? CaptureClarifyResult.Incomplete(NormalizeDisplay(raw));
            }
        }
        catch (Exception)
        {
            return local;
        }
    }

    public static async Task<CaptureClarifyResult> ContinueAsync(
        string captureText,
        string? projectName,
        string userReply,
        IReadOnlyList<string> priorUserReplies,
        string? hermesBaseUrl,
        string? hermesApiKey,
        List<HermesChatMessage> history,
        CancellationToken ct = default)
    {
        var local = CaptureClarify.Continue(captureText, projectName, priorUserReplies, userReply);
        try
        {
            if (!TryClient(hermesBaseUrl, hermesApiKey, out var client) || history.Count == 0)
            {
                return local;
            }

            using (client)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(6));
                var session = await client.EnsureSessionAsync(cancellationToken: timeout.Token).ConfigureAwait(false);

                history.Add(new HermesChatMessage { Role = "user", Content = userReply.Trim() });
                var raw = await CollectAsync(client, session, history, timeout.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return local;
                }

                history.Add(new HermesChatMessage { Role = "assistant", Content = raw });
                return CaptureClarify.TryParseAgentComplete(raw)
                       ?? CaptureClarifyResult.Incomplete(NormalizeDisplay(raw));
            }
        }
        catch (Exception)
        {
            return local;
        }
    }

    /// <summary>Force a DONE / TITLE / NOTE / SUMMARY finish when the user hits Done.</summary>
    public static async Task<CaptureClarifyResult> FinishAsync(
        string captureText,
        string? projectName,
        IReadOnlyList<string> userReplies,
        string? hermesBaseUrl,
        string? hermesApiKey,
        List<HermesChatMessage> history,
        CancellationToken ct = default)
    {
        var local = CaptureClarify.Finalize(captureText, projectName, userReplies);
        try
        {
            if (!TryClient(hermesBaseUrl, hermesApiKey, out var client) || history.Count == 0)
            {
                return local;
            }

            using (client)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(6));
                var session = await client.EnsureSessionAsync(cancellationToken: timeout.Token).ConfigureAwait(false);

                history.Add(new HermesChatMessage
                {
                    Role = "user",
                    Content = """
                        User hit Done. Finish now. Output ONLY:
                        DONE
                        TITLE: <short task title, under 80 chars — not the chat transcript>
                        NOTE: <one-line subtitle from the answers>
                        SUMMARY: <2-4 sentence task summary from the Q&A>
                        Do not put questions, replies, or the conversation into TITLE.
                        """,
                });

                var raw = await CollectAsync(client, session, history, timeout.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return local;
                }

                history.Add(new HermesChatMessage { Role = "assistant", Content = raw });
                var parsed = CaptureClarify.TryParseAgentComplete(raw);
                if (parsed is null || string.IsNullOrWhiteSpace(parsed.FinalTitle))
                {
                    return local;
                }

                // Fill missing note/summary from local compose so we never lose the answers.
                return CaptureClarifyResult.Complete(
                    parsed.FinalTitle!,
                    parsed.Message,
                    parsed.Note ?? local.Note,
                    parsed.Summary ?? local.Summary);
            }
        }
        catch (Exception)
        {
            return local;
        }
    }

    private static string SystemPrompt(string? projectName, string captureText) =>
        $"""
        You are Orbit's inline capture clarifier on project "{projectName ?? "unknown"}".
        Original capture line: "{captureText.Trim()}"

        Goal: turn a rough capture into (1) a short task TITLE and (2) a SUMMARY of the clarifications.
        Rules:
        - Ask at most ONE short clarifying question per turn (1-3 short lines total).
        - No markdown bullets. No tools.
        - Never paste the conversation, your questions, or the user's raw replies into the title.
        - When you have enough (or the user says they're done / answers enough), finish with EXACTLY:
          DONE
          TITLE: <final task title only, under 80 characters, actionable>
          NOTE: <one-line subtitle from what they clarified — owner, deadline, constraint>
          SUMMARY: <brief paragraph of the Q&A context for the task notes>
        - TITLE = the work item name. NOTE/SUMMARY = the details from the chat. Keep them separate.
        - TITLE must reflect what you and the user agreed — not the vague original if they clarified it.
        """;

    private static bool TryClient(string? hermesBaseUrl, string? hermesApiKey, out HermesHttpClient client)
    {
        client = null!;
        if (!HermesUrlValidation.TryValidate(hermesBaseUrl, out var url, out _))
        {
            return false;
        }

        client = new HermesHttpClient(new Uri(url!), hermesApiKey);
        return true;
    }

    private static async Task<string> CollectAsync(
        HermesHttpClient client,
        HermesSession session,
        IReadOnlyList<HermesChatMessage> history,
        CancellationToken ct)
    {
        var buffer = new StringBuilder();
        await foreach (var delta in client.StreamChatAsync(
                           new HermesChatRequest
                           {
                               SessionId = session.SessionId,
                               SessionKey = session.SessionKey,
                               Stream = true,
                               Messages = history.ToList(),
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

        return buffer.ToString().Trim();
    }

    private static string NormalizeDisplay(string raw)
    {
        var lines = raw
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.TrimStart('-', '*', '•', ' ', '\t'))
            .Where(l => l.Length > 0
                        && !l.Equals("DONE", StringComparison.OrdinalIgnoreCase)
                        && !l.StartsWith("TITLE:", StringComparison.OrdinalIgnoreCase)
                        && !l.StartsWith("NOTE:", StringComparison.OrdinalIgnoreCase)
                        && !l.StartsWith("SUBTITLE:", StringComparison.OrdinalIgnoreCase)
                        && !l.StartsWith("SUMMARY:", StringComparison.OrdinalIgnoreCase))
            .Take(4)
            .Select(l => l.Length <= 110 ? l : l[..109] + "…");
        var text = string.Join("\n", lines);
        return string.IsNullOrWhiteSpace(text) ? raw.Trim() : text;
    }
}
