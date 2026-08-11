using System.Security.Cryptography;
using System.Text;
using Orbit.Core.Host;
using Orbit.Infrastructure.Contacts;

namespace Orbit.Infrastructure.Email;

public sealed class EmailIngestionService
{
    private readonly EmailArtifactStore _store;
    private readonly MsgEmailParser _parser;
    private readonly string _generatedFilesRoot;
    private readonly EmailContactEnricher? _enricher;
    private readonly MultiProjectClaimSplitter? _claimSplitter;

    public EmailIngestionService(
        EmailArtifactStore store,
        MsgEmailParser parser,
        string generatedFilesRoot,
        EmailContactEnricher? enricher = null,
        MultiProjectClaimSplitter? claimSplitter = null)
    {
        _store = store;
        _parser = parser;
        _generatedFilesRoot = PathSafety.NormalizeFullPath(generatedFilesRoot);
        _enricher = enricher;
        _claimSplitter = claimSplitter;
        Directory.CreateDirectory(Path.Combine(_generatedFilesRoot, "emails"));
    }

    public EmailArtifactRecord IngestFromPath(string sourcePath, IReadOnlyList<string>? projectIds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullSource = PathSafety.NormalizeFullPath(sourcePath);
        if (!File.Exists(fullSource))
        {
            throw new FileNotFoundException("MSG file was not found.", fullSource);
        }

        if (!string.Equals(Path.GetExtension(fullSource), ".msg", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only .msg files are supported for email ingestion.", nameof(sourcePath));
        }

        var bytes = File.ReadAllBytes(fullSource);
        return IngestBytes(bytes, Path.GetFileName(fullSource), projectIds);
    }

    public EmailArtifactRecord IngestBytes(
        byte[] msgBytes,
        string? originalFileName = null,
        IReadOnlyList<string>? projectIds = null)
    {
        ArgumentNullException.ThrowIfNull(msgBytes);
        if (msgBytes.Length == 0)
        {
            throw new ArgumentException("MSG payload was empty.", nameof(msgBytes));
        }

        var contentHash = Convert.ToHexString(SHA256.HashData(msgBytes)).ToLowerInvariant();
        ParsedEmailMessage parsed;
        using (var stream = new MemoryStream(msgBytes, writable: false))
        {
            parsed = _parser.ParseStream(stream);
        }

        var existingId =
            (!string.IsNullOrWhiteSpace(parsed.InternetMessageId)
                ? _store.FindIdByInternetMessageId(parsed.InternetMessageId)
                : null)
            ?? _store.FindIdByContentHash(contentHash);

        var wasExisting = existingId is not null;
        var emailId = existingId ?? Guid.NewGuid().ToString("D");
        var emailDir = Path.Combine(_generatedFilesRoot, "emails", emailId);
        Directory.CreateDirectory(emailDir);
        var attachmentsDir = Path.Combine(emailDir, "attachments");
        Directory.CreateDirectory(attachmentsDir);

        var rawPath = Path.Combine(emailDir, "original.msg");
        File.WriteAllBytes(rawPath, msgBytes);

        string? bodyTextPath = null;
        if (!string.IsNullOrEmpty(parsed.BodyText))
        {
            bodyTextPath = Path.Combine(emailDir, "body.txt");
            File.WriteAllText(bodyTextPath, parsed.BodyText, Encoding.UTF8);
        }

        string? bodyHtmlPath = null;
        if (!string.IsNullOrEmpty(parsed.BodyHtml))
        {
            bodyHtmlPath = Path.Combine(emailDir, "body.html");
            File.WriteAllText(bodyHtmlPath, parsed.BodyHtml, Encoding.UTF8);
        }

        // Replace attachment copies for re-ingest of the same message.
        foreach (var existing in Directory.EnumerateFiles(attachmentsDir))
        {
            File.Delete(existing);
        }

        var attachmentRecords = new List<EmailAttachmentRecord>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attachment in parsed.Attachments)
        {
            var safeName = SanitizeFileName(attachment.FileName);
            var unique = MakeUniqueName(safeName, usedNames);
            usedNames.Add(unique);
            var dest = Path.Combine(attachmentsDir, unique);
            File.WriteAllBytes(dest, attachment.Data);
            attachmentRecords.Add(new EmailAttachmentRecord
            {
                FileName = unique,
                Path = dest,
                SizeBytes = attachment.Data.LongLength,
            });
        }

        var preview = BuildPreview(parsed.BodyText, parsed.BodyHtml, parsed.Subject);
        var artifact = new EmailArtifactRecord
        {
            Id = emailId,
            Subject = parsed.Subject,
            SentAt = parsed.SentAt?.UtcDateTime.ToString("O"),
            ReceivedAt = parsed.ReceivedAt?.UtcDateTime.ToString("O"),
            InternetMessageId = parsed.InternetMessageId,
            ConversationId = parsed.ConversationId,
            BodyPreview = preview,
            RawPath = rawPath,
            BodyTextPath = bodyTextPath,
            BodyHtmlPath = bodyHtmlPath,
            ContentHash = contentHash,
            Attachments = attachmentRecords,
            WasExisting = wasExisting,
        };

        _store.UpsertArtifact(artifact, parsed.Participants);

        if (projectIds is not null)
        {
            foreach (var projectId in projectIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
            {
                _store.LinkToProject(emailId, projectId, confidence: 1.0, matchReason: "explicit");
            }
        }

        ContactEnrichmentResult? enrichment = null;
        if (_enricher is not null)
        {
            var bodyForEnrich = !string.IsNullOrWhiteSpace(parsed.BodyText)
                ? parsed.BodyText
                : StripTags(parsed.BodyHtml ?? string.Empty);
            enrichment = _enricher.Enrich(
                emailId,
                parsed.Participants,
                bodyForEnrich,
                projectIds);
        }

        ClaimSplitResult? claimSplit = null;
        if (_claimSplitter is not null)
        {
            var bodyForClaims = !string.IsNullOrWhiteSpace(parsed.BodyText)
                ? parsed.BodyText
                : StripTags(parsed.BodyHtml ?? string.Empty);
            claimSplit = _claimSplitter.ProcessEmail(emailId, bodyForClaims, parsed.Subject);
        }

        var loaded = _store.Get(emailId)
            ?? throw new InvalidOperationException("Email artifact was not readable after ingest.");
        return loaded with
        {
            WasExisting = wasExisting,
            Attachments = attachmentRecords,
            EnrichedPersonIds = enrichment?.PersonIds ?? [],
            EnrichmentSuggestionCount = enrichment?.SuggestionCount ?? 0,
            ClaimExtractionCount = claimSplit?.CreatedExtractionIds.Count ?? 0,
            ClaimSuggestionId = claimSplit?.SuggestionId,
        };
    }

    private static string BuildPreview(string? bodyText, string? bodyHtml, string? subject)
    {
        var source = !string.IsNullOrWhiteSpace(bodyText)
            ? bodyText
            : !string.IsNullOrWhiteSpace(bodyHtml)
                ? StripTags(bodyHtml)
                : subject ?? string.Empty;
        source = source.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        if (source.Length <= 240)
        {
            return source;
        }

        return source[..240];
    }

    private static string StripTags(string html)
    {
        var sb = new StringBuilder(html.Length);
        var inTag = false;
        foreach (var ch in html)
        {
            if (ch == '<')
            {
                inTag = true;
                continue;
            }

            if (ch == '>')
            {
                inTag = false;
                continue;
            }

            if (!inTag)
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    private static string SanitizeFileName(string name)
    {
        var fileName = Path.GetFileName(string.IsNullOrWhiteSpace(name) ? "attachment.bin" : name);
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(c, '_');
        }

        return string.IsNullOrWhiteSpace(fileName) ? "attachment.bin" : fileName;
    }

    private static string MakeUniqueName(string fileName, HashSet<string> used)
    {
        if (!used.Contains(fileName))
        {
            return fileName;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; i < 10_000; i++)
        {
            var candidate = $"{stem}_{i}{ext}";
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"{stem}_{Guid.NewGuid():N}{ext}";
    }
}
