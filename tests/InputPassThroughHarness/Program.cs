using System.Reflection;
using System.Runtime.Loader;

if (args.Length != 1)
    throw new ArgumentException("Pass the managed build output directory.");

string outputDirectory = Path.GetFullPath(args[0]);
string assemblyPath = Path.Combine(outputDirectory, "GBFR.ExtraSigilSlots20.Reloaded.dll");
PluginLoadContext context = new(assemblyPath);
Assembly assembly = context.LoadFromAssemblyPath(assemblyPath);
Type classifierType = assembly.GetType(
    "GBFR.ExtraSigilSlots20.Reloaded.RawInputClassifier",
    throwOnError: true)!;
MethodInfo classifier = classifierType.GetMethod(
    "IsKeyboardOrMouse",
    BindingFlags.NonPublic | BindingFlags.Static
) ?? throw new MissingMethodException(classifierType.FullName, "IsKeyboardOrMouse");

(uint Type, bool Capture, string Name)[] cases =
[
    (0, true, "mouse"),
    (1, true, "keyboard"),
    (2, false, "HID/controller"),
    (3, false, "unknown/future"),
];
foreach ((uint type, bool expected, string name) in cases)
{
    bool actual = (bool)(classifier.Invoke(null, [type]) ?? false);
    if (actual != expected)
        throw new InvalidOperationException(
            $"Raw input type {type} ({name}): expected capture={expected}, got {actual}."
        );
    Console.WriteLine($"RAW_TYPE={type} NAME={name} CAPTURE={actual}");
}

Console.WriteLine("RAW_INPUT_CLASSIFICATION=PASS");

sealed class PluginLoadContext(string pluginPath) : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver = new(pluginPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (path is not null)
            return LoadFromAssemblyPath(path);
        string harnessDependency = Path.Combine(
            AppContext.BaseDirectory,
            assemblyName.Name + ".dll"
        );
        return File.Exists(harnessDependency) ? LoadFromAssemblyPath(harnessDependency) : null;
    }
}
