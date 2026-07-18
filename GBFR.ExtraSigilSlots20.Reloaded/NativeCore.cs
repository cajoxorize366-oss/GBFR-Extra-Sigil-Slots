using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace GBFR.ExtraSigilSlots20.Reloaded;

internal static unsafe class NativeCore
{
    internal const int AbiVersion = 6;
    internal const int VirtualSlotCount = 8;
    internal const int OwnerCharacterCapacity = 4;

    private const string LibraryName = "GBFR.ExtraSigilSlots20.Native.dll";
    private static readonly object ResolverLock = new();
    private static string? _libraryPath;
    private static IntPtr _libraryHandle;
    private static int _resolverConfigured;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct GemData
    {
        internal uint Trait1;
        internal int Trait1Level;
        internal uint Trait2;
        internal int Trait2Level;
        internal uint GemId;
        internal uint WornBy;
        internal int SigilLevel;
        internal uint SlotId;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct InventoryItem
    {
        internal GemData Gem;
        internal uint Equipped;
        internal uint RequiredCharacterHash;
        internal uint VirtualOwnerCharacterHash;
        internal int VirtualOwnerSlot;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct RuntimeState
    {
        internal uint AbiVersion;
        internal uint StructSize;
        internal int Initialized;
        internal int HooksReady;
        internal int ShuttingDown;
        internal int RuntimeMessageIsError;
        internal uint UiSelectedCharacterHash;
        internal uint EffectiveCharacterHash;
        internal uint LastRebuiltCharacterHash;
        internal int LastContextMode;
        internal uint OwnerThreadId;
        internal uint OverlayThreadId;
        internal ulong OwnerTickCount;
        internal ulong OverlayFrameCount;
        internal uint OwnerCharacterCount;
        internal fixed uint OwnerCharacterHashes[OwnerCharacterCapacity];
        internal ulong LastApplyGeneration;
        internal uint LastApplyCharacterHash;
        internal uint LastApplyExpectedCount;
        internal uint LastApplyInjectedCount;
        internal int LastApplyResult;
        internal int AutoApply;
        internal int ShowEquipped;
        internal int ToggleKey;
        internal int Language;
        internal uint AuthorizedStatusCount;
        internal uint AuthorizedCharacterHash;
        internal ulong AuthorizedStatusAddress;
        internal ulong InventoryRevision;
        internal int InventoryDirty;
        internal int EditAllowed;
        internal int UiMode;
        internal int SourceMode;
        internal int EditSessionState;
        internal uint ObservedCharacterHash;
        internal ulong ObservedStatusAddress;
        internal int ObservedStatusContext;
        internal uint LifecycleRebindAttempts;
        internal int InputCaptureRequested;
        internal int InputCaptureEffective;
        internal int InputIatHooksReady;
        internal int DirectInputHookReady;
        internal ulong NaturalBindAttempts;
        internal ulong NaturalBindSuccesses;
        internal ulong NaturalBindStatusAddress;
        internal uint NaturalBindCharacterHash;
        internal int NaturalBindContext;
        internal uint NaturalBindExpectedCount;
        internal uint NaturalBindInjectedCount;
        internal int NaturalBindResult;
        internal ulong OwnerManagerAddress;
        internal uint NaturalBindOwnerKey;
        internal ulong NaturalBindOwnerStatusAddress;
    }

    internal sealed record InventoryView(
        GemData Gem,
        bool Equipped,
        uint RequiredCharacterHash,
        uint VirtualOwnerCharacterHash,
        int VirtualOwnerSlot,
        string Label)
    {
        internal string Searchable { get; } = Label.ToLowerInvariant();
    }

    internal static void Configure(string modDirectory)
    {
        string path = Path.GetFullPath(Path.Combine(modDirectory, LibraryName));
        lock (ResolverLock)
        {
            if (_libraryPath is not null &&
                !string.Equals(_libraryPath, path, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Native core was already bound to a different path: {_libraryPath}"
                );
            }
            _libraryPath = path;
            if (Interlocked.Exchange(ref _resolverConfigured, 1) == 0)
            {
                NativeLibrary.SetDllImportResolver(
                    typeof(NativeCore).Assembly,
                    ResolveLibrary
                );
            }
        }
    }

    internal static bool Initialize()
    {
        uint abiVersion = NativeGetAbiVersion();
        if (abiVersion != AbiVersion)
        {
            throw new InvalidOperationException(
                $"Native ABI mismatch: managed {AbiVersion}, native {abiVersion}."
            );
        }
        return NativeInitialize() != 0;
    }

    internal static void Tick() => NativeTick();

    internal static void Shutdown() => NativeShutdown();

    internal static bool TryGetState(out RuntimeState state)
    {
        return NativeGetState(out state, (uint)sizeof(RuntimeState)) != 0 &&
            state.AbiVersion == AbiVersion &&
            state.StructSize == sizeof(RuntimeState);
    }

    internal static string GetRuntimeMessage()
    {
        uint required = NativeCopyRuntimeMessage(null, 0);
        if (required <= 1)
            return string.Empty;
        if (required > 64 * 1024)
            required = 64 * 1024;
        byte[] bytes = new byte[required];
        fixed (byte* buffer = bytes)
            NativeCopyRuntimeMessage((sbyte*)buffer, required);
        int length = Array.IndexOf(bytes, (byte)0);
        if (length < 0)
            length = bytes.Length;
        return Encoding.UTF8.GetString(bytes, 0, length);
    }

    internal static bool RefreshInventory() => NativeRefreshInventory() != 0;

    internal static uint GetInventoryCount() => NativeGetInventoryCount();

    internal static bool TryCopyInventoryItem(
        uint index,
        byte[] labelBuffer,
        out InventoryView? view)
    {
        InventoryItem item;
        fixed (byte* buffer = labelBuffer)
        {
            if (NativeCopyInventoryItem(
                    index,
                    out item,
                    (uint)sizeof(InventoryItem),
                    (sbyte*)buffer,
                    (uint)labelBuffer.Length) == 0)
            {
                view = null;
                return false;
            }
        }
        int length = Array.IndexOf(labelBuffer, (byte)0);
        if (length < 0)
            length = labelBuffer.Length;
        string label = Encoding.UTF8.GetString(labelBuffer, 0, length);
        view = new InventoryView(
            item.Gem,
            item.Equipped != 0,
            item.RequiredCharacterHash,
            item.VirtualOwnerCharacterHash,
            item.VirtualOwnerSlot,
            label
        );
        return true;
    }

    internal static uint[] GetSelection(uint characterHash)
    {
        uint[] slots = new uint[VirtualSlotCount];
        fixed (uint* slotPointer = slots)
        {
            if (NativeGetSelection(characterHash, slotPointer, VirtualSlotCount) == 0)
                Array.Clear(slots);
        }
        return slots;
    }

    internal static bool SetSelection(uint characterHash, int virtualSlot, uint inventorySlotId)
    {
        return NativeSetSelection(characterHash, virtualSlot, inventorySlotId) != 0;
    }

    internal static uint RequestApply(uint characterHash) => NativeRequestApply(characterHash);

    internal static bool SetAutoApply(bool enabled) => NativeSetAutoApply(enabled ? 1 : 0) != 0;

    internal static bool SetShowEquipped(bool enabled) =>
        NativeSetShowEquipped(enabled ? 1 : 0) != 0;

    internal static bool SetToggleKey(int virtualKey) => NativeSetToggleKey(virtualKey) != 0;

    internal static bool SetLanguage(int language) => NativeSetLanguage(language) != 0;

    internal static bool SetInputCapture(bool requested) =>
        NativeSetInputCapture(requested ? 1 : 0) != 0;

    internal static void ForceReleaseInput() => NativeSetInputCapture(-1);

    internal static bool IsInputCaptureActive() => NativeGetInputCaptureActive() != 0;

    internal static bool IsInventoryDirty() => NativeIsInventoryDirty() != 0;

    internal static bool CanEditCharacter(uint characterHash) =>
        NativeCanEditCharacter(characterHash) != 0;

    private static IntPtr ResolveLibrary(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;
        lock (ResolverLock)
        {
            if (_libraryHandle != IntPtr.Zero)
                return _libraryHandle;
            if (_libraryPath is null || !File.Exists(_libraryPath))
                throw new DllNotFoundException($"Native core not found: {_libraryPath}");
            _libraryHandle = NativeLibrary.Load(_libraryPath);
            return _libraryHandle;
        }
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GBFR20_GetAbiVersion();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int GBFR20_Initialize();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern void GBFR20_Tick();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern void GBFR20_Shutdown();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int GBFR20_GetState(out RuntimeState state, uint stateSize);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GBFR20_CopyRuntimeMessage(sbyte* buffer, uint bufferSize);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int GBFR20_RefreshInventory();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GBFR20_GetInventoryCount();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int GBFR20_CopyInventoryItem(
        uint index,
        out InventoryItem item,
        uint itemSize,
        sbyte* labelBuffer,
        uint labelBufferSize);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int GBFR20_GetSelection(
        uint characterHash,
        uint* slots,
        uint slotCount);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int GBFR20_SetSelection(
        uint characterHash,
        int virtualSlot,
        uint inventorySlotId);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint GBFR20_RequestApply(uint characterHash);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int GBFR20_SetAutoApply(int enabled);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int GBFR20_SetShowEquipped(int enabled);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int GBFR20_SetToggleKey(int virtualKey);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int GBFR20_SetLanguage(int language);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int GBFR20_SetInputCapture(int requested);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int GBFR20_GetInputCaptureActive();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int GBFR20_IsInventoryDirty();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int GBFR20_CanEditCharacter(uint characterHash);

    private static uint NativeGetAbiVersion() => GBFR20_GetAbiVersion();
    private static int NativeInitialize() => GBFR20_Initialize();
    private static void NativeTick() => GBFR20_Tick();
    private static void NativeShutdown() => GBFR20_Shutdown();
    private static int NativeGetState(out RuntimeState state, uint size) =>
        GBFR20_GetState(out state, size);
    private static uint NativeCopyRuntimeMessage(sbyte* buffer, uint size) =>
        GBFR20_CopyRuntimeMessage(buffer, size);
    private static int NativeRefreshInventory() => GBFR20_RefreshInventory();
    private static uint NativeGetInventoryCount() => GBFR20_GetInventoryCount();
    private static int NativeCopyInventoryItem(
        uint index,
        out InventoryItem item,
        uint itemSize,
        sbyte* labelBuffer,
        uint labelBufferSize) =>
        GBFR20_CopyInventoryItem(index, out item, itemSize, labelBuffer, labelBufferSize);
    private static int NativeGetSelection(uint hash, uint* slots, uint count) =>
        GBFR20_GetSelection(hash, slots, count);
    private static int NativeSetSelection(uint hash, int slot, uint id) =>
        GBFR20_SetSelection(hash, slot, id);
    private static uint NativeRequestApply(uint hash) => GBFR20_RequestApply(hash);
    private static int NativeSetAutoApply(int enabled) => GBFR20_SetAutoApply(enabled);
    private static int NativeSetShowEquipped(int enabled) => GBFR20_SetShowEquipped(enabled);
    private static int NativeSetToggleKey(int key) => GBFR20_SetToggleKey(key);
    private static int NativeSetLanguage(int language) => GBFR20_SetLanguage(language);
    private static int NativeSetInputCapture(int requested) => GBFR20_SetInputCapture(requested);
    private static int NativeGetInputCaptureActive() => GBFR20_GetInputCaptureActive();
    private static int NativeIsInventoryDirty() => GBFR20_IsInventoryDirty();
    private static int NativeCanEditCharacter(uint hash) => GBFR20_CanEditCharacter(hash);
}
