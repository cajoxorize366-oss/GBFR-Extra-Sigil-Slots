using Reloaded.Mod.Interfaces;
using System.Reflection;
using System.Text.Json;

if (args.Length != 1)
    throw new ArgumentException("Pass the managed build output directory.");

string outputDirectory = Path.GetFullPath(args[0]);
string assemblyPath = Path.Combine(outputDirectory, "GBFR.ExtraSigilSlots.Reloaded.dll");
Assembly assembly = Assembly.LoadFrom(assemblyPath);
Type configuratorType = assembly.GetType(
    "GBFR.ExtraSigilSlots.Reloaded.Configurator",
    throwOnError: true)!;
Type configType = assembly.GetType(
    "GBFR.ExtraSigilSlots.Reloaded.HotkeyConfig",
    throwOnError: true)!;
Type hotkeyType = assembly.GetType(
    "GBFR.ExtraSigilSlots.Reloaded.OverlayHotkey",
    throwOnError: true)!;
PropertyInfo hotkeyProperty = configType.GetProperty("MenuHotkey")
    ?? throw new MissingMemberException(configType.FullName, "MenuHotkey");
PropertyInfo saveProperty = configType.GetProperty("Save")
    ?? throw new MissingMemberException(configType.FullName, "Save");
MethodInfo disposeEvents = configType.GetMethod("DisposeEvents")
    ?? throw new MissingMethodException(configType.FullName, "DisposeEvents");

string temporaryDirectory = Path.Combine(
    Path.GetTempPath(),
    "GBFR-ExtraSigilSlots-HotkeyConfigHarness-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryDirectory);

List<object> configurationsToDispose = [];
try
{
    IConfiguratorV3 runtimeConfigurator = CreateConfigurator();
    IUpdatableConfigurable runtimeConfig =
        (IUpdatableConfigurable)runtimeConfigurator.GetConfigurations()[0];
    configurationsToDispose.Add(runtimeConfig);
    Assert(ReadVirtualKey(runtimeConfig) == 0x77,
        "A missing Reloaded-II config must default to F8.");

    using ManualResetEventSlim configurationChanged = new(false);
    IUpdatableConfigurable? updatedRuntimeConfig = null;
    runtimeConfig.ConfigurationUpdated += _ =>
        throw new InvalidOperationException("Intentional subscriber failure.");
    runtimeConfig.ConfigurationUpdated += updated =>
    {
        updatedRuntimeConfig = updated;
        configurationChanged.Set();
    };

    IConfiguratorV3 editorConfigurator = CreateConfigurator();
    IUpdatableConfigurable editorConfig =
        (IUpdatableConfigurable)editorConfigurator.GetConfigurations()[0];
    configurationsToDispose.Add(editorConfig);
    hotkeyProperty.SetValue(editorConfig, Enum.ToObject(hotkeyType, 0x75));
    ((Action?)saveProperty.GetValue(editorConfig))?.Invoke();

    Assert(configurationChanged.Wait(TimeSpan.FromSeconds(3)),
        "A failing subscriber must not block later ConfigurationUpdated subscribers.");
    Assert(updatedRuntimeConfig is not null && ReadVirtualKey(updatedRuntimeConfig) == 0x75,
        "The live Reloaded-II update must carry F6.");
    configurationsToDispose.Add(updatedRuntimeConfig!);

    string configPath = Path.Combine(temporaryDirectory, "HotkeyConfig.json");
    using (JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(configPath)))
    {
        Assert(document.RootElement.GetProperty("MenuHotkey").GetString() == "F6",
            "The hotkey must be serialized as a readable enum name.");
    }

    IConfiguratorV3 reloadedConfigurator = CreateConfigurator();
    IUpdatableConfigurable reloadedConfig =
        (IUpdatableConfigurable)reloadedConfigurator.GetConfigurations()[0];
    configurationsToDispose.Add(reloadedConfig);
    Assert(ReadVirtualKey(reloadedConfig) == 0x75,
        "A saved F6 hotkey must survive a reload.");

    foreach (object configurable in configurationsToDispose.ToArray())
        disposeEvents.Invoke(configurable, null);
    configurationsToDispose.Clear();

    File.WriteAllText(configPath, "{\"MenuHotkey\":999}");
    IConfiguratorV3 invalidConfigurator = CreateConfigurator();
    IUpdatableConfigurable invalidConfig =
        (IUpdatableConfigurable)invalidConfigurator.GetConfigurations()[0];
    configurationsToDispose.Add(invalidConfig);
    Assert(ReadVirtualKey(invalidConfig) == 0x77,
        "An unsupported numeric hotkey must normalize to F8.");

    int[] virtualKeys = Enum.GetValues(hotkeyType)
        .Cast<object>()
        .Select(Convert.ToInt32)
        .ToArray();
    Assert(virtualKeys.Length == virtualKeys.Distinct().Count(),
        "Reloaded-II hotkey choices must have unique virtual-key values.");
    Assert(virtualKeys.All(key => key is >= 1 and <= 255),
        "Every Reloaded-II hotkey choice must be a valid Win32 virtual key.");

    Console.WriteLine("HOTKEY_CONFIG=PASS");
}
finally
{
    foreach (object configurable in configurationsToDispose)
    {
        try
        {
            disposeEvents.Invoke(configurable, null);
        }
        catch
        {
            // Continue cleaning the isolated test directory.
        }
    }

    Directory.Delete(temporaryDirectory, recursive: true);
}

IConfiguratorV3 CreateConfigurator() =>
    (IConfiguratorV3)(Activator.CreateInstance(configuratorType, temporaryDirectory)
        ?? throw new InvalidOperationException("Could not create the Reloaded-II configurator."));

int ReadVirtualKey(object configuration) =>
    Convert.ToInt32(hotkeyProperty.GetValue(configuration));

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
