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

    Console.WriteLine("PRESENT_BRIDGE_TEST=PASS");
    Console.WriteLine("NORMAL_HRESULT=0x12345678");
    Console.WriteLine("CONTAINED_SEH=0xC0000005");
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

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int InvokeOriginalPresent(
    ulong originalFunctionAddress,
    IntPtr swapChain,
    uint syncInterval,
    uint presentFlags,
    out uint exceptionCode);

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
