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

namespace GBFR.ExtraSigilSlots20.Reloaded;

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

    private readonly Action<string> _log;
    private IHook<DX11Hook.Present> _presentHook = null!;
    private bool _initialized;
    private bool _presentRecursionLock;
    private int _presentFailureCount;
    private bool _disposed;

    internal SafeImguiHookDx11(Action<string> log)
    {
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
            _presentHook = SDK.Hooks
                .CreateHook<DX11Hook.Present>(
                    typeof(SafeImguiHookDx11),
                    nameof(PresentImplStatic),
                    presentPointer)
                .Activate();
            TryLog(
                "DX11 Present-only backend enabled; " +
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
        if (_presentRecursionLock)
            return InvokeOriginalPresent(swapChainPointer, syncInterval, flags);

        _presentRecursionLock = true;
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

                using var device = swapChain.GetDevice<Device>();
                if (!_initialized)
                {
                    ImguiHook.InitializeWithHandle(windowHandle);
                    ImGui.ImGuiImplDX11Init(
                        (void*)device.NativePointer,
                        (void*)device.ImmediateContext.NativePointer);
                    _initialized = true;
                }

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
            _presentRecursionLock = false;
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
            return _presentHook.OriginalFunction.Value.Invoke(
                swapChainPointer,
                syncInterval,
                flags);
        }
        catch (Exception exception)
        {
            ReportFailure("original Present", exception);
            return FailureResult;
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
        _presentHook?.Disable();
    }

    public void Enable()
    {
        if (_disposed)
            return;
        _presentHook?.Enable();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            Disable();
        }
        catch (Exception exception)
        {
            ReportFailure("hook disable", exception);
        }

        try
        {
            if (_initialized)
                ImGui.ImGuiImplDX11Shutdown();
        }
        catch (Exception exception)
        {
            ReportFailure("shutdown", exception);
        }
        finally
        {
            _initialized = false;
            if (ReferenceEquals(s_instance, this))
                s_instance = null;
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
