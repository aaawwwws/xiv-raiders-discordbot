namespace RaidDcBot.Services;

public static class DcResolver
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> DcToWorlds = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
    {
        ["Meteor"] = new[]
        {
            "Belias",
            "Mandragora",
            "Ramuh",
            "Shinryu",
            "Unicorn",
            "Valefor",
            "Yojimbo",
            "Zeromus"
        },
        ["Gaia"] = new[]
        {
            "Alexander",
            "Bahamut",
            "Durandal",
            "Fenrir",
            "Ifrit",
            "Ridill",
            "Tiamat",
            "Ultima"
        },
        ["Mana"] = new[]
        {
            "Anima",
            "Asura",
            "Chocobo",
            "Hades",
            "Ixion",
            "Masamune",
            "Pandaemonium",
            "Titan"
        },
        ["Elemental"] = new[]
        {
            "Aegis",
            "Atomos",
            "Carbuncle",
            "Garuda",
            "Gungnir",
            "Kujata",
            "Tonberry",
            "Typhon"
        }
    };
    private static readonly IReadOnlyDictionary<string, string> WorldToDc = BuildWorldToDcMap();

    public static string? Resolve(string? dataCenter, string world)
    {
        if (TryNormalizeDataCenter(dataCenter, out var normalized))
        {
            return normalized;
        }

        if (WorldToDc.TryGetValue(world.Trim(), out var dc))
        {
            return dc;
        }

        return null;
    }

    public static IReadOnlyList<string> KnownDcNames => DcToWorlds.Keys.ToArray();

    public static bool TryGetWorlds(string? dataCenter, out IReadOnlyList<string> worlds)
    {
        worlds = Array.Empty<string>();
        if (!TryNormalizeDataCenter(dataCenter, out var normalized))
        {
            return false;
        }

        if (!DcToWorlds.TryGetValue(normalized, out var found))
        {
            return false;
        }

        worlds = found;
        return true;
    }

    public static bool TryNormalizeDataCenter(string? dataCenter, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(dataCenter))
        {
            return false;
        }

        var trimmed = dataCenter.Trim();
        var match = DcToWorlds.Keys.FirstOrDefault(dc => string.Equals(dc, trimmed, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return false;
        }

        normalized = match;
        return true;
    }

    private static IReadOnlyDictionary<string, string> BuildWorldToDcMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in DcToWorlds)
        {
            foreach (var world in pair.Value)
            {
                map[world] = pair.Key;
            }
        }

        return map;
    }
}
