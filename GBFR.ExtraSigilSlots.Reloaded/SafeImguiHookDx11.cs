// This file is based on Reloaded.Imgui.Hook.Direct3D11's ImguiHookDx11.
// Copyright (c) 2020 Sewer56
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using DearImguiSharp;
using Reloaded.Hooks.Definitions;
using Reloaded.Imgui.Hook;
using Reloaded.Imgui.Hook.DirectX.Definitions;
using Reloaded.Imgui.Hook.DirectX.Hooks;
using Reloaded.Imgui.Hook.Implementations;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using static Reloaded.Imgui.Hook.Misc.Native;
using Device = SharpDX.Direct3D11.Device;

namespace GBFR.ExtraSigilSlots.Reloaded;

/// <summary>
/// Present-only DX11 ImGui backend. Render targets are created only for frames
/// which contain UI draw data and are released before the original Present.
/// This deliberately avoids a ResizeBuffers hook, because another managed hook
/// in that chain can be an UnmanagedCallersOnly callback which cannot safely be
/// invoked by Reloaded.Hooks from managed code on every Windows configuration.
/// </summary>
internal sealed unsafe class SafeImguiHookDx11 : IImguiHook
{
    private static readonly string[] SupportedDlls =
    [
        "d3d11.dll",
        "d3d11_1.dll",
        "d3d11_2.dll",
        "d3d11_3.dll",
        "d3d11_4.dll",
    ];

    private static SafeImguiHookDx11? s_instance;
    private static readonly IntPtr FailureResult = new(unchecked((int)0x80004005));

    [ThreadStatic]
    private static bool s_presentRecursionLock;

    private readonly Action _presentTick;
    private readonly Func<bool> _shouldRenderFrontend;
    private readonly Action<string> _log;
    private readonly object _hookStateLock = new();
    private readonly ReaderWriterLockSlim _presentLifetimeLock =
        new(LockRecursionPolicy.SupportsRecursion);
    private IHook<DX11Hook.Present> _presentHook = null!;
    private long _originalPresentAddress;
    private bool _initialized;
    private int _presentFailureCount;
    private int _nativePresentFailureHandled;
    private int _presentStopping;
    private bool _disposed;

    internal SafeImguiHookDx11(
        Action presentTick,
        Func<bool> shouldRenderFrontend,
        Action<string> log)
    {
        _presentTick = presentTick;
        _shouldRenderFrontend = shouldRenderFrontend;
        _log = log;
    }

    public bool IsApiSupported()
    {
        foreach (string dll in SupportedDlls)
        {
            if (GetModuleHandle(dll) != IntPtr.Zero)
                return true;
        }
        return false;
    }

    public void Initialize()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SafeImguiHookDx11));

        long presentPointer =
            (long)DX11Hook.DXGIVTable[(int)IDXGISwapChain.Present].FunctionPointer;

        s_instance = this;
        try
        {
            _presentHook = SDK.Hooks.CreateHook<DX11Hook.Present>(
                typeof(SafeImguiHookDx11),
                nameof(PresentImplStatic),
                presentPointer);
            Volatile.Write(
                ref _originalPresentAddress,
                _presentHook.OriginalFunctionAddress);
            _presentHook.Activate();
            TryLog(
                "DX11 Present-only backend enabled with a native original-Present boundary; " +
                "frame-local render targets replace the ResizeBuffers hook.");
        }
        catch
        {
            try
            {
                _presentHook?.Disable();
            }
            catch
            {
            }
            if (ReferenceEquals(s_instance, this))
                s_instance = null;
            throw;
        }
    }

    private IntPtr PresentImpl(IntPtr swapChainPointer, int syncInterval, PresentFlags flags)
    {
        _presentLifetimeLock.EnterReadLock();
        try
        {
            if (Volatile.Read(ref _presentStopping) != 0 || s_presentRecursionLock)
                return InvokeOriginalPresent(swapChainPointer, syncInterval, flags);

            s_presentRecursionLock = true;
            try
            {
                try
                {
                    // The callback owns the native pointer. SharpDX's pointer
                    // constructor does not AddRef, so this borrowed wrapper must
                    // not be disposed (Dispose would release the game's object).
                    var swapChain = new SwapChain(swapChainPointer);
                    IntPtr windowHandle = swapChain.Description.OutputHandle;
                    if (!ImguiHook.CheckWindowHandle(windowHandle))
                        return InvokeOriginalPresent(swapChainPointer, syncInterval, flags);

                    // Keep the game/native hook work on its established game-
                    // window Present cadence. Only the frontend below sleeps.
                    _presentTick();

                    // Initialize the Win32/DX11 backend once so WndProc can wake
                    // the overlay. After that, a closed frontend exits before
                    // device acquisition and every ImGui frame/render call.
                    if (_initialized && !_shouldRenderFrontend())
                        return InvokeOriginalPresent(swapChainPointer, syncInterval, flags);

                    using var device = swapChain.GetDevice<Device>();
                    if (!_initialized)
                    {
                        ImguiHook.InitializeWithHandle(windowHandle);
                        ImGui.ImGuiImplDX11Init(
                            (void*)device.NativePointer,
                            (void*)device.ImmediateContext.NativePointer);
                        _initialized = true;
                    }

                    if (!_shouldRenderFrontend())
                        return InvokeOriginalPresent(swapChainPointer, syncInterval, flags);

                    ImGui.ImGuiImplDX11NewFrame();
                    ImguiHook.NewFrame();
                    using var drawData = ImGui.GetDrawData();
                    if (drawData.CmdListsCount > 0 && drawData.TotalVtxCount > 0)
                        RenderFrame(swapChain, device, drawData);
                }
                catch (Exception exception)
                {
                    ReportFailure("Present overlay", exception);
                }

                return InvokeOriginalPresent(swapChainPointer, syncInterval, flags);
            }
            catch (Exception exception)
            {
                ReportFailure("Present callback", exception);
                return FailureResult;
            }
            finally
            {
                s_presentRecursionLock = false;
            }
        }
        finally
        {
            _presentLifetimeLock.ExitReadLock();
        }
    }

    private static void RenderFrame(
        SwapChain swapChain,
        Device device,
        ImDrawData drawData)
    {
        using var backBuffer = swapChain.GetBackBuffer<Texture2D>(0);
        using var renderTarget = new RenderTargetView(device, backBuffer);
        bool renderTargetBound = false;
        try
        {
            device.ImmediateContext.OutputMerger.SetRenderTargets(renderTarget);
            renderTargetBound = true;
            ImGui.ImGuiImplDX11RenderDrawData(drawData);
        }
        finally
        {
            if (renderTargetBound)
            {
                // Present does not require an OM render target. Unbinding here
                // releases the context's indirect back-buffer reference before
                // the temporary RTV and texture wrappers are disposed.
                device.ImmediateContext.OutputMerger.SetRenderTargets(
                    (RenderTargetView)null!);
            }
        }
    }

    private IntPtr InvokeOriginalPresent(
        IntPtr swapChainPointer,
        int syncInterval,
        PresentFlags flags)
    {
        try
        {
            long originalPresentAddress = Volatile.Read(ref _originalPresentAddress);
            if (originalPresentAddress == 0)
                return FailureResult;

            int result = NativeCore.InvokeOriginalPresent(
                unchecked((ulong)originalPresentAddress),
                swapChainPointer,
                syncInterval,
                unchecked((uint)flags),
                out uint exceptionCode);
            if (exceptionCode != 0)
                HandleNativePresentFailure(exceptionCode);
            return new IntPtr(result);
        }
        catch (Exception exception)
        {
            ReportFailure("original Present", exception);
            return FailureResult;
        }
    }

    private void HandleNativePresentFailure(uint exceptionCode)
    {
        if (Interlocked.Exchange(ref _nativePresentFailureHandled, 1) != 0)
            return;

        Volatile.Write(ref _presentStopping, 1);
        TryLog(
            $"DX11 original Present native boundary caught SEH 0x{exceptionCode:X8}; " +
            "the overlay hook will be disabled off the graphics callback thread.");
        if (!ThreadPool.QueueUserWorkItem(
                static state => ((SafeImguiHookDx11)state!).DisableAfterNativePresentFailure(),
                this))
        {
            TryLog("DX11 could not queue the native-Present failure fallback.");
        }
    }

    private void DisableAfterNativePresentFailure()
    {
        try
        {
            Disable();
            TryLog(
                "DX11 overlay hook disabled after a native Present failure; " +
                "later frames now use the game's Present directly.");
        }
        catch (Exception exception)
        {
            ReportFailure("hook disable after native Present failure", exception);
        }
    }

    private void ReportFailure(string stage, Exception exception)
    {
        int failureNumber = Interlocked.Increment(ref _presentFailureCount);
        if (failureNumber > 3 && (failureNumber & (failureNumber - 1)) != 0)
            return;
        TryLog(
            $"DX11 {stage} failed (occurrence {failureNumber}); " +
            $"the game callback was contained instead of crashing: {exception}");
    }

    private void TryLog(string message)
    {
        try
        {
            _log(message);
        }
        catch
        {
            // Logging must never make an unmanaged graphics callback fail.
        }
    }

    public void Disable()
    {
        lock (_hookStateLock)
            _presentHook?.Disable();
    }

    public void Enable()
    {
        lock (_hookStateLock)
        {
            if (_disposed || Volatile.Read(ref _nativePresentFailureHandled) != 0)
                return;
            _presentHook?.Enable();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Volatile.Write(ref _presentStopping, 1);

        try
        {
            Disable();
        }
        catch (Exception exception)
        {
            ReportFailure("hook disable", exception);
        }

        bool lifetimeLockEntered = false;
        try
        {
            if (!s_presentRecursionLock)
            {
                lifetimeLockEntered = _presentLifetimeLock.TryEnterWriteLock(
                    TimeSpan.FromSeconds(2));
            }
            if (!lifetimeLockEntered)
            {
                TryLog(
                    "DX11 shutdown skipped graphics resource release because a Present callback " +
                    "was still active; process teardown will reclaim those resources.");
            }
            else if (_initialized)
            {
                ImGui.ImGuiImplDX11Shutdown();
                _initialized = false;
            }
        }
        catch (Exception exception)
        {
            ReportFailure("shutdown", exception);
        }
        finally
        {
            if (ReferenceEquals(s_instance, this))
                s_instance = null;
            if (lifetimeLockEntered)
                _presentLifetimeLock.ExitWriteLock();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static IntPtr PresentImplStatic(
        IntPtr swapChainPointer,
        int syncInterval,
        PresentFlags flags)
    {
        try
        {
            SafeImguiHookDx11? instance = s_instance;
            return instance is null
                ? FailureResult
                : instance.PresentImpl(swapChainPointer, syncInterval, flags);
        }
        catch (Exception exception)
        {
            SafeImguiHookDx11? instance = s_instance;
            instance?.ReportFailure("Present unmanaged boundary", exception);
            return FailureResult;
        }
    }
}
