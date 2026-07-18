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
using Reloaded.Imgui.Hook.Misc;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using static Reloaded.Imgui.Hook.Misc.Native;
using Device = SharpDX.Direct3D11.Device;

namespace GBFR.ExtraSigilSlots20.Reloaded;

/// <summary>
/// DX11 ImGui backend which guarantees that managed exceptions never escape a
/// native Present or ResizeBuffers callback. An exception crossing an
/// UnmanagedCallersOnly boundary terminates the process before Mod.Render can
/// recover it, which is why this guard must live at the graphics-hook boundary.
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
    private IHook<DX11Hook.ResizeBuffers> _resizeBuffersHook = null!;
    private RenderTargetView? _renderTargetView;
    private bool _initialized;
    private bool _needsDeviceObjectRebuild;
    private bool _presentRecursionLock;
    private bool _resizeRecursionLock;
    private int _presentFailureCount;
    private int _resizeFailureCount;
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
        long presentPointer =
            (long)DX11Hook.DXGIVTable[(int)IDXGISwapChain.Present].FunctionPointer;
        long resizeBuffersPointer =
            (long)DX11Hook.DXGIVTable[(int)IDXGISwapChain.ResizeBuffers].FunctionPointer;

        s_instance = this;
        _presentHook = SDK.Hooks
            .CreateHook<DX11Hook.Present>(
                typeof(SafeImguiHookDx11),
                nameof(PresentImplStatic),
                presentPointer)
            .Activate();
        _resizeBuffersHook = SDK.Hooks
            .CreateHook<DX11Hook.ResizeBuffers>(
                typeof(SafeImguiHookDx11),
                nameof(ResizeBuffersImplStatic),
                resizeBuffersPointer)
            .Activate();
    }

    private IntPtr ResizeBuffersImpl(
        IntPtr swapChainPointer,
        uint bufferCount,
        uint width,
        uint height,
        Format newFormat,
        uint swapChainFlags)
    {
        if (_resizeRecursionLock)
        {
            return InvokeOriginalResizeBuffers(
                swapChainPointer,
                bufferCount,
                width,
                height,
                newFormat,
                swapChainFlags);
        }

        _resizeRecursionLock = true;
        try
        {
            bool belongsToGame;
            try
            {
                var swapChain = new SwapChain(swapChainPointer);
                belongsToGame = ImguiHook.CheckWindowHandle(
                    swapChain.Description.OutputHandle);
            }
            catch (Exception exception)
            {
                ReportFailure("ResizeBuffers preflight", exception, ref _resizeFailureCount);
                return InvokeOriginalResizeBuffers(
                    swapChainPointer,
                    bufferCount,
                    width,
                    height,
                    newFormat,
                    swapChainFlags);
            }

            if (!belongsToGame)
            {
                return InvokeOriginalResizeBuffers(
                    swapChainPointer,
                    bufferCount,
                    width,
                    height,
                    newFormat,
                    swapChainFlags);
            }

            try
            {
                PreResizeBuffers();
            }
            catch (Exception exception)
            {
                ReportFailure("ResizeBuffers cleanup", exception, ref _resizeFailureCount);
            }

            IntPtr result = InvokeOriginalResizeBuffers(
                swapChainPointer,
                bufferCount,
                width,
                height,
                newFormat,
                swapChainFlags);

            if (Succeeded(result))
            {
                try
                {
                    EnsureRenderResources(swapChainPointer);
                }
                catch (Exception exception)
                {
                    ReportFailure("ResizeBuffers rebuild", exception, ref _resizeFailureCount);
                }
            }
            return result;
        }
        catch (Exception exception)
        {
            ReportFailure("ResizeBuffers callback", exception, ref _resizeFailureCount);
            return FailureResult;
        }
        finally
        {
            _resizeRecursionLock = false;
        }
    }

    private void PreResizeBuffers()
    {
        DisposeRenderTarget();
        if (!_initialized)
            return;

        _needsDeviceObjectRebuild = true;
        ImGui.ImGuiImplDX11InvalidateDeviceObjects();
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
                    _needsDeviceObjectRebuild = false;
                }

                EnsureRenderResources(swapChainPointer);
                ImGui.ImGuiImplDX11NewFrame();
                ImguiHook.NewFrame();
                device.ImmediateContext.OutputMerger.SetRenderTargets(_renderTargetView);
                using var drawData = ImGui.GetDrawData();
                ImGui.ImGuiImplDX11RenderDrawData(drawData);
            }
            catch (Exception exception)
            {
                ReportFailure("Present overlay", exception, ref _presentFailureCount);
            }

            return InvokeOriginalPresent(swapChainPointer, syncInterval, flags);
        }
        catch (Exception exception)
        {
            ReportFailure("Present callback", exception, ref _presentFailureCount);
            return FailureResult;
        }
        finally
        {
            _presentRecursionLock = false;
        }
    }

    private void EnsureRenderResources(IntPtr swapChainPointer)
    {
        if (!_initialized)
            return;

        if (_needsDeviceObjectRebuild)
        {
            ImGui.ImGuiImplDX11CreateDeviceObjects();
            _needsDeviceObjectRebuild = false;
        }

        if (_renderTargetView is not null)
            return;

        var swapChain = new SwapChain(swapChainPointer);
        using var device = swapChain.GetDevice<Device>();
        using var backBuffer = swapChain.GetBackBuffer<Texture2D>(0);
        _renderTargetView = new RenderTargetView(device, backBuffer);
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
            ReportFailure("original Present", exception, ref _presentFailureCount);
            return FailureResult;
        }
    }

    private IntPtr InvokeOriginalResizeBuffers(
        IntPtr swapChainPointer,
        uint bufferCount,
        uint width,
        uint height,
        Format newFormat,
        uint swapChainFlags)
    {
        try
        {
            return _resizeBuffersHook.OriginalFunction.Value.Invoke(
                swapChainPointer,
                bufferCount,
                width,
                height,
                newFormat,
                swapChainFlags);
        }
        catch (Exception exception)
        {
            ReportFailure("original ResizeBuffers", exception, ref _resizeFailureCount);
            return FailureResult;
        }
    }

    private static bool Succeeded(IntPtr result) =>
        unchecked((int)result.ToInt64()) >= 0;

    private void ReportFailure(string stage, Exception exception, ref int counter)
    {
        int failureNumber = Interlocked.Increment(ref counter);
        if (failureNumber > 3 && (failureNumber & (failureNumber - 1)) != 0)
            return;

        try
        {
            _log(
                $"DX11 {stage} failed (occurrence {failureNumber}); " +
                $"the game callback was contained instead of crashing: {exception}");
        }
        catch
        {
            // Logging must never make an unmanaged graphics callback fail.
        }
    }

    private void DisposeRenderTarget()
    {
        RenderTargetView? target = _renderTargetView;
        _renderTargetView = null;
        target?.Dispose();
    }

    public void Disable()
    {
        _presentHook?.Disable();
        _resizeBuffersHook?.Disable();
    }

    public void Enable()
    {
        _presentHook?.Enable();
        _resizeBuffersHook?.Enable();
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
            ReportFailure("hook disable", exception, ref _presentFailureCount);
        }

        try
        {
            DisposeRenderTarget();
            if (_initialized)
                ImGui.ImGuiImplDX11Shutdown();
        }
        catch (Exception exception)
        {
            ReportFailure("shutdown", exception, ref _presentFailureCount);
        }
        finally
        {
            _initialized = false;
            if (ReferenceEquals(s_instance, this))
                s_instance = null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static IntPtr ResizeBuffersImplStatic(
        IntPtr swapChainPointer,
        uint bufferCount,
        uint width,
        uint height,
        Format newFormat,
        uint swapChainFlags)
    {
        try
        {
            SafeImguiHookDx11? instance = s_instance;
            return instance is null
                ? FailureResult
                : instance.ResizeBuffersImpl(
                    swapChainPointer,
                    bufferCount,
                    width,
                    height,
                    newFormat,
                    swapChainFlags);
        }
        catch (Exception exception)
        {
            SafeImguiHookDx11? instance = s_instance;
            if (instance is not null)
                instance.ReportFailure(
                    "ResizeBuffers unmanaged boundary",
                    exception,
                    ref instance._resizeFailureCount);
            return FailureResult;
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
            if (instance is not null)
                instance.ReportFailure(
                    "Present unmanaged boundary",
                    exception,
                    ref instance._presentFailureCount);
            return FailureResult;
        }
    }
}
