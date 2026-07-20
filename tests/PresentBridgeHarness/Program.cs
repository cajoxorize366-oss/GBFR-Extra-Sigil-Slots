using System.ComponentModel;
using System.Runtime.InteropServices;

if (args.Length != 1)
    throw new ArgumentException("Pass the native build output directory.");
if (!Environment.Is64BitProcess)
    throw new PlatformNotSupportedException("The Present bridge smoke test requires x64.");

string nativePath = Path.Combine(
    Path.GetFullPath(args[0]),
    "GBFR.ExtraSigilSlots.Native.dll");
IntPtr nativeLibrary = NativeLibrary.Load(nativePath);
IntPtr executableMemory = IntPtr.Zero;
try
{
    IntPtr bridgeExport = NativeLibrary.GetExport(
        nativeLibrary,
        "GBFR20_InvokeOriginalPresent");
    InvokeOriginalPresent bridge =
        Marshal.GetDelegateForFunctionPointer<InvokeOriginalPresent>(bridgeExport);
    IntPtr resolverExport = NativeLibrary.GetExport(
        nativeLibrary,
        "GBFR20_ResolveHookChainTarget");
    ResolveHookChainTarget resolveHookChainTarget =
        Marshal.GetDelegateForFunctionPointer<ResolveHookChainTarget>(resolverExport);

    int nullResult = bridge(0, new IntPtr(1), 0, 0, out uint nullException);
    if (nullResult != unchecked((int)0x80004003) || nullException != 0)
        throw new InvalidOperationException("The Present bridge null-address guard failed.");

    executableMemory = NativeMethods.VirtualAlloc(
        IntPtr.Zero,
        new UIntPtr(4096),
        0x3000,
        0x40);
    if (executableMemory == IntPtr.Zero)
        throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualAlloc failed.");

    Marshal.Copy(
        new byte[] { 0xB8, 0x78, 0x56, 0x34, 0x12, 0xC3 },
        0,
        executableMemory,
        6);
    int successResult = bridge(
        unchecked((ulong)executableMemory.ToInt64()),
        new IntPtr(1),
        0,
        0,
        out uint successException);
    if (successResult != 0x12345678 || successException != 0)
        throw new InvalidOperationException("The Present bridge changed a normal HRESULT.");

    Marshal.Copy(
        new byte[] { 0x31, 0xC0, 0x8B, 0x00, 0xC3 },
        0,
        executableMemory,
        5);
    int failureResult = bridge(
        unchecked((ulong)executableMemory.ToInt64()),
        new IntPtr(1),
        0,
        0,
        out uint failureException);
    if (failureResult != unchecked((int)0x80004005) ||
        failureException != 0xC0000005)
    {
        throw new InvalidOperationException(
            $"The Present bridge did not contain the access violation: " +
            $"HRESULT=0x{failureResult:X8}, SEH=0x{failureException:X8}.");
    }

    IntPtr chainEntry = executableMemory + 0x100;
    IntPtr chainMiddle = executableMemory + 0x200;
    IntPtr chainTail = executableMemory + 0x300;
    Marshal.Copy(
        new byte[] { 0xB8, 0x78, 0x56, 0x34, 0x12, 0xC3 },
        0,
        chainTail,
        6);
    WriteAbsoluteIndirectJump(chainEntry, chainMiddle);
    WriteMovRaxJump(chainMiddle, chainTail);

    ulong resolvedTarget = resolveHookChainTarget(
        unchecked((ulong)chainEntry.ToInt64()),
        8,
        out uint resolvedJumpCount,
        out uint resolveStatus);
    if (resolvedTarget != unchecked((ulong)chainTail.ToInt64()) ||
        resolvedJumpCount != 2 || resolveStatus != 0)
    {
        throw new InvalidOperationException(
            $"The hook-chain resolver missed a two-layer x64 JMP chain: " +
            $"target=0x{resolvedTarget:X}, jumps={resolvedJumpCount}, status={resolveStatus}.");
    }
    int resolvedResult = bridge(
        resolvedTarget,
        new IntPtr(1),
        0,
        0,
        out uint resolvedException);
    if (resolvedResult != 0x12345678 || resolvedException != 0)
        throw new InvalidOperationException("The resolved hook-chain tail was not callable.");

    IntPtr relativeEntry = executableMemory + 0x500;
    WriteRelativeJump(relativeEntry, chainTail);
    AssertResolvedTarget(
        resolveHookChainTarget,
        relativeEntry,
        chainTail,
        expectedJumpCount: 1,
        "E9 relative JMP");

    IntPtr shortEntry = executableMemory + 0x600;
    IntPtr shortTail = executableMemory + 0x610;
    Marshal.Copy(
        new byte[] { 0xB8, 0x78, 0x56, 0x34, 0x12, 0xC3 },
        0,
        shortTail,
        6);
    WriteShortJump(shortEntry, shortTail);
    AssertResolvedTarget(
        resolveHookChainTarget,
        shortEntry,
        shortTail,
        expectedJumpCount: 1,
        "EB short JMP");

    IntPtr rexIndirectEntry = executableMemory + 0x700;
    WriteRexAbsoluteIndirectJump(rexIndirectEntry, chainTail);
    AssertResolvedTarget(
        resolveHookChainTarget,
        rexIndirectEntry,
        chainTail,
        expectedJumpCount: 1,
        "REX FF25 indirect JMP");

    IntPtr movR11Entry = executableMemory + 0x800;
    WriteMovR11Jump(movR11Entry, chainTail);
    AssertResolvedTarget(
        resolveHookChainTarget,
        movR11Entry,
        chainTail,
        expectedJumpCount: 1,
        "mov r11/JMP r11");

    ulong depthFailure = resolveHookChainTarget(
        unchecked((ulong)chainEntry.ToInt64()),
        1,
        out uint depthJumpCount,
        out uint depthStatus);
    if (depthFailure != 0 || depthJumpCount != 1 || depthStatus != 5)
        throw new InvalidOperationException("The hook-chain depth limit was not enforced.");

    IntPtr cycleEntry = executableMemory + 0x400;
    IntPtr cycleTarget = executableMemory + 0x420;
    WriteRelativeJump(cycleEntry, cycleTarget);
    WriteRelativeJump(cycleTarget, cycleEntry);
    ulong cycleResult = resolveHookChainTarget(
        unchecked((ulong)cycleEntry.ToInt64()),
        8,
        out uint cycleJumpCount,
        out uint cycleStatus);
    if (cycleResult != 0 || cycleJumpCount != 2 || cycleStatus != 4)
        throw new InvalidOperationException("The hook-chain cycle guard failed.");

    IntPtr unsupportedEntry = executableMemory + 0x900;
    Marshal.Copy(new byte[] { 0x48, 0xFF, 0xE0 }, 0, unsupportedEntry, 3);
    ulong unsupportedResult = resolveHookChainTarget(
        unchecked((ulong)unsupportedEntry.ToInt64()),
        8,
        out uint unsupportedJumpCount,
        out uint unsupportedStatus);
    if (unsupportedResult != 0 || unsupportedJumpCount != 0 || unsupportedStatus != 6)
        throw new InvalidOperationException("A dynamic register JMP was not rejected.");

    IntPtr nonExecutableEntry = executableMemory + 0xA00;
    WriteMovRaxJump(nonExecutableEntry, new IntPtr(1));
    ulong nonExecutableResult = resolveHookChainTarget(
        unchecked((ulong)nonExecutableEntry.ToInt64()),
        8,
        out uint nonExecutableJumpCount,
        out uint nonExecutableStatus);
    if (nonExecutableResult != 0 ||
        nonExecutableJumpCount != 1 || nonExecutableStatus != 3)
    {
        throw new InvalidOperationException("A non-executable JMP target was not rejected.");
    }

    IntPtr unreadableEntry = executableMemory + 4095;
    Marshal.WriteByte(unreadableEntry, 0xE9);
    ulong unreadableResult = resolveHookChainTarget(
        unchecked((ulong)unreadableEntry.ToInt64()),
        8,
        out uint unreadableJumpCount,
        out uint unreadableStatus);
    if (unreadableResult != 0 || unreadableJumpCount != 0 || unreadableStatus != 2)
        throw new InvalidOperationException("A truncated JMP at a page boundary was not rejected.");

    ulong invalidResult = resolveHookChainTarget(
        0,
        8,
        out uint invalidJumpCount,
        out uint invalidStatus);
    if (invalidResult != 0 || invalidJumpCount != 0 || invalidStatus != 1)
        throw new InvalidOperationException("The resolver invalid-address guard failed.");

    Console.WriteLine("PRESENT_BRIDGE_TEST=PASS");
    Console.WriteLine("NORMAL_HRESULT=0x12345678");
    Console.WriteLine("CONTAINED_SEH=0xC0000005");
    Console.WriteLine("HOOK_CHAIN_RESOLVER_TEST=PASS");
    Console.WriteLine("HOOK_CHAIN_LAYERS=2");
    Console.WriteLine("HOOK_CHAIN_CYCLE=REJECTED");
    Console.WriteLine("HOOK_CHAIN_DEPTH_LIMIT=ENFORCED");
    Console.WriteLine("HOOK_CHAIN_UNSAFE_TARGETS=REJECTED");
}
finally
{
    if (executableMemory != IntPtr.Zero &&
        !NativeMethods.VirtualFree(executableMemory, UIntPtr.Zero, 0x8000))
    {
        Console.Error.WriteLine(
            $"VirtualFree failed with Win32 error {Marshal.GetLastWin32Error()}.");
    }
    NativeLibrary.Free(nativeLibrary);
}

static void WriteAbsoluteIndirectJump(IntPtr source, IntPtr target)
{
    byte[] instruction = new byte[14];
    instruction[0] = 0xFF;
    instruction[1] = 0x25;
    BitConverter.GetBytes(target.ToInt64()).CopyTo(instruction, 6);
    Marshal.Copy(instruction, 0, source, instruction.Length);
}

static void WriteMovRaxJump(IntPtr source, IntPtr target)
{
    byte[] instruction = new byte[12];
    instruction[0] = 0x48;
    instruction[1] = 0xB8;
    BitConverter.GetBytes(target.ToInt64()).CopyTo(instruction, 2);
    instruction[10] = 0xFF;
    instruction[11] = 0xE0;
    Marshal.Copy(instruction, 0, source, instruction.Length);
}

static void WriteRexAbsoluteIndirectJump(IntPtr source, IntPtr target)
{
    byte[] instruction = new byte[15];
    instruction[0] = 0x48;
    instruction[1] = 0xFF;
    instruction[2] = 0x25;
    BitConverter.GetBytes(target.ToInt64()).CopyTo(instruction, 7);
    Marshal.Copy(instruction, 0, source, instruction.Length);
}

static void WriteMovR11Jump(IntPtr source, IntPtr target)
{
    byte[] instruction = new byte[13];
    instruction[0] = 0x49;
    instruction[1] = 0xBB;
    BitConverter.GetBytes(target.ToInt64()).CopyTo(instruction, 2);
    instruction[10] = 0x41;
    instruction[11] = 0xFF;
    instruction[12] = 0xE3;
    Marshal.Copy(instruction, 0, source, instruction.Length);
}

static void WriteShortJump(IntPtr source, IntPtr target)
{
    long displacement = target.ToInt64() - (source.ToInt64() + 2);
    if (displacement is < sbyte.MinValue or > sbyte.MaxValue)
        throw new InvalidOperationException("Synthetic short JMP is out of range.");
    Marshal.Copy(
        new byte[] { 0xEB, unchecked((byte)(sbyte)displacement) },
        0,
        source,
        2);
}

static void WriteRelativeJump(IntPtr source, IntPtr target)
{
    long displacement = target.ToInt64() - (source.ToInt64() + 5);
    if (displacement is < int.MinValue or > int.MaxValue)
        throw new InvalidOperationException("Synthetic relative JMP is out of range.");
    byte[] instruction = new byte[5];
    instruction[0] = 0xE9;
    BitConverter.GetBytes((int)displacement).CopyTo(instruction, 1);
    Marshal.Copy(instruction, 0, source, instruction.Length);
}

static void AssertResolvedTarget(
    ResolveHookChainTarget resolver,
    IntPtr entry,
    IntPtr expectedTarget,
    uint expectedJumpCount,
    string scenario)
{
    ulong result = resolver(
        unchecked((ulong)entry.ToInt64()),
        8,
        out uint jumpCount,
        out uint status);
    if (result != unchecked((ulong)expectedTarget.ToInt64()) ||
        jumpCount != expectedJumpCount || status != 0)
    {
        throw new InvalidOperationException(
            $"The resolver failed for {scenario}: target=0x{result:X}, " +
            $"jumps={jumpCount}, status={status}.");
    }
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int InvokeOriginalPresent(
    ulong originalFunctionAddress,
    IntPtr swapChain,
    uint syncInterval,
    uint presentFlags,
    out uint exceptionCode);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate ulong ResolveHookChainTarget(
    ulong functionAddress,
    uint maxJumpCount,
    out uint jumpCount,
    out uint status);

internal static partial class NativeMethods
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr VirtualAlloc(
        IntPtr address,
        UIntPtr size,
        uint allocationType,
        uint protect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool VirtualFree(
        IntPtr address,
        UIntPtr size,
        uint freeType);
}
