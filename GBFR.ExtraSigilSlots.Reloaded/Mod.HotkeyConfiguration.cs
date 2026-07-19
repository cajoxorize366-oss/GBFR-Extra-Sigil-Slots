using Reloaded.Mod.Interfaces;

namespace GBFR.ExtraSigilSlots.Reloaded;

public sealed partial class Mod
{
    private HotkeyConfig? _hotkeyConfiguration;

    private void InitializeHotkeyConfiguration(
        IModLoader loader,
        string modId,
        int nativeToggleKey)
    {
        string configDirectory = loader.GetModConfigDirectory(modId);
        Directory.CreateDirectory(configDirectory);
        string configPath = Path.Combine(configDirectory, HotkeyConfig.FileName);
        bool configExists = File.Exists(configPath);

        Configurator configurator = new(configDirectory);
        HotkeyConfig configuration = configurator.GetConfiguration<HotkeyConfig>(0);
        if (!configExists)
        {
            configuration.MenuHotkey = HotkeyConfig.FromVirtualKey(nativeToggleKey);
            configuration.Save?.Invoke();
        }

        configuration.ConfigurationUpdated += OnHotkeyConfigurationUpdated;
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                configuration.DisposeEvents();
                return;
            }
            _hotkeyConfiguration = configuration;
        }

        ApplyHotkeyConfiguration(configuration, logChange: false);
    }

    private void OnHotkeyConfigurationUpdated(IUpdatableConfigurable configurable)
    {
        if (configurable is not HotkeyConfig configuration)
            return;

        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                configuration.DisposeEvents();
                return;
            }
            _hotkeyConfiguration = configuration;
        }

        ApplyHotkeyConfiguration(configuration, logChange: true);
    }

    private void ApplyHotkeyConfiguration(
        HotkeyConfig configuration,
        bool logChange)
    {
        lock (_lifecycleLock)
        {
            if (_disposed || !_nativeCoreActive)
                return;
        }

        int virtualKey = configuration.VirtualKey;
        try
        {
            if (!NativeCore.SetToggleKey(virtualKey))
            {
                Log($"Reloaded-II rejected overlay hotkey {configuration.KeyName}.");
                return;
            }

            int previousKey = FrontendOverlayGate.CurrentToggleKey;
            FrontendOverlayGate.SetToggleKey(virtualKey);
            if (logChange && previousKey != virtualKey)
            {
                Log(
                    $"Overlay hotkey changed to {configuration.KeyName} " +
                    "through Reloaded-II configuration.");
            }
        }
        catch (Exception exception)
        {
            Log($"Could not apply Reloaded-II overlay hotkey: {exception.Message}");
        }
    }

    private string GetConfiguredHotkeyName()
    {
        lock (_lifecycleLock)
            return _hotkeyConfiguration?.KeyName ?? nameof(OverlayHotkey.F8);
    }

    private HotkeyConfig? DetachHotkeyConfiguration()
    {
        lock (_lifecycleLock)
        {
            HotkeyConfig? configuration = _hotkeyConfiguration;
            _hotkeyConfiguration = null;
            return configuration;
        }
    }
}
