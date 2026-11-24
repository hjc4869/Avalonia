using System;
using System.Runtime.InteropServices;
using Avalonia.OpenGL.Egl;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Win32.Interop;
using MicroCom.Runtime;

namespace Avalonia.Win32.DirectX;

/// <summary>
/// A composed window that can display a DXGI swapchain as its content.
/// </summary>
internal interface ISwapchainVisualHost
{
    EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo WindowInfo { get; }
    IDisposable BeginTransaction();

    /// <summary>
    /// Attaches the swapchain as the content of the window's visual.
    /// Called with the transaction held, only when the swapchain is (re)created.
    /// </summary>
    void SetSwapchainContent(IUnknown swapchain);

    void ResizeIfNeeded(PixelSize size);
    void ApplyEffects(CompositionTransparencyLevel transparencyLevel, PlatformThemeVariant themeVariant);
}

/// <summary>
/// A platform render surface that renders into a composition swapchain displayed by the given host.
/// </summary>
internal sealed class CompositionSwapchainSurface : IDirect3D11TexturePlatformSurface2
{
    private readonly ISwapchainVisualHost _host;

    public CompositionSwapchainSurface(ISwapchainVisualHost host)
    {
        _host = host;
    }

    public IDirect3D11TextureRenderTarget2 CreateRenderTarget(IPlatformGraphicsContext graphicsContext, IntPtr d3dDevice) =>
        new CompositionSwapchainRenderTarget(graphicsContext, d3dDevice, _host,
            AvaloniaLocator.Current.GetService<Win32PlatformOptions>()?.CompositionSwapChainFrameLatency ?? 2);
}

/// <summary>
/// Renders into a DXGI flip-model swapchain created for composition and attached
/// as visual content via <see cref="ISwapchainVisualHost"/>. Unlike the composition
/// drawing surface targets, this allows DWM to use optimized presentation modes
/// (independent flip) for fullscreen windows.
/// </summary>
/// <remarks>
/// Frames are presented with a sync interval of one, so DXGI queues finished frames for the display instead of
/// replacing them. A frame is started once the system compositor has taken an earlier one, which keeps the frames
/// queued ahead of the display within the configured latency. The back buffers are sequential flip buffers that
/// keep their contents and identity, so sessions support partial presentation and one texture wrapper serves
/// every frame. FP16 buffers are used when ANGLE negotiated an extended range color format.
/// </remarks>
internal unsafe class CompositionSwapchainRenderTarget : IDirect3D11TextureRenderTarget, IDirect3D11TextureRenderTarget2
{
    private const int BufferCount = 2;
    private const uint DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT = 0x00000040U;
    // The compositor takes a queued frame at the start of each of its own frames, so a slot that
    // stays taken this long means it has stopped consuming them.
    private const uint FrameSlotTimeoutMs = 100;
    private static readonly Guid IID_ID3D11Texture2D = Guid.Parse("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid IID_IDXGISwapChain2 = Guid.Parse("a8be2ac4-199f-4946-b331-79599fb98de7");
    // Inherits IDXGISwapChain2 and is the one some Windows builds answer for.
    private static readonly Guid IID_IDXGISwapChain3 = Guid.Parse("94d99bdb-f1f8-4ab0-b236-7da0170edab1");

    private readonly IPlatformGraphicsContext _context;
    private readonly ISwapchainVisualHost _host;
    private readonly IUnknown _d3dDevice;
    private readonly IDXGIDevice _dxgiDevice;
    private readonly IDXGIFactory2 _dxgiFactory;
    private readonly uint _maxFrameLatency;
    private IDXGISwapChain1? _swapChain;
    private uint _swapChainFlags;
    private IntPtr _swapChain2;
    private IntPtr _frameLatencyHandle;
    // A slot of the frame queue is held from the wait that took it until the frame is presented.
    private bool _frameSlotTaken;
    private bool _frameSlotTimedOut;
    private IUnknown? _backBuffer;
    private PixelSize _size;
    private bool _isSwapchainTransparencyCapable;
    private bool _lost;
    // The contents of each back buffer, in the order DXGI hands them out. Sequential flip buffers rotate
    // on every present, so the current back buffer is the one after the last presented buffer.
    private readonly SwapchainBuffer?[] _buffers = new SwapchainBuffer?[BufferCount];
    private int _backBufferIndex;

    [DllImport("kernel32", ExactSpelling = true)]
    private static extern uint WaitForSingleObjectEx(IntPtr handle, uint milliseconds, int alertable);

    public CompositionSwapchainRenderTarget(IPlatformGraphicsContext context, IntPtr d3dDevice,
        ISwapchainVisualHost host, int maxFrameLatency)
    {
        _context = context;
        _host = host;
        // DXGI accepts 1 to 16.
        _maxFrameLatency = (uint)Math.Clamp(maxFrameLatency, 1, 16);
        ColorFormat = Win32SurfaceColorFormat.Negotiated(context);

        try
        {
            _d3dDevice = MicroComRuntime.CreateProxyFor<IUnknown>(d3dDevice, false).CloneReference();
            _dxgiDevice = _d3dDevice.QueryInterface<IDXGIDevice>();
            using var adapter = _dxgiDevice.Adapter;
            var factoryGuid = MicroComRuntime.GetGuidFor(typeof(IDXGIFactory2));
            _dxgiFactory = MicroComRuntime.CreateProxyFor<IDXGIFactory2>(adapter.GetParent(&factoryGuid), true);
        }
        catch
        {
            _dxgiFactory?.Dispose();
            _dxgiDevice?.Dispose();
            _d3dDevice?.Dispose();
            throw;
        }
    }

    public PlatformSurfaceColorFormat ColorFormat { get; }

    public PlatformSurfaceColorVolume? PreferredColorVolume =>
        (_host.WindowInfo as EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfoWithColorVolume)
        ?.PreferredColorVolume;

    public bool SupportsPartialPresentation => true;

    public Action? TextureInvalidating { get; set; }

    public void Dispose()
    {
        InvalidateBuffers();
        _backBuffer?.Dispose();
        _backBuffer = null;
        ReleaseFrameLatency();
        _swapChain?.Dispose();
        _dxgiFactory.Dispose();
        _dxgiDevice.Dispose();
        _d3dDevice.Dispose();
    }

    public PlatformRenderTargetState State =>
        _context.IsLost || _lost ? PlatformRenderTargetState.Corrupted : PlatformRenderTargetState.Ready;

    private DXGI_FORMAT Format => ColorFormat.Encoding == PlatformPixelEncoding.RgbaF16
        ? DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT
        : DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;

    // Drops every reference to the back buffers and forgets their contents.
    private void ReleaseBackBuffer()
    {
        TextureInvalidating?.Invoke();
        InvalidateBuffers();
        _backBuffer?.Dispose();
        _backBuffer = null;
    }

    private void InvalidateBuffers()
    {
        for (var i = 0; i < _buffers.Length; i++)
        {
            _buffers[i]?.Invalidate();
            _buffers[i] = null;
        }

        _backBufferIndex = 0;
    }

    private void CreateSwapchain(PixelSize size, bool isTransparency)
    {
        var desc = new DXGI_SWAP_CHAIN_DESC1
        {
            Width = (uint)size.Width,
            Height = (uint)size.Height,
            Format = Format,
            SampleDesc = { Count = 1, Quality = 0 },
            BufferUsage = DxgiRenderTarget.DXGI_USAGE_RENDER_TARGET_OUTPUT,
            BufferCount = BufferCount,
            Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH,
            // Sequential buffers keep their contents after presenting, which partial presentation relies on.
            SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL,
            // Premultiplied alpha increases the DWM composition cost and prevents optimized
            // presentation modes, so it's only used when the window is actually transparent,
            // see https://github.com/AvaloniaUI/Avalonia/issues/20643
            AlphaMode = isTransparency ?
                DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_PREMULTIPLIED :
                DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE,
            Flags = DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT
        };

        var swapChain = _dxgiFactory.CreateSwapChainForComposition(_dxgiDevice, &desc, null);
        if (!TryLimitFrameLatency(swapChain))
        {
            // A waitable swapchain queues without bound unless its slots are waited for.
            ReleaseFrameLatency();
            swapChain.Dispose();
            desc.Flags = 0;
            swapChain = _dxgiFactory.CreateSwapChainForComposition(_dxgiDevice, &desc, null);
        }

        try
        {
            _host.SetSwapchainContent(swapChain);
        }
        catch
        {
            ReleaseFrameLatency();
            swapChain.Dispose();
            throw;
        }

        _swapChain = swapChain;
        _swapChainFlags = desc.Flags;
        _isSwapchainTransparencyCapable = isTransparency;
        _size = size;
    }

    private bool TryLimitFrameLatency(IDXGISwapChain1 swapChain)
    {
        var unknown = swapChain.GetNativeIntPtr();
        var iid = IID_IDXGISwapChain2;
        if (Marshal.QueryInterface(unknown, in iid, out _swapChain2) != 0)
        {
            iid = IID_IDXGISwapChain3;
            if (Marshal.QueryInterface(unknown, in iid, out _swapChain2) != 0)
            {
                _swapChain2 = IntPtr.Zero;
                return false;
            }
        }

        // IDXGISwapChain2 follows the 29 slots of IDXGISwapChain1: SetSourceSize, GetSourceSize,
        // SetMaximumFrameLatency, GetMaximumFrameLatency, GetFrameLatencyWaitableObject.
        var vtable = *(IntPtr**)_swapChain2;
        var setMaximumFrameLatency = (delegate* unmanaged[Stdcall]<IntPtr, uint, int>)vtable[31];
        if (setMaximumFrameLatency(_swapChain2, _maxFrameLatency) < 0)
            return false;

        var getFrameLatencyWaitableObject = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)vtable[33];
        _frameLatencyHandle = getFrameLatencyWaitableObject(_swapChain2);
        return _frameLatencyHandle != IntPtr.Zero;
    }

    private void ReleaseFrameLatency()
    {
        if (_frameLatencyHandle != IntPtr.Zero)
        {
            UnmanagedMethods.CloseHandle(_frameLatencyHandle);
            _frameLatencyHandle = IntPtr.Zero;
        }

        if (_swapChain2 != IntPtr.Zero)
        {
            Marshal.Release(_swapChain2);
            _swapChain2 = IntPtr.Zero;
        }

        _frameSlotTaken = false;
        _frameSlotTimedOut = false;
    }

    // Blocks until the compositor has taken an earlier frame out of a full queue.
    private void WaitForFrameSlot()
    {
        if (_frameLatencyHandle == IntPtr.Zero || _frameSlotTaken)
            return;

        // Once a wait has timed out, later frames only poll, so a compositor that is not
        // consuming frames does not hold back the render loop.
        var result = WaitForSingleObjectEx(_frameLatencyHandle, _frameSlotTimedOut ? 0 : FrameSlotTimeoutMs, 0);
        _frameSlotTaken = result == 0;
        _frameSlotTimedOut = !_frameSlotTaken;
    }

    // Callers of the original interface don't know about partial presentation: their frames are always
    // presented, and the buffer contents are not tracked.
    IDirect3D11TextureRenderTargetRenderSession IDirect3D11TextureRenderTarget.BeginDraw()
    {
        var fallbackSceneInfo = new IRenderTarget.RenderTargetSceneInfo(_host.WindowInfo.Size,
            _host.WindowInfo.Scaling, CompositionTransparencyLevel.None);
        return BeginDraw(fallbackSceneInfo, false);
    }

    public IDirect3D11TextureRenderTargetRenderSession BeginDraw(IRenderTarget.RenderTargetSceneInfo sceneInfo) =>
        BeginDraw(sceneInfo, true);

    private Session BeginDraw(IRenderTarget.RenderTargetSceneInfo sceneInfo, bool trackContents)
    {
        if (State.IsCorrupted)
            throw new RenderTargetCorruptedException();

        // Waiting for the queue outside of the transaction keeps the composition lock free meanwhile.
        WaitForFrameSlot();

        var transaction = _host.BeginTransaction();
        try
        {
            var isTransparency = sceneInfo.TransparencyLevel != CompositionTransparencyLevel.None;
            // Unlike hwnd ones, composition swapchains require explicit non-zero dimensions
            var size = new PixelSize(Math.Max(1, sceneInfo.Size.Width), Math.Max(1, sceneInfo.Size.Height));

            try
            {
                if (_swapChain is null || _isSwapchainTransparencyCapable != isTransparency)
                {
                    // The alpha mode can't be changed by ResizeBuffers, so the swapchain
                    // is recreated when the transparency level changes
                    ReleaseBackBuffer();
                    ReleaseFrameLatency();
                    _swapChain?.Dispose();
                    _swapChain = null;
                    CreateSwapchain(size, isTransparency);
                }
                else if (_size != size)
                {
                    // Every back buffer reference, including the texture wrapper, has to be released first
                    ReleaseBackBuffer();
                    _swapChain.ResizeBuffers(BufferCount, (uint)size.Width, (uint)size.Height, Format,
                        _swapChainFlags);
                    _size = size;
                }

                _host.ResizeIfNeeded(size);
                _host.ApplyEffects(sceneInfo.TransparencyLevel,
                    (sceneInfo.PlatformSpecificSceneInfo as Win32TopLevelSceneInfo)?.ThemeVariant ??
                    PlatformThemeVariant.Light);

                if (_backBuffer is null)
                {
                    // Direct3D 11 renames the resource behind buffer 0 on every present, so the
                    // same texture always refers to the current back buffer.
                    var iid = IID_ID3D11Texture2D;
                    _backBuffer = MicroComRuntime.CreateProxyFor<IUnknown>(_swapChain!.GetBuffer(0, &iid), true);
                }
            }
            catch (Exception e)
            {
                _lost = true;
                throw new RenderTargetCorruptedException(e);
            }

            var buffer = _buffers[_backBufferIndex] ??= new SwapchainBuffer();
            var session = new Session(this, _swapChain!, _backBuffer, buffer, trackContents, transaction, size,
                sceneInfo.Scaling);
            transaction = null;
            return session;
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    private void OnPresented(bool succeeded, bool trackContents)
    {
        _frameSlotTaken = false;
        if (!succeeded)
        {
            // A failed present leaves the buffers in an unknown state; the target is recreated.
            _lost = true;
            InvalidateBuffers();
        }
        else if (!trackContents)
            InvalidateBuffers();
        else
            _backBufferIndex = (_backBufferIndex + 1) % BufferCount;
    }

    private void OnDrawingFailed(SwapchainBuffer buffer)
    {
        // The frame is not presented, so the same back buffer is used next time, with unknown contents.
        buffer.Invalidate();
        if (_buffers[_backBufferIndex] == buffer)
            _buffers[_backBufferIndex] = null;
    }

    private sealed class SwapchainBuffer : IPartialPresentationBuffer
    {
        public bool IsValid { get; private set; } = true;
        public void Invalidate() => IsValid = false;
        public bool Equals(IPartialPresentationBuffer? other) => ReferenceEquals(this, other);
    }

    private class Session : IDirect3D11TextureRenderTargetRenderSession, IPartialPresentationFeature
    {
        private readonly CompositionSwapchainRenderTarget _target;
        private readonly IDXGISwapChain1 _swapChain;
        private readonly IUnknown _texture;
        private readonly SwapchainBuffer _buffer;
        private readonly bool _trackContents;
        private readonly IDisposable _transaction;
        private bool _succeeded;
        private bool _disposed;

        public Session(CompositionSwapchainRenderTarget target, IDXGISwapChain1 swapChain, IUnknown texture,
            SwapchainBuffer buffer, bool trackContents, IDisposable transaction, PixelSize size, double scaling)
        {
            _target = target;
            _swapChain = swapChain;
            _texture = texture;
            _buffer = buffer;
            _trackContents = trackContents;
            _transaction = transaction;
            Size = size;
            Scaling = scaling;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                if (_succeeded || !_trackContents)
                {
                    // Present goes before the transaction is committed, so a freshly attached
                    // swapchain always has a frame by the time it becomes visible
                    _target.OnPresented(_swapChain.Present(1, 0) >= 0, _trackContents);
                }
                else
                    _target.OnDrawingFailed(_buffer);
            }
            finally
            {
                _transaction.Dispose();
            }
        }

        public IntPtr D3D11Texture2D => _texture.GetNativeIntPtr();
        public PixelSize Size { get; }
        public PixelPoint Offset => default;
        public double Scaling { get; }

        public IPartialPresentationFeature? PartialPresentation => _trackContents ? this : null;

        IPartialPresentationBuffer? IPartialPresentationFeature.Buffer => _buffer;

        // The whole buffer is presented; the compositor only needs the buffer's contents tracked.
        void IPartialPresentationFeature.SetDirtyRects(ReadOnlySpan<PixelRect> frameDirtyRects,
            ReadOnlySpan<PixelRect> bufferDirtyRects)
        {
        }

        void IPartialPresentationFeature.Succeeded() => _succeeded = true;
    }
}
