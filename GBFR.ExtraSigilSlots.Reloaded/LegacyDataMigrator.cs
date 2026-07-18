namespace GBFR.ExtraSigilSlots.Reloaded;

internal static class LegacyDataMigrator
{
    private const string LegacyModDirectoryName = "GBFR.ExtraSigilSlots20.Reloaded";
    private const string ConfigFileName = "GBFR-ExtraSigilSlotsNumConfig.ini";
    private const string PresetFileName = "GBFR-ExtraSigilSlots.presets.json";
    private const string LegacyPresetFileName = "GBFR-ExtraSigilSlots20.presets.json";
    private const string LegacyConfigFileName = "GBFR-ExtraSigilSlots20.ini";

    private const string PackagedDefaultConfig =
        "[Settings]\n" +
        "ConfigVersion=2\n" +
        "ToggleKey=119\n" +
        "ShowEquipped=0\n" +
        "AutoApply=1\n" +
        "Language=zh-CN\n" +
        "VirtualSlotCount=8";

    internal static void Migrate(string modDirectory, Action<string> log)
    {
        try
        {
            string currentDirectory = Path.GetFullPath(modDirectory);
            string? modsDirectory = Directory.GetParent(currentDirectory)?.FullName;
            string? legacyDirectory = modsDirectory is null
                ? null
                : Path.Combine(modsDirectory, LegacyModDirectoryName);

            if (legacyDirectory is not null && Directory.Exists(legacyDirectory))
            {
                log(
                    $"Legacy mod directory detected at {legacyDirectory}; " +
                    "disable or remove its old ModId after migration to prevent double loading.");
            }

            MigrateConfig(currentDirectory, legacyDirectory, log);
            MigratePresets(currentDirectory, legacyDirectory, log);
        }
        catch (Exception exception)
        {
            log($"Legacy data migration was skipped after an error: {exception}");
        }
    }

    private static void MigrateConfig(
        string currentDirectory,
        string? legacyDirectory,
        Action<string> log)
    {
        string destination = Path.Combine(currentDirectory, ConfigFileName);
        bool destinationCanBeReplaced = !File.Exists(destination) || IsPackagedDefault(destination);
        if (!destinationCanBeReplaced)
            return;

        foreach (string source in ConfigCandidates(currentDirectory, legacyDirectory))
        {
            if (!File.Exists(source) || PathsEqual(source, destination))
                continue;
            if (File.Exists(destination) && IsPackagedDefault(source))
                continue;

            File.Copy(source, destination, overwrite: true);
            log($"Migrated legacy settings and character selections from {source}.");
            return;
        }
    }

    private static void MigratePresets(
        string currentDirectory,
        string? legacyDirectory,
        Action<string> log)
    {
        string destination = Path.Combine(currentDirectory, PresetFileName);
        if (File.Exists(destination))
            return;

        foreach (string source in PresetCandidates(currentDirectory, legacyDirectory))
        {
            if (!File.Exists(source) || PathsEqual(source, destination))
                continue;

            File.Copy(source, destination, overwrite: false);
            log($"Migrated legacy named presets from {source}.");
            return;
        }
    }

    private static IEnumerable<string> ConfigCandidates(
        string currentDirectory,
        string? legacyDirectory)
    {
        if (legacyDirectory is not null && Directory.Exists(legacyDirectory))
        {
            yield return Path.Combine(legacyDirectory, ConfigFileName);
            yield return Path.Combine(legacyDirectory, LegacyConfigFileName);
        }
        yield return Path.Combine(currentDirectory, LegacyConfigFileName);
    }

    private static IEnumerable<string> PresetCandidates(
        string currentDirectory,
        string? legacyDirectory)
    {
        if (legacyDirectory is not null && Directory.Exists(legacyDirectory))
        {
            yield return Path.Combine(legacyDirectory, LegacyPresetFileName);
            yield return Path.Combine(legacyDirectory, PresetFileName);
        }
        yield return Path.Combine(currentDirectory, LegacyPresetFileName);
    }

    private static bool IsPackagedDefault(string path)
    {
        string normalized = File.ReadAllText(path)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .TrimStart('\uFEFF')
            .Trim();
        return string.Equals(normalized, PackagedDefaultConfig, StringComparison.Ordinal);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);
}
