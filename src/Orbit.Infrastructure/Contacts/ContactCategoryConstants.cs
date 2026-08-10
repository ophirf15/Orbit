namespace Orbit.Infrastructure.Contacts;

public static class ContactCategories
{
    public const string Company = "company";
    public const string Client = "client";
    public const string Vendor = "vendor";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Company, Client, Vendor,
    };

    public static bool IsValid(string? value) =>
        string.IsNullOrWhiteSpace(value) || All.Contains(value.Trim());
}

public static class ContactDispositions
{
    public const string Active = "active";
    public const string FlaggedResident = "flagged_resident";
    public const string ExcludedResident = "excluded_resident";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Active, FlaggedResident, ExcludedResident,
    };

    public static bool IsValid(string? value) =>
        !string.IsNullOrWhiteSpace(value) && All.Contains(value.Trim());
}
