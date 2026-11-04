namespace MeetMateBot.Infrastructure.Configuration;

internal static class FirestoreCredentialsPathResolver
{
    public static string? Resolve(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        if (File.Exists(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var candidatePaths = new List<string>();

        if (!Path.IsPathRooted(configuredPath))
        {
            candidatePaths.Add(Path.Combine(AppContext.BaseDirectory, configuredPath));
            candidatePaths.Add(Path.Combine(Environment.CurrentDirectory, configuredPath));
        }

        var fileName = Path.GetFileName(configuredPath);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            foreach (var root in EnumerateAncestorDirectories(AppContext.BaseDirectory))
            {
                candidatePaths.Add(Path.Combine(root, fileName));
            }

            foreach (var root in EnumerateAncestorDirectories(Environment.CurrentDirectory))
            {
                candidatePaths.Add(Path.Combine(root, fileName));
            }
        }

        return candidatePaths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> EnumerateAncestorDirectories(string startPath)
    {
        var directory = new DirectoryInfo(startPath);
        while (directory is not null)
        {
            yield return directory.FullName;
            directory = directory.Parent;
        }
    }
}
