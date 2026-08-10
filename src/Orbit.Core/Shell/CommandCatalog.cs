namespace Orbit.Core.Shell;

public sealed record ShellCommand(string Id, string Title, string Keywords);

/// <summary>
/// Shell command catalog for the command palette (nav + quick capture).
/// </summary>
public static class CommandCatalog
{
    public const string Pulse = "nav.pulse";
    public const string Ignition = "nav.ignition";
    public const string Workbench = "nav.workbench";
    public const string Agent = "nav.agent";
    /// <summary>Legacy route id — contacts live on the workbench drawer now.</summary>
    public const string People = "nav.people";
    public const string Files = "nav.files";
    public const string Search = "nav.search";
    /// <summary>Legacy route id — email context lives on the workbench drawer now.</summary>
    public const string Emails = "nav.emails";
    public const string Settings = "nav.settings";
    public const string About = "nav.about";
    public const string ToggleTheme = "theme.toggle";
    public const string QuickCapture = "capture.quick";
    public const string PushOutlook = "mail.push_outlook";

    private static readonly IReadOnlyList<ShellCommand> All =
    [
        new(Pulse, "Go to Pulse", "home pulse day brief concerns jarvis"),
        new(Ignition, "Go to Ignition", "ignition setup orbit projects"),
        new(Workbench, "Go to Board", "workbench board projects"),
        new(Agent, "Go to Hermes", "agent hermes chat assistant"),
        new(Files, "Go to Files", "files folders documents"),
        new(Search, "Go to Search", "search find evidence preview"),
        new(Settings, "Go to Settings", "settings preferences options"),
        new(About, "Go to About", "about diagnostics version"),
        new(ToggleTheme, "Toggle light/dark theme", "theme dark light appearance"),
        new(QuickCapture, "Quick capture to Limbo", "capture limbo note quick add"),
        new(PushOutlook, "Push selected Outlook mail", "outlook email push ingest hermes duty"),
    ];

    public static IReadOnlyList<ShellCommand> GetAll() => All;

    public static IReadOnlyList<ShellCommand> Filter(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return All;
        }

        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return All
            .Where(command => tokens.All(token =>
                command.Title.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                command.Keywords.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                command.Id.Contains(token, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }
}
