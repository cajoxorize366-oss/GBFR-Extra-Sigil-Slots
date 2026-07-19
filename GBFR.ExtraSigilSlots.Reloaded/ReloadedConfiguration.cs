using Reloaded.Mod.Interfaces;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GBFR.ExtraSigilSlots.Reloaded;

public abstract class ReloadedConfigurable<TConfig> : IUpdatableConfigurable
    where TConfig : ReloadedConfigurable<TConfig>, new()
{
    private static readonly object s_readLock = new();
    private FileSystemWatcher? _watcher;
    private int _disposed;

    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    [Browsable(false)]
    public event Action<IUpdatableConfigurable>? ConfigurationUpdated;

    [JsonIgnore]
    [Browsable(false)]
    public string FilePath { get; private set; } = string.Empty;

    [JsonIgnore]
    [Browsable(false)]
    public string ConfigName { get; private set; } = string.Empty;

    [JsonIgnore]
    [Browsable(false)]
    public Action? Save { get; private set; }

    protected virtual void Normalize()
    {
    }

    public static TConfig FromFile(string filePath, string configName)
    {
        TConfig configuration;
        try
        {
            configuration = ReadStrict(filePath);
        }
        catch
        {
            configuration = new TConfig();
        }

        configuration.Normalize();
        configuration.Initialize(filePath, configName);
        return configuration;
    }

    public void DisposeEvents()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        FileSystemWatcher? watcher = Interlocked.Exchange(ref _watcher, null);
        if (watcher is not null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        ConfigurationUpdated = null;
    }

    private void Initialize(string filePath, string configName)
    {
        FilePath = filePath;
        ConfigName = configName;
        Save = SaveToDisk;

        string directory = Path.GetDirectoryName(filePath)
            ?? throw new InvalidOperationException("The configuration path has no directory.");
        Directory.CreateDirectory(directory);

        FileSystemWatcher watcher = new(directory, Path.GetFileName(filePath))
        {
            NotifyFilter = NotifyFilters.FileName |
                NotifyFilters.LastWrite |
                NotifyFilters.Size,
        };
        watcher.Changed += OnConfigurationFileChanged;
        watcher.Created += OnConfigurationFileChanged;
        watcher.Renamed += OnConfigurationFileRenamed;
        watcher.EnableRaisingEvents = true;
        _watcher = watcher;
    }

    private void SaveToDisk()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        TConfig configuration = (TConfig)this;
        configuration.Normalize();
        string directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = FilePath + ".tmp";
        FileSystemWatcher? watcher = _watcher;

        try
        {
            if (watcher is not null)
                watcher.EnableRaisingEvents = false;
            string json = JsonSerializer.Serialize(configuration, SerializerOptions);
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // A leftover temporary file does not invalidate the saved config.
            }

            if (watcher is not null && Volatile.Read(ref _disposed) == 0)
                watcher.EnableRaisingEvents = true;
        }
    }

    private void OnConfigurationFileChanged(object sender, FileSystemEventArgs args) =>
        ReloadFromDisk();

    private void OnConfigurationFileRenamed(object sender, RenamedEventArgs args) =>
        ReloadFromDisk();

    private void ReloadFromDisk()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        lock (s_readLock)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            TConfig? updated = null;
            for (int attempt = 0; attempt < 25; ++attempt)
            {
                try
                {
                    updated = ReadStrict(FilePath);
                    break;
                }
                catch when (attempt < 24)
                {
                    Thread.Sleep(10);
                }
                catch
                {
                    return;
                }
            }

            if (updated is null)
                return;

            updated.Normalize();
            updated.Initialize(FilePath, ConfigName);
            Action<IUpdatableConfigurable>? handlers = ConfigurationUpdated;
            updated.ConfigurationUpdated = handlers;
            DisposeEvents();
            if (handlers is null)
                return;

            foreach (Action<IUpdatableConfigurable> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(updated);
                }
                catch
                {
                    // One configuration consumer must not crash the game or
                    // prevent the remaining consumers from receiving the update.
                }
            }
        }
    }

    private static TConfig ReadStrict(string filePath)
    {
        if (!File.Exists(filePath))
            return new TConfig();

        return JsonSerializer.Deserialize<TConfig>(
                File.ReadAllBytes(filePath),
                SerializerOptions)
            ?? new TConfig();
    }
}

public sealed class Configurator : IConfiguratorV3
{
    private IUpdatableConfigurable[]? _configurations;

    public Configurator()
    {
    }

    public Configurator(string configDirectory)
    {
        ConfigFolder = configDirectory;
    }

    public string? ModFolder { get; private set; }

    public string? ConfigFolder { get; private set; }

    public ConfiguratorContext Context { get; private set; }

    public IUpdatableConfigurable[] Configurations =>
        _configurations ??= MakeConfigurations();

    public TConfig GetConfiguration<TConfig>(int index) =>
        (TConfig)Configurations[index];

    public void SetModDirectory(string modDirectory)
    {
        ModFolder = modDirectory;
        ConfigFolder ??= modDirectory;
    }

    public void SetConfigDirectory(string configDirectory)
    {
        ConfigFolder = configDirectory;
    }

    public void SetContext(in ConfiguratorContext context)
    {
        Context = context;
    }

    public void Migrate(string oldDirectory, string newDirectory)
    {
        try
        {
            string oldPath = Path.Combine(oldDirectory, HotkeyConfig.FileName);
            string newPath = Path.Combine(newDirectory, HotkeyConfig.FileName);
            if (!File.Exists(oldPath) || File.Exists(newPath))
                return;

            Directory.CreateDirectory(newDirectory);
            File.Move(oldPath, newPath);
        }
        catch
        {
            // Reloaded-II may retry migration; leaving the old file is safe.
        }
    }

    public IConfigurable[] GetConfigurations() => Configurations;

    public bool TryRunCustomConfiguration() => false;

    private IUpdatableConfigurable[] MakeConfigurations()
    {
        string directory = ConfigFolder ?? ModFolder ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(directory);
        IUpdatableConfigurable[] configurations =
        [
            HotkeyConfig.FromFile(
                Path.Combine(directory, HotkeyConfig.FileName),
                HotkeyConfig.ConfigurationName),
        ];
        configurations[0].ConfigurationUpdated += updated =>
            configurations[0] = updated;
        return configurations;
    }
}
