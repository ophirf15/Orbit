using Orbit.Core.Settings;
using Orbit.Core.Shell;

namespace Orbit.Tests.Settings;

public sealed class ThemeMappingTests
{
    [Theory]
    [InlineData(ThemePreference.System)]
    [InlineData(ThemePreference.Light)]
    [InlineData(ThemePreference.Dark)]
    public void Normalize_DefinedValues_Unchanged(ThemePreference preference)
    {
        Assert.Equal(preference, ThemeMapping.Normalize(preference));
    }

    [Fact]
    public void Normalize_Invalid_CoercesToSystem()
    {
        Assert.Equal(ThemePreference.System, ThemeMapping.Normalize((ThemePreference)99));
    }

    [Fact]
    public void FollowsSystem_OnlyForSystem()
    {
        Assert.True(ThemeMapping.FollowsSystem(ThemePreference.System));
        Assert.False(ThemeMapping.FollowsSystem(ThemePreference.Dark));
        Assert.False(ThemeMapping.FollowsSystem(ThemePreference.Light));
    }
}

public sealed class HermesUrlValidationTests
{
    [Fact]
    public void TryValidate_AcceptsHttpLocalhost()
    {
        Assert.True(HermesUrlValidation.TryValidate("http://127.0.0.1:8642/", out var normalized, out _));
        Assert.Equal("http://127.0.0.1:8642", normalized);
    }

    [Fact]
    public void TryValidate_RejectsGarbage()
    {
        Assert.False(HermesUrlValidation.TryValidate("not-a-url", out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}

public sealed class CommandCatalogTests
{
    [Fact]
    public void GetAll_ContainsStableNavIds()
    {
        var ids = CommandCatalog.GetAll().Select(c => c.Id).ToHashSet();
        Assert.Contains(CommandCatalog.Workbench, ids);
        Assert.Contains(CommandCatalog.Agent, ids);
        Assert.Contains(CommandCatalog.Files, ids);
        Assert.Contains(CommandCatalog.Settings, ids);
        Assert.Contains(CommandCatalog.About, ids);
        Assert.DoesNotContain(CommandCatalog.People, ids);
        Assert.DoesNotContain(CommandCatalog.Emails, ids);
    }

    [Fact]
    public void Filter_Empty_ReturnsAll()
    {
        Assert.Equal(CommandCatalog.GetAll().Count, CommandCatalog.Filter("").Count);
        Assert.Equal(CommandCatalog.GetAll().Count, CommandCatalog.Filter(null).Count);
    }

    [Fact]
    public void Filter_SettingsToken_MatchesSettings()
    {
        var hits = CommandCatalog.Filter("set");
        Assert.Contains(hits, c => c.Id == CommandCatalog.Settings);
    }

    [Fact]
    public void Filter_NoMatch_Empty()
    {
        Assert.Empty(CommandCatalog.Filter("zzzxxyyqq"));
    }
}
