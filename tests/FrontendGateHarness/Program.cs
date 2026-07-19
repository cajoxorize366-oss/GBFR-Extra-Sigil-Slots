using System.Reflection;
using System.Runtime.Loader;

if (args.Length != 1)
    throw new ArgumentException("Pass the managed build output directory.");

string outputDirectory = Path.GetFullPath(args[0]);
string assemblyPath = Path.Combine(outputDirectory, "GBFR.ExtraSigilSlots.Reloaded.dll");
PluginLoadContext context = new(assemblyPath);
Assembly assembly = context.LoadFromAssemblyPath(assemblyPath);
Type gate = assembly.GetType(
    "GBFR.ExtraSigilSlots.Reloaded.FrontendOverlayGate",
    throwOnError: true)!;

MethodInfo forceClosed = GetMethod("ForceClosed");
MethodInfo setOpen = GetMethod("SetOpen");
MethodInfo setToggleKey = GetMethod("SetToggleKey");
MethodInfo observe = GetMethod("ObserveWindowMessage");
MethodInfo consume = GetMethod("ConsumeToggleRequest");
PropertyInfo shouldRender = GetProperty("ShouldRenderFrame");
PropertyInfo isOpen = GetProperty("IsOpen");
PropertyInfo currentKey = GetProperty("CurrentToggleKey");

forceClosed.Invoke(null, null);
setToggleKey.Invoke(null, [0x77]);
Assert(!ReadBool(shouldRender), "A closed frontend must not render.");
Assert(!ReadBool(isOpen), "The frontend must start closed.");

bool queued = (bool)(observe.Invoke(null, [0x0100u, new IntPtr(0x77), IntPtr.Zero]) ?? false);
Assert(queued, "The first F8 keydown must queue a toggle.");
Assert(ReadBool(shouldRender), "A pending toggle must wake one frontend frame.");

bool repeated = (bool)(observe.Invoke(
    null,
    [0x0100u, new IntPtr(0x77), new IntPtr(1L << 30)]) ?? false);
Assert(!repeated, "An autorepeated keydown must not queue another toggle.");
Assert((bool)(consume.Invoke(null, null) ?? false), "The queued toggle must be consumed.");
setOpen.Invoke(null, [true]);
Assert(ReadBool(isOpen) && ReadBool(shouldRender), "An open frontend must render.");

observe.Invoke(null, [0x0100u, new IntPtr(0x77), IntPtr.Zero]);
Assert((bool)(consume.Invoke(null, null) ?? false), "The close toggle must be consumed.");
setOpen.Invoke(null, [false]);
Assert(!ReadBool(shouldRender), "Closing must put the frontend back to sleep.");

setToggleKey.Invoke(null, [0]);
Assert((int)(currentKey.GetValue(null) ?? 0) == 0x77, "Invalid keys must fall back to F8.");
Assert(!(bool)(observe.Invoke(null, [0x0101u, new IntPtr(0x77), IntPtr.Zero]) ?? true),
    "Key-up must not queue a toggle.");

forceClosed.Invoke(null, null);
setToggleKey.Invoke(null, [0x75]);
Assert((int)(currentKey.GetValue(null) ?? 0) == 0x75,
    "A Reloaded-II hotkey change must update the frontend gate.");
Assert(!(bool)(observe.Invoke(null, [0x0100u, new IntPtr(0x77), IntPtr.Zero]) ?? true),
    "F8 must stop toggling after the hotkey changes to F6.");
Assert((bool)(observe.Invoke(null, [0x0100u, new IntPtr(0x75), IntPtr.Zero]) ?? false),
    "The configured F6 key must queue a toggle.");
Assert((bool)(consume.Invoke(null, null) ?? false),
    "The configured-key toggle must be consumed.");
forceClosed.Invoke(null, null);
setToggleKey.Invoke(null, [0x77]);

observe.Invoke(null, [0x0100u, new IntPtr(0x77), IntPtr.Zero]);
observe.Invoke(null, [0x0100u, new IntPtr(0x77), IntPtr.Zero]);
Assert(!(bool)(consume.Invoke(null, null) ?? true),
    "Two physical toggles before a frame must cancel by parity.");
Assert(!ReadBool(shouldRender), "An even toggle count must return the frontend to sleep.");

Console.WriteLine("FRONTEND_EVENT_GATE=PASS");

MethodInfo GetMethod(string name) => gate.GetMethod(
    name,
    BindingFlags.NonPublic | BindingFlags.Static) ??
    throw new MissingMethodException(gate.FullName, name);

PropertyInfo GetProperty(string name) => gate.GetProperty(
    name,
    BindingFlags.NonPublic | BindingFlags.Static) ??
    throw new MissingMemberException(gate.FullName, name);

static bool ReadBool(PropertyInfo property) => (bool)(property.GetValue(null) ?? false);

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

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
            assemblyName.Name + ".dll");
        return File.Exists(harnessDependency) ? LoadFromAssemblyPath(harnessDependency) : null;
    }
}
