using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

if (args.Length != 1)
    throw new ArgumentException("Pass the managed build output directory.");

string modDirectory = Path.GetFullPath(args[0]);
string managedPath = Path.Combine(modDirectory, "GBFR.ExtraSigilSlots20.Reloaded.dll");
Assembly assembly = Assembly.LoadFrom(managedPath);
BindingFlags staticFlags = BindingFlags.Static | BindingFlags.NonPublic;
BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.NonPublic;

Type nativeCore = assembly.GetType(
    "GBFR.ExtraSigilSlots20.Reloaded.NativeCore",
    throwOnError: true)!;
nativeCore.GetMethod("Configure", staticFlags)!.Invoke(null, [modDirectory]);

Type nativeSelectionType = nativeCore.GetNestedType(
    "PresetCharacterSelection",
    BindingFlags.NonPublic)!;
Type nativeResultType = nativeCore.GetNestedType(
    "PresetSlotResult",
    BindingFlags.NonPublic)!;
if (Marshal.SizeOf(nativeSelectionType) != 100 || Marshal.SizeOf(nativeResultType) != 20)
    throw new InvalidOperationException("Managed preset ABI struct sizes are incorrect.");

IntPtr nativeLibrary = NativeLibrary.Load(
    Path.Combine(modDirectory, "GBFR.ExtraSigilSlots20.Native.dll"));
try
{
    IntPtr abiExport = NativeLibrary.GetExport(nativeLibrary, "GBFR20_GetAbiVersion");
    IntPtr applyExport = NativeLibrary.GetExport(nativeLibrary, "GBFR20_ApplyPreset");
    GetAbiVersion getAbiVersion = Marshal.GetDelegateForFunctionPointer<GetAbiVersion>(abiExport);
    if (getAbiVersion() != 8 || applyExport == IntPtr.Zero)
        throw new InvalidOperationException("Native ABI 8 preset exports are unavailable.");
}
finally
{
    NativeLibrary.Free(nativeLibrary);
}

Type storeType = assembly.GetType(
    "GBFR.ExtraSigilSlots20.Reloaded.SigilPresetStore",
    throwOnError: true)!;
string testDirectory = Path.Combine(
    Path.GetTempPath(),
    "GBFR20-preset-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testDirectory);
string jsonPath = Path.Combine(testDirectory, "GBFR-ExtraSigilSlots20.presets.json");

try
{
    File.WriteAllText(
        jsonPath,
        """
        {
          "Version": 1,
          "Presets": [
            {
              "Id": "existing",
              "Name": "方案甲",
              "Characters": [
                {
                  "CharacterHash": 707178930,
                  "Slots": [123, 0, 0, 0, 0, 0, 0, 0]
                }
              ]
            },
            {
              "Id": "second",
              "Name": "方案乙",
              "Characters": [
                {
                  "CharacterHash": 417542649,
                  "Slots": [0, 123, 0, 0, 0, 0, 0, 0]
                }
              ]
            }
          ]
        }
        """,
        new UTF8Encoding(false));

    object store = Activator.CreateInstance(
        storeType,
        instanceFlags,
        binder: null,
        args: [testDirectory, new Action<string>(_ => { })],
        culture: null)!;

    MethodInfo referencesMethod = storeType.GetMethod("GetPresetNamesForSlot", instanceFlags)!;
    MethodInfo transferMethod = storeType.GetMethod("ClearSlotReferencesAndRun", instanceFlags)!;
    MethodInfo createMethod = storeType.GetMethod("Create", instanceFlags)!;

    int InitialReferenceCount() =>
        ((System.Collections.ICollection)referencesMethod.Invoke(store, [123u])!).Count;

    if (InitialReferenceCount() != 2)
        throw new InvalidOperationException("Expected two preset references before transfer.");

    object?[] rollbackArgs = [123u, new Func<bool>(() => false), null];
    bool rollbackResult = (bool)transferMethod.Invoke(store, rollbackArgs)!;
    if (rollbackResult || InitialReferenceCount() != 2 || !JsonContainsSlot(jsonPath, 123))
        throw new InvalidOperationException("Failed transfer did not restore preset references.");

    object?[] commitArgs = [123u, new Func<bool>(() => true), null];
    bool commitResult = (bool)transferMethod.Invoke(store, commitArgs)!;
    if (!commitResult || InitialReferenceCount() != 0 || JsonContainsSlot(jsonPath, 123))
        throw new InvalidOperationException("Successful transfer did not clear every reference.");

    object created = createMethod.Invoke(store, ["中文自定义预设"])!;
    Type presetType = created.GetType();
    string createdName = (string)presetType.GetProperty("Name")!.GetValue(created)!;
    int characterCount = ((System.Collections.ICollection)presetType
        .GetProperty("Characters")!
        .GetValue(created)!).Count;
    if (createdName != "中文自定义预设" || characterCount != 28)
        throw new InvalidOperationException("Named full-character preset capture failed.");

    Console.WriteLine("PRESET_STORE_TEST=PASS");
    Console.WriteLine("ROLLBACK_REFERENCES=2");
    Console.WriteLine("COMMITTED_REFERENCES=0");
    Console.WriteLine("CAPTURED_CHARACTERS=28");
    Console.WriteLine("ABI_VERSION=8");
    Console.WriteLine("PRESET_SELECTION_SIZE=100");
    Console.WriteLine("PRESET_RESULT_SIZE=20");
}
finally
{
    Directory.Delete(testDirectory, recursive: true);
}

static bool JsonContainsSlot(string path, uint slotId)
{
    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
    foreach (JsonElement preset in document.RootElement.GetProperty("Presets").EnumerateArray())
    {
        foreach (JsonElement character in preset.GetProperty("Characters").EnumerateArray())
        {
            foreach (JsonElement slot in character.GetProperty("Slots").EnumerateArray())
            {
                if (slot.GetUInt32() == slotId)
                    return true;
            }
        }
    }
    return false;
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate uint GetAbiVersion();
