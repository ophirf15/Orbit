using Orbit.Infrastructure.Malleability;

namespace Orbit.Tests.Malleability;

public sealed class DeveloperSourceServiceTests
{
    [Fact]
    public void EnsureWritableUnderRepo_DeniesProjectFolderPath()
    {
        using var temp = new TempRoots();
        var projectFolder = Path.Combine(temp.Root, "projects", "Harbor Court");
        Directory.CreateDirectory(projectFolder);
        Directory.CreateDirectory(temp.RepoRoot);

        var service = new DeveloperSourceService(
            developerMode: () => true,
            sourceRepoRoot: () => temp.RepoRoot,
            developerRemoteOverride: () => false,
            projectFolderRoots: () => [projectFolder],
            runProcess: (_, _, _, _) => new DeveloperSourceService.ProcessRunResult(0, "ok"));

        var outside = Path.Combine(projectFolder, "secret.txt");
        var ex = Assert.Throws<DeveloperSourceDeniedException>(() => service.EnsureWritableUnderRepo(outside));
        Assert.Contains("outside", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateBranch_Telegram_DeniedUnlessOverride()
    {
        using var temp = new TempRoots();
        Directory.CreateDirectory(temp.RepoRoot);
        var service = new DeveloperSourceService(
            () => true,
            () => temp.RepoRoot,
            () => false,
            runProcess: (_, _, _, _) => new DeveloperSourceService.ProcessRunResult(0, "ok"));

        var denied = Assert.Throws<DeveloperSourceDeniedException>(() =>
            service.CreateBranch("feat/test", channel: "telegram"));
        Assert.Contains("Telegram", denied.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateBranch_Telegram_AllowedWithOverride()
    {
        using var temp = new TempRoots();
        Directory.CreateDirectory(temp.RepoRoot);
        string? capturedBranch = null;
        var service = new DeveloperSourceService(
            () => true,
            () => temp.RepoRoot,
            () => true,
            runProcess: (_, args, _, _) =>
            {
                capturedBranch = args[^1];
                return new DeveloperSourceService.ProcessRunResult(0, "Switched");
            });

        var result = service.CreateBranch("feat/allowed", channel: "telegram");
        Assert.Equal("feat/allowed", result.BranchName);
        Assert.Equal("feat/allowed", capturedBranch);
    }

    [Fact]
    public void CreateBranch_RequiresDeveloperMode()
    {
        using var temp = new TempRoots();
        Directory.CreateDirectory(temp.RepoRoot);
        var service = new DeveloperSourceService(
            () => false,
            () => temp.RepoRoot,
            () => false,
            runProcess: (_, _, _, _) => new DeveloperSourceService.ProcessRunResult(0, "ok"));

        Assert.Throws<DeveloperSourceDeniedException>(() => service.CreateBranch("feat/x"));
    }

    [Fact]
    public void WriteFile_OnlyUnderRepo()
    {
        using var temp = new TempRoots();
        Directory.CreateDirectory(temp.RepoRoot);
        var service = new DeveloperSourceService(
            () => true,
            () => temp.RepoRoot,
            () => false,
            runProcess: (_, _, _, _) => new DeveloperSourceService.ProcessRunResult(0, "ok"));

        var written = service.WriteFileUnderRepo("notes/hello.txt", "hi");
        Assert.True(File.Exists(written.FullPath));
        Assert.Equal("hi", File.ReadAllText(written.FullPath));

        var projectPath = Path.Combine(temp.Root, "elsewhere", "file.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        Assert.Throws<DeveloperSourceDeniedException>(() =>
            service.WriteFileUnderRepo(projectPath, "nope"));
    }

    [Fact]
    public void PropertyOnboardingSkill_Exists()
    {
        var repoHint = FindRepoRoot();
        var skill = Path.Combine(repoHint, "docs", "hermes", "skills", "property-onboarding.md");
        Assert.True(File.Exists(skill), $"Missing skill file: {skill}");
        var text = File.ReadAllText(skill);
        Assert.Contains("orbit_add_custom_field", text, StringComparison.Ordinal);
        Assert.Contains("grant OS permissions", text, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Orbit.sln"))
                || File.Exists(Path.Combine(dir.FullName, "build.ps1")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        // Fallback: walk up from current directory
        dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "build.ps1")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate Orbit repo root for skill file assertion.");
    }

    private sealed class TempRoots : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "OrbitDevSourceTests", Guid.NewGuid().ToString("N"));

        public string RepoRoot => Path.Combine(Root, "repo");

        public TempRoots() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
                // best-effort
            }
        }
    }
}
