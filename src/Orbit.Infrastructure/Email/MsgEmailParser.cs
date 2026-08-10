using System.Text;
using System.Text.RegularExpressions;
using MsgReader.Outlook;

namespace Orbit.Infrastructure.Email;

/// <summary>
/// Parses Outlook .msg files via MSGReader (no Outlook / Graph required).
/// </summary>
public sealed class MsgEmailParser
{
    static MsgEmailParser()
    {
        // MSGReader RTF/HTML de-encapsulation needs Windows-1252 and siblings.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static readonly Regex MessageIdHeader = new(
        @"^Message-ID:\s*(?<id>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public ParsedEmailMessage ParseFile(string msgPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(msgPath);
        if (!File.Exists(msgPath))
        {
            throw new FileNotFoundException("MSG file was not found.", msgPath);
        }

        using var message = new Storage.Message(msgPath, FileAccess.Read);
        return ParseMessage(message);
    }

    public ParsedEmailMessage ParseStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var message = new Storage.Message(stream, FileAccess.Read, leaveStreamOpen: true);
        return ParseMessage(message);
    }

    private static ParsedEmailMessage ParseMessage(Storage.Message message)
    {
        var headers = message.TransportMessageHeaders ?? string.Empty;
        var internetMessageId = FirstNonEmpty(
            message.Headers?.MessageId,
            ExtractHeader(MessageIdHeader, headers));
        var conversationId = FirstNonEmpty(message.ConversationId, message.ConversationTopic);

        var participants = new List<ParsedEmailParticipant>();
        if (message.Sender is not null)
        {
            var address = FirstNonEmpty(message.Sender.Email, message.Sender.DisplayName) ?? "unknown";
            participants.Add(new ParsedEmailParticipant
            {
                Role = "from",
                Address = address,
                DisplayName = message.Sender.DisplayName,
            });
        }

        AddRecipients(participants, message, RecipientType.To, "to");
        AddRecipients(participants, message, RecipientType.Cc, "cc");
        AddRecipients(participants, message, RecipientType.Bcc, "bcc");

        var attachments = new List<ParsedEmailAttachment>();
        if (message.Attachments is not null)
        {
            foreach (var item in message.Attachments)
            {
                if (item is not Storage.Attachment att)
                {
                    continue;
                }

                var name = string.IsNullOrWhiteSpace(att.FileName) ? "attachment.bin" : att.FileName;
                var data = att.Data ?? [];
                if (data.Length == 0)
                {
                    continue;
                }

                attachments.Add(new ParsedEmailAttachment
                {
                    FileName = Path.GetFileName(name),
                    Data = data,
                    ContentType = att.MimeType,
                });
            }
        }

        return new ParsedEmailMessage
        {
            Subject = message.Subject,
            SentAt = message.SentOn,
            ReceivedAt = message.ReceivedOn,
            InternetMessageId = NormalizeMessageId(internetMessageId),
            ConversationId = string.IsNullOrWhiteSpace(conversationId) ? null : conversationId.Trim(),
            BodyText = message.BodyText,
            BodyHtml = message.BodyHtml,
            Participants = participants,
            Attachments = attachments,
        };
    }

    private static void AddRecipients(
        List<ParsedEmailParticipant> participants,
        Storage.Message message,
        RecipientType type,
        string role)
    {
        if (message.Recipients is null)
        {
            return;
        }

        foreach (var recipient in message.Recipients)
        {
            if (recipient.Type != type)
            {
                continue;
            }

            var address = FirstNonEmpty(recipient.Email, recipient.DisplayName);
            if (string.IsNullOrWhiteSpace(address))
            {
                continue;
            }

            participants.Add(new ParsedEmailParticipant
            {
                Role = role,
                Address = address,
                DisplayName = recipient.DisplayName,
            });
        }
    }

    private static string? ExtractHeader(Regex regex, string headers)
    {
        var match = regex.Match(headers);
        return match.Success ? match.Groups["id"].Value.Trim() : null;
    }

    private static string? NormalizeMessageId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().Trim('<', '>');
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}
