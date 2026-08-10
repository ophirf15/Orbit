namespace Orbit.Tests.Email;

/// <summary>Resolves the committed minimal .msg under tests/fixtures.</summary>
public static class MsgFixtureFactory
{
    public static string GetSampleMsgPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "fixtures", "sample.msg"),
            Path.Combine(AppContext.BaseDirectory, "sample.msg"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", "sample.msg")),
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException(
            "Missing tests/fixtures/sample.msg (copy to test output as fixtures/sample.msg).");
    }

    public static string CopySampleMsg(string directory, string fileName = "sample.msg")
    {
        Directory.CreateDirectory(directory);
        var dest = Path.Combine(directory, fileName);
        File.Copy(GetSampleMsgPath(), dest, overwrite: true);
        return dest;
    }
}
