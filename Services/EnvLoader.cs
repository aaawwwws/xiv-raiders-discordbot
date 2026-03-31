using System;
using System.IO;

namespace RaidDcBot.Services;

public static class EnvLoader
{
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
    {
        "Discord__Token",
        "Database__ConnectionString",
        "FFLogs__Enabled",
        "FFLogs__ClientId",
        "FFLogs__ClientSecret"
    };

    public static void Load(string fileName = ".env")
    {
        var path = FindEnvPath(fileName);
        if (path is null)
        {
            return;
        }

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
            {
                line = line[7..].Trim();
            }

            var idx = line.IndexOf('=');
            if (idx <= 0)
            {
                continue;
            }

            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(key) || !AllowedKeys.Contains(key))
            {
                continue;
            }

            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    private static string? FindEnvPath(string fileName)
    {
        var searchRoots = new List<string>();
        AddIfMissing(searchRoots, Directory.GetCurrentDirectory());
        AddIfMissing(searchRoots, AppContext.BaseDirectory);

        foreach (var root in searchRoots)
        {
            var current = new DirectoryInfo(root);
            for (var depth = 0; depth < 8 && current is not null; depth++)
            {
                foreach (var candidate in EnumerateEnvCandidates(current.FullName, fileName))
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                current = current.Parent;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateEnvCandidates(string baseDir, string fileName)
    {
        yield return Path.Combine(baseDir, fileName);
        yield return Path.Combine(baseDir, "xiv-raiders-discordbot", fileName);
        yield return Path.Combine(baseDir, "discordbot", fileName);
        yield return Path.Combine(baseDir, "RaidDcBot", fileName);
        yield return Path.Combine(baseDir, "src", "xiv-raiders-discordbot", fileName);
        yield return Path.Combine(baseDir, "src", "discordbot", fileName);
        yield return Path.Combine(baseDir, "src", "RaidDcBot", fileName);
    }

    private static void AddIfMissing(ICollection<string> values, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var normalized = Path.GetFullPath(path);
        if (!values.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            values.Add(normalized);
        }
    }
}
