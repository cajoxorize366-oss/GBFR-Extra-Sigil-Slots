using Reloaded.Hooks.ReloadedII.Interfaces;
using Reloaded.Imgui.Hook;
using Reloaded.Imgui.Hook.Direct3D11;
using Reloaded.Imgui.Hook.Implementations;
using Reloaded.Mod.Interfaces;
using Reloaded.Mod.Interfaces.Internal;

namespace GBFR.ExtraSigilSlots20.Reloaded;

public sealed partial class Mod : IMod
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
                        // The selector lives inside the game window. Extra platform
                        // windows add another DXGI/compositor path and can leave
                        // transient black trails while the mouse is moving.
                        EnableViewports = false,
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
}
