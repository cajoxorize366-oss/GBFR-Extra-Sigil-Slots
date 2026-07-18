using DearImguiSharp;
using Reloaded.Hooks.ReloadedII.Interfaces;
using Reloaded.Imgui.Hook;
using Reloaded.Imgui.Hook.Direct3D11;
using Reloaded.Imgui.Hook.Implementations;
using Reloaded.Mod.Interfaces;
using Reloaded.Mod.Interfaces.Internal;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GBFR.ExtraSigilSlots20.Reloaded;

public sealed class Mod : IMod
{
    private const string ModId = "GBFR.ExtraSigilSlots20.Reloaded";

    private readonly object _lifecycleLock = new();
    private readonly object _imguiOperationLock = new();
    private readonly object _logLock = new();
    private ILogger? _logger;
    private StreamWriter? _fileLog;
    private SigilOverlayUi? _ui;
    private bool _starting;
    private bool _started;
    private bool _imguiCreated;
    private bool _nativeCoreActive;
    private bool _disposed;
    private int _renderThreadLogged;
    private int _inputCaptureLogged;
    private int _renderStopping;
    private int _activeRenderCallbacks;
    private static int s_captureInput;
    private static int s_hasOriginalWndProc;
    private static int s_capturedRawInputMessages;
    private static int s_capturedMouseKeyboardMessages;
    private static int s_pendingAnsiLeadByte;
    private static int s_pendingAnsiCodePage;
    private static int s_imeCompositionActive;
    private static int s_imeResultInjected;
    private static WndProcHook.WndProc s_originalWndProc;

    public Action Disposing => Dispose;

    public void Start(IModLoaderV1 loader) => QueueStart(loader, ModId);

    public void StartEx(IModLoaderV1 loader, IModConfigV1 config) =>
        QueueStart(loader, config.ModId);

    public void Suspend()
    {
        lock (_imguiOperationLock)
        {
            lock (_lifecycleLock)
            {
                if (!_started || !_imguiCreated || _disposed)
                    return;
            }
            ForceReleaseInputCapture();
            Volatile.Write(ref _renderStopping, 1);
            _ui?.Close();
            ImguiHook.Disable();
        }
    }

    public void Resume()
    {
        lock (_imguiOperationLock)
        {
            lock (_lifecycleLock)
            {
                if (!_started || !_imguiCreated || _disposed)
                    return;
            }
            Volatile.Write(ref _renderStopping, 0);
            ImguiHook.Enable();
        }
    }

    public void Unload() => Dispose();

    public bool CanUnload() => false;

    public bool CanSuspend() => true;

    private void QueueStart(IModLoaderV1 loaderApi, string modId)
    {
        lock (_lifecycleLock)
        {
            if (_starting || _started || _disposed)
                return;
            _starting = true;
        }

        _ = StartCoreAsync(loaderApi, modId);
    }

    private async Task StartCoreAsync(IModLoaderV1 loaderApi, string modId)
    {
        try
        {
            IModLoader loader = (IModLoader)loaderApi;
            lock (_logLock)
                _logger = (ILogger)loader.GetLogger();

            string modDirectory = loader.GetDirectoryForModId(modId);
            Directory.CreateDirectory(modDirectory);
            lock (_logLock)
            {
                _fileLog?.Dispose();
                _fileLog = new StreamWriter(
                    Path.Combine(modDirectory, "ExtraSigilSlots20.Reloaded.log"),
                    append: false
                )
                {
                    AutoFlush = true,
                };
            }

            if (loader.GetController<IReloadedHooks>() is not { } hooksController ||
                !hooksController.TryGetTarget(out IReloadedHooks? hooks) ||
                hooks is null)
            {
                throw new InvalidOperationException(
                    "Reloaded.Hooks controller is unavailable. Enable reloaded.sharedlib.hooks."
                );
            }

            NativeCore.Configure(modDirectory);
            bool hooksReady = NativeCore.Initialize();
            bool shutdownImmediately;
            lock (_lifecycleLock)
            {
                shutdownImmediately = _disposed;
                if (!shutdownImmediately)
                    _nativeCoreActive = true;
                else
                    _starting = false;
            }
            if (shutdownImmediately)
            {
                NativeCore.Shutdown();
                return;
            }

            Log(
                hooksReady
                    ? "ReShade-free native core initialized and hooks passed preflight."
                    : $"Native core loaded without hooks: {NativeCore.GetRuntimeMessage()}"
            );

            SDK.Init(hooks, message =>
            {
                if (!message.Contains(
                        "Discarding via Recursion Lock",
                        StringComparison.Ordinal))
                {
                    Log($"Reloaded.Imgui.Hook: {message}");
                }
            });
            await ImguiHook.Create(
                    Render,
                    new ImguiHookOptions
                    {
                        EnableViewports = true,
                        IgnoreWindowUnactivate = true,
                        CustomWndProcHandlerPointer = GetWndProcHandlerPointer(),
                        Implementations = new List<IImguiHook>
                        {
                            new CjkConfiguredDx11Hook(modDirectory, Log),
                        },
                    }
                )
                .ConfigureAwait(false);

            SigilOverlayUi ui = new(modDirectory, SetInputCapture, Log);
            bool initialized = false;
            lock (_imguiOperationLock)
            {
                lock (_lifecycleLock)
                {
                    _starting = false;
                    if (!_disposed)
                    {
                        _ui = ui;
                        _imguiCreated = true;
                        _started = true;
                        Volatile.Write(ref _renderStopping, 0);
                        initialized = true;
                    }
                }

                if (!initialized)
                {
                    ImguiHook.Destroy();
                    ui.Dispose();
                }
            }

            if (!initialized)
                return;

            Log("Direct3D11 Reloaded ImGui frontend initialized; ReShade and Luma are not used by this mod.");
            Log("Press F8 to open the extra-sigil selector.");
        }
        catch (Exception exception)
        {
            bool shutdownCore;
            lock (_imguiOperationLock)
            {
                lock (_lifecycleLock)
                {
                    _starting = false;
                    shutdownCore = _nativeCoreActive;
                    _nativeCoreActive = false;
                }
            }
            if (shutdownCore)
            {
                try
                {
                    NativeCore.Shutdown();
                }
                catch
                {
                    // Preserve the original initialization failure in the log.
                }
            }
            Log($"Initialization failed: {exception}");
        }
    }

    private void Render()
    {
        Interlocked.Increment(ref _activeRenderCallbacks);
        try
        {
            if (Volatile.Read(ref _renderStopping) != 0)
                return;
            if (Interlocked.CompareExchange(ref _renderThreadLogged, 1, 0) == 0)
                Log($"First Direct3D11 Present callback: OS TID {GetCurrentThreadId()}.");
            _ui?.RenderFrame();
            if (Volatile.Read(ref s_capturedRawInputMessages) +
                    Volatile.Read(ref s_capturedMouseKeyboardMessages) > 0 &&
                Interlocked.CompareExchange(ref _inputCaptureLogged, 1, 0) == 0)
            {
                Log(
                    $"GUI input capture confirmed: raw={Volatile.Read(ref s_capturedRawInputMessages)}, " +
                    $"mouse/keyboard={Volatile.Read(ref s_capturedMouseKeyboardMessages)}."
                );
            }
        }
        catch (Exception exception)
        {
            SetInputCapture(false);
            _ui?.Close();
            Log($"Render callback recovered from an exception: {exception}");
        }
        finally
        {
            Interlocked.Decrement(ref _activeRenderCallbacks);
        }
    }

    private void Log(string message)
    {
        string line = $"[{ModId}] {message}";
        lock (_logLock)
        {
            try
            {
                _logger?.WriteLine(line);
                _fileLog?.WriteLine(line);
            }
            catch
            {
                // Logging must never tear down the render callback.
            }
        }
    }

    private void Dispose()
    {
        SigilOverlayUi? ui;
        bool destroyImgui;
        bool shutdownCore;
        lock (_imguiOperationLock)
        {
            lock (_lifecycleLock)
            {
                if (_disposed)
                    return;

                _disposed = true;
                ui = _ui;
                _ui = null;
                destroyImgui = _imguiCreated;
                shutdownCore = _nativeCoreActive;
                _imguiCreated = false;
                _nativeCoreActive = false;
                _started = false;
            }

            ForceReleaseInputCapture();
            Volatile.Write(ref _renderStopping, 1);
            if (destroyImgui)
                ImguiHook.Disable();
        }

        bool renderDrained = SpinWait.SpinUntil(
            () => Volatile.Read(ref _activeRenderCallbacks) == 0,
            TimeSpan.FromSeconds(5)
        );
        if (renderDrained)
        {
            lock (_imguiOperationLock)
            {
                if (destroyImgui)
                    ImguiHook.Destroy();
                ui?.Dispose();
            }
            if (shutdownCore)
                NativeCore.Shutdown();
        }
        else
        {
            Log(
                "Unload cleanup was deferred because a render callback did not drain in five seconds; " +
                "the mod is marked non-unloadable and its modules remain resident until process exit."
            );
        }

        lock (_logLock)
        {
            _fileLog?.Dispose();
            _fileLog = null;
        }
    }

    private static void SetInputCapture(bool capture)
    {
        bool effective = capture;
        try
        {
            NativeCore.SetInputCapture(capture);
            effective = capture || NativeCore.IsInputCaptureActive();
        }
        catch
        {
            // Keep the Win32 barrier available even if the native input layer is unavailable.
        }
        int captureValue = effective ? 1 : 0;
        int previousValue = Interlocked.Exchange(ref s_captureInput, captureValue);
        if (previousValue != captureValue)
            ClearTextInputState();
    }

    private static void ForceReleaseInputCapture()
    {
        try
        {
            NativeCore.ForceReleaseInput();
        }
        catch
        {
            // The native module may not have been loaded yet.
        }
        Volatile.Write(ref s_captureInput, 0);
        ClearTextInputState();
    }

    private static unsafe IntPtr GetWndProcHandlerPointer()
    {
        return (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProcHandler;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe IntPtr WndProcHandler(
        IntPtr hWnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam)
    {
        try
        {
            if (Volatile.Read(ref s_captureInput) != 0)
            {
                if (message == 0x0051)
                    ClearTextInputState();
                else if (message is 0x0100 or 0x0101 or 0x0104 or 0x0105)
                {
                    ClearPendingAnsiCharacter();
                    if ((message == 0x0100 || message == 0x0104) &&
                        Volatile.Read(ref s_imeResultInjected) != 0)
                    {
                        // A new physical key starts a new input transaction even if an IME
                        // failed to send WM_IME_ENDCOMPOSITION for the previous result.
                        Volatile.Write(ref s_imeCompositionActive, 0);
                        Volatile.Write(ref s_imeResultInjected, 0);
                    }
                }
                if (TryHandleCapturedTextInput(
                        hWnd,
                        message,
                        wParam,
                        lParam,
                        out IntPtr textResult))
                {
                    Interlocked.Increment(ref s_capturedMouseKeyboardMessages);
                    return textResult;
                }
            }
            ImGui.ImplWin32_WndProcHandler((void*)hWnd, message, wParam, lParam);
            if (ImguiHook.Options?.IgnoreWindowUnactivate == true)
            {
                if (message == 0x0008)
                    return IntPtr.Zero;
                if ((message == 0x0006 || message == 0x001C) && wParam == IntPtr.Zero)
                    return IntPtr.Zero;
            }
            if (Volatile.Read(ref s_captureInput) != 0 &&
                ShouldCaptureMessage(message, lParam))
            {
                if (message == 0x00FF)
                    Interlocked.Increment(ref s_capturedRawInputMessages);
                else
                    Interlocked.Increment(ref s_capturedMouseKeyboardMessages);
                return IntPtr.Zero;
            }
        }
        catch
        {
            // Input classification is fail-open: preserve the game's own
            // WndProc path if ImGui or Raw Input inspection fails.
        }

        try
        {
            WndProcHook? hook = WndProcHook.Instance;
            if (hook is not null)
            {
                WndProcHook.WndProc original = hook.Hook.OriginalFunction;
                s_originalWndProc = original;
                Volatile.Write(ref s_hasOriginalWndProc, 1);
                return original.Value.Invoke(hWnd, message, wParam, lParam);
            }
            if (Volatile.Read(ref s_hasOriginalWndProc) != 0)
                return s_originalWndProc.Value.Invoke(hWnd, message, wParam, lParam);
            return DefWindowProcW(hWnd, message, wParam, lParam);
        }
        catch
        {
            return DefWindowProcW(hWnd, message, wParam, lParam);
        }
    }

    private static unsafe bool TryHandleCapturedTextInput(
        IntPtr hWnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        out IntPtr result)
    {
        const uint WmChar = 0x0102;
        const uint WmUniChar = 0x0109;
        const uint UnicodeNoChar = 0xFFFF;
        const uint WmImeStartComposition = 0x010D;
        const uint WmImeEndComposition = 0x010E;
        const uint WmImeComposition = 0x010F;
        const uint WmImeChar = 0x0286;
        const long GcsResultStr = 0x0800;

        result = IntPtr.Zero;
        switch (message)
        {
            case WmChar:
                if (Volatile.Read(ref s_imeResultInjected) != 0)
                    return true;
                // Some games expose a Unicode HWND while their IME thunk still posts ANSI
                // DBCS bytes. Consume every WM_CHAR here so the old ImGui backend cannot
                // inject those bytes as separate Latin-1 characters.
                return TryHandleWindowChar(hWnd, wParam);

            case WmUniChar:
            {
                uint codePoint = unchecked((uint)(nuint)wParam);
                if (codePoint == UnicodeNoChar)
                {
                    result = new IntPtr(1);
                    return true;
                }
                if (codePoint <= 0x10FFFF)
                    ImGui.ImGuiIO_AddInputCharacter(ImGui.GetIO(), codePoint);
                return true;
            }

            case WmImeStartComposition:
                ClearPendingAnsiCharacter();
                Volatile.Write(ref s_imeCompositionActive, 1);
                Volatile.Write(ref s_imeResultInjected, 0);
                result = DefWindowProcW(hWnd, message, wParam, lParam);
                return true;

            case WmImeEndComposition:
                ClearPendingAnsiCharacter();
                Volatile.Write(ref s_imeCompositionActive, 0);
                Volatile.Write(ref s_imeResultInjected, 0);
                result = DefWindowProcW(hWnd, message, wParam, lParam);
                return true;

            case WmImeComposition:
                if ((lParam.ToInt64() & GcsResultStr) != 0 &&
                    TryInjectImeResult(hWnd))
                {
                    // The committed string came directly from IMM as UTF-16. Do not call
                    // DefWindowProcW here, otherwise it can emit ANSI WM_IME_CHAR/WM_CHAR
                    // messages for the same text and duplicate or corrupt the input.
                    Volatile.Write(ref s_imeResultInjected, 1);
                    return true;
                }

                Volatile.Write(ref s_imeResultInjected, 0);
                result = DefWindowProcW(hWnd, message, wParam, lParam);
                return true;

            case WmImeChar:
                if (Volatile.Read(ref s_imeResultInjected) != 0)
                {
                    result = new IntPtr(1);
                    return true;
                }
                if (IsWindowUnicode(hWnd))
                {
                    // Let the Unicode default procedure turn the committed IME character into WM_CHAR.
                    result = DefWindowProcW(hWnd, message, wParam, lParam);
                    return true;
                }

                ClearPendingAnsiCharacter();
                if (TryDecodeAnsiCharacter(
                        GetKeyboardCodePage(hWnd),
                        unchecked((ushort)(nuint)wParam),
                        out ushort character))
                {
                    ImGui.ImGuiIO_AddInputCharacterUTF16(ImGui.GetIO(), character);
                }
                result = new IntPtr(1);
                return true;

            default:
                return false;
        }
    }

    private static bool TryHandleWindowChar(IntPtr hWnd, IntPtr wParam)
    {
        uint value = unchecked((uint)(nuint)wParam);
        uint codePage = GetKeyboardCodePage(hWnd);
        if (value == 0 || value > ushort.MaxValue)
            return true;
        if (value > byte.MaxValue)
        {
            ClearPendingAnsiCharacter();
            if (IsWindowUnicode(hWnd))
            {
                ImGui.ImGuiIO_AddInputCharacterUTF16(
                    ImGui.GetIO(),
                    unchecked((ushort)value)
                );
                return true;
            }
            return InjectDecodedAnsiCharacter(codePage, unchecked((ushort)value));
        }

        byte current = unchecked((byte)value);
        if (TryDecodeWindowByte(codePage, current, out ushort character))
            ImGui.ImGuiIO_AddInputCharacterUTF16(ImGui.GetIO(), character);
        return true;
    }

    private static bool TryDecodeWindowByte(
        uint codePage,
        byte current,
        out ushort character)
    {
        character = 0;
        int pendingLead = Interlocked.Exchange(ref s_pendingAnsiLeadByte, 0);
        if (pendingLead != 0)
        {
            uint pendingCodePage = unchecked((uint)Interlocked.Exchange(
                ref s_pendingAnsiCodePage,
                0
            ));
            if (pendingCodePage != 0)
            {
                ushort packed = (ushort)((pendingLead << 8) | current);
                if (TryDecodeAnsiCharacter(pendingCodePage, packed, out character))
                    return true;
            }
        }

        uint leadCodePage = codePage;
        if (!IsDBCSLeadByteEx(leadCodePage, current) &&
            leadCodePage != 936 &&
            Volatile.Read(ref s_imeCompositionActive) != 0 &&
            IsDBCSLeadByteEx(936, current))
        {
            // Microsoft Pinyin may deliver GBK bytes even when the game's thread reports
            // a Western code page. CP936 is therefore a deliberate Chinese-IME fallback.
            leadCodePage = 936;
        }
        if (IsDBCSLeadByteEx(leadCodePage, current))
        {
            Volatile.Write(ref s_pendingAnsiCodePage, unchecked((int)leadCodePage));
            Volatile.Write(ref s_pendingAnsiLeadByte, current);
            return false;
        }

        if (current < 0x80)
        {
            character = current;
            return true;
        }
        return TryDecodeAnsiCharacter(codePage, current, out character);
    }

    private static unsafe bool TryInjectImeResult(IntPtr hWnd)
    {
        const uint GcsResultStr = 0x0800;
        IntPtr inputContext = ImmGetContext(hWnd);
        if (inputContext == IntPtr.Zero)
            return false;

        try
        {
            int requiredBytes = ImmGetCompositionStringW(
                inputContext,
                GcsResultStr,
                null,
                0
            );
            if (requiredBytes <= 0 || (requiredBytes & 1) != 0 || requiredBytes > 8192)
                return false;

            byte[] buffer = new byte[requiredBytes];
            fixed (byte* bufferPointer = buffer)
            {
                int copiedBytes = ImmGetCompositionStringW(
                    inputContext,
                    GcsResultStr,
                    bufferPointer,
                    unchecked((uint)buffer.Length)
                );
                if (copiedBytes <= 0 || (copiedBytes & 1) != 0)
                    return false;

                ushort* characters = (ushort*)bufferPointer;
                int characterCount = copiedBytes / sizeof(ushort);
                for (int index = 0; index < characterCount; ++index)
                {
                    ImGui.ImGuiIO_AddInputCharacterUTF16(
                        ImGui.GetIO(),
                        characters[index]
                    );
                }
                return true;
            }
        }
        finally
        {
            ImmReleaseContext(hWnd, inputContext);
        }
    }

    private static void ClearPendingAnsiCharacter()
    {
        Interlocked.Exchange(ref s_pendingAnsiLeadByte, 0);
        Interlocked.Exchange(ref s_pendingAnsiCodePage, 0);
    }

    private static void ClearTextInputState()
    {
        ClearPendingAnsiCharacter();
        Volatile.Write(ref s_imeCompositionActive, 0);
        Volatile.Write(ref s_imeResultInjected, 0);
    }

    private static bool InjectDecodedAnsiCharacter(uint codePage, ushort packed)
    {
        if (!TryDecodeAnsiCharacter(codePage, packed, out ushort character))
            return true;
        ImGui.ImGuiIO_AddInputCharacterUTF16(ImGui.GetIO(), character);
        return true;
    }

    internal static unsafe bool TryDecodeAnsiCharacter(
        uint codePage,
        ushort packed,
        out ushort character)
    {
        byte* bytes = stackalloc byte[2];
        byte first = unchecked((byte)(packed >> 8));
        int byteCount;
        if (first == 0)
        {
            bytes[0] = unchecked((byte)packed);
            byteCount = 1;
        }
        else
        {
            bytes[0] = first;
            bytes[1] = unchecked((byte)packed);
            byteCount = 2;
        }

        char decoded = '\0';
        int count = MultiByteToWideChar(
            codePage,
            0x00000001,
            bytes,
            byteCount,
            &decoded,
            1
        );
        character = decoded;
        return count == 1 && decoded != '\0';
    }

    private static uint GetKeyboardCodePage(IntPtr hWnd)
    {
        uint threadId = GetWindowThreadProcessId(hWnd, out _);
        ulong layout = unchecked((ulong)GetKeyboardLayout(threadId).ToInt64());
        ushort lowLanguage = unchecked((ushort)layout);
        if (TryGetAnsiCodePage(lowLanguage, out uint codePage))
            return codePage;
        return GetACP();
    }

    private static bool TryGetAnsiCodePage(ushort language, out uint codePage)
    {
        const uint LocaleReturnNumber = 0x20000000;
        const uint LocaleDefaultAnsiCodePage = 0x00001004;
        codePage = 0;
        return language != 0 &&
            GetLocaleInfoA(
                language,
                LocaleReturnNumber | LocaleDefaultAnsiCodePage,
                out codePage,
                sizeof(uint)
            ) != 0 &&
            codePage != 0;
    }

    private static bool ShouldCaptureMessage(uint message, IntPtr lParam)
    {
        const uint WmInput = 0x00FF;
        const uint WmNcMouseFirst = 0x00A0;
        const uint WmNcMouseLast = 0x00AD;
        const uint WmGestureFirst = 0x0119;
        const uint WmGestureLast = 0x011A;
        const uint WmKeyFirst = 0x0100;
        const uint WmKeyLast = 0x0109;
        const uint WmMouseFirst = 0x0200;
        const uint WmMouseLast = 0x020E;
        const uint WmTouch = 0x0240;
        const uint WmPointerFirst = 0x0241;
        const uint WmPointerLast = 0x024F;
        const uint WmHotkey = 0x0312;
        const uint WmAppCommand = 0x0319;
        return (message == WmInput && ShouldCaptureRawInput(lParam)) ||
            message is >= WmNcMouseFirst and <= WmNcMouseLast ||
            message is >= WmGestureFirst and <= WmGestureLast ||
            message is >= WmKeyFirst and <= WmKeyLast ||
            message is >= WmMouseFirst and <= WmMouseLast ||
            message == WmTouch ||
            message is >= WmPointerFirst and <= WmPointerLast ||
            message == WmHotkey ||
            message == WmAppCommand;
    }

    private static bool ShouldCaptureRawInput(IntPtr rawInputHandle)
    {
        const uint RidHeader = 0x10000005;
        if (rawInputHandle == IntPtr.Zero)
            return false;

        uint headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        uint dataSize = headerSize;
        uint copied = GetRawInputData(
            rawInputHandle,
            RidHeader,
            out RawInputHeader header,
            ref dataSize,
            headerSize
        );
        return copied != uint.MaxValue && copied >= headerSize &&
            IsKeyboardOrMouseRawInputType(header.Type);
    }

    private static bool IsKeyboardOrMouseRawInputType(uint type) => type is 0 or 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        internal uint Type;
        internal uint Size;
        internal IntPtr Device;
        internal IntPtr WParam;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(
        IntPtr hWnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr rawInputHandle,
        uint command,
        out RawInputHeader data,
        ref uint dataSize,
        uint headerSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowUnicode(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int GetLocaleInfoA(
        uint locale,
        uint localeType,
        out uint localeData,
        int localeDataLength);

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsDBCSLeadByteEx(uint codePage, byte testChar);

    [DllImport("kernel32.dll")]
    private static extern unsafe int MultiByteToWideChar(
        uint codePage,
        uint flags,
        byte* multiByteText,
        int multiByteCount,
        char* wideText,
        int wideCount);

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetContext(IntPtr hWnd);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr inputContext);

    [DllImport("imm32.dll", ExactSpelling = true)]
    private static extern unsafe int ImmGetCompositionStringW(
        IntPtr inputContext,
        uint index,
        void* buffer,
        uint bufferLength);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
