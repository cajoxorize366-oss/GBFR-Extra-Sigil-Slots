using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

const uint BootstrapMagic = 0x42524647;
const ushort ProtocolVersion = 1;
const int BootstrapSize = 2064;
const int FrameHeaderSize = 20;
const int HelloResponseSize = 16;
const int StateResponseSize = 284;
const int DataDirectoryOffset = 16;
const int DataDirectoryCapacity = 1024;

if (args.Length != 1)
    throw new ArgumentException("Pass the native build output directory.");
if (!Environment.Is64BitProcess)
    throw new PlatformNotSupportedException("The standalone agent smoke test requires x64.");

string nativePath = Path.Combine(
    Path.GetFullPath(args[0]),
    "GBFR.ExtraSigilSlots.Native.dll");
IntPtr nativeLibrary = NativeLibrary.Load(nativePath);
try
{
    IntPtr bootstrapExport = NativeLibrary.GetExport(
        nativeLibrary,
        "GBFR20_StandaloneBootstrap");
    StandaloneBootstrap bootstrap =
        Marshal.GetDelegateForFunctionPointer<StandaloneBootstrap>(bootstrapExport);

    AssertRejected(bootstrap, IntPtr.Zero, "null configuration");
    AssertRejected(bootstrap, CreateConfiguration(magic: 0), "bad magic");
    AssertRejected(
        bootstrap,
        CreateConfiguration(protocolVersion: ProtocolVersion + 1),
        "unsupported protocol version");
    AssertRejected(
        bootstrap,
        CreateConfiguration(structSize: BootstrapSize - 1),
        "wrong structure size");
    AssertRejected(bootstrap, CreateConfiguration(), "empty data directory");
    AssertRejected(
        bootstrap,
        CreateConfiguration(directory: "relative-data"),
        "relative data directory");
    AssertRejected(
        bootstrap,
        CreateConfiguration(terminateDirectory: false),
        "unterminated data directory");

    Console.WriteLine("STANDALONE_AGENT_TEST=PASS");
    Console.WriteLine($"BOOTSTRAP_SIZE={BootstrapSize}");
    Console.WriteLine($"FRAME_HEADER_SIZE={FrameHeaderSize}");
    Console.WriteLine($"HELLO_RESPONSE_SIZE={HelloResponseSize}");
    Console.WriteLine($"STATE_RESPONSE_SIZE={StateResponseSize}");
    Console.WriteLine("INVALID_BOOTSTRAPS=REJECTED");
}
finally
{
    NativeLibrary.Free(nativeLibrary);
}

static IntPtr CreateConfiguration(
    uint magic = BootstrapMagic,
    ushort protocolVersion = ProtocolVersion,
    int structSize = BootstrapSize,
    string? directory = null,
    bool terminateDirectory = true)
{
    byte[] bytes = new byte[BootstrapSize];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), magic);
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4, 2), protocolVersion);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), unchecked((uint)structSize));
    BinaryPrimitives.WriteUInt32LittleEndian(
        bytes.AsSpan(12, 4),
        unchecked((uint)Environment.ProcessId + 1));

    if (!terminateDirectory)
    {
        for (int index = 0; index < DataDirectoryCapacity; ++index)
            BinaryPrimitives.WriteUInt16LittleEndian(
                bytes.AsSpan(DataDirectoryOffset + index * sizeof(ushort), sizeof(ushort)),
                (ushort)'x');
    }
    else if (!string.IsNullOrEmpty(directory))
    {
        byte[] encoded = Encoding.Unicode.GetBytes(directory);
        if (encoded.Length >= DataDirectoryCapacity * sizeof(ushort))
            throw new ArgumentOutOfRangeException(nameof(directory));
        encoded.CopyTo(bytes, DataDirectoryOffset);
    }

    IntPtr buffer = Marshal.AllocHGlobal(bytes.Length);
    Marshal.Copy(bytes, 0, buffer, bytes.Length);
    return buffer;
}

static void AssertRejected(
    StandaloneBootstrap bootstrap,
    IntPtr configuration,
    string scenario)
{
    try
    {
        uint result = bootstrap(configuration);
        if (result != 0)
            throw new InvalidOperationException($"Bootstrap accepted {scenario}.");
    }
    finally
    {
        if (configuration != IntPtr.Zero)
            Marshal.FreeHGlobal(configuration);
    }
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate uint StandaloneBootstrap(IntPtr configuration);
