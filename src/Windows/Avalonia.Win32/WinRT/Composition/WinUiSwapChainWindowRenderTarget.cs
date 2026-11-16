using System;
using System.Runtime.InteropServices;
using Avalonia.OpenGL.Egl;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Win32.DirectX;
using Avalonia.Win32.Interop;
using MicroCom.Runtime;

namespace Avalonia.Win32.WinRT.Composition
{
    /// <summary>
    /// Presents the window through a flip model swap chain hosted in the composition tree, so
    /// finished frames are queued by DXGI instead of waiting for a composition commit. The back
    /// buffers keep their contents and identity, so one wrapper surface serves every frame.
    /// A frame is started once the system compositor has taken an earlier one, which keeps the
    /// frames queued ahead of the display within the configured latency.
    /// </summary>
    internal unsafe class WinUiSwapChainWindowRenderTarget : IDirect3D11TextureRenderTarget, IDirect3D11TextureRenderTarget2
    {
        private const uint DXGI_USAGE_RENDER_TARGET_OUTPUT = 0x00000020U;
        private const uint DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT = 0x00000040U;
        // The compositor takes a queued frame at the start of each of its own frames, so a slot that
        // stays taken this long means it has stopped consuming them.
        private const uint FrameSlotTimeoutMs = 100;
        private static readonly Guid IID_ID3D11Texture2D = Guid.Parse("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        private static readonly Guid IID_IDXGISwapChain2 = Guid.Parse("a8be2ac4-199f-4946-b331-79599fb98de7");
        // Inherits IDXGISwapChain2 and is the one some Windows builds answer for.
        private static readonly Guid IID_IDXGISwapChain3 = Guid.Parse("94d99bdb-f1f8-4ab0-b236-7da0170edab1");

        private readonly IPlatformGraphicsContext _context;
        private readonly WinUiCompositedWindow _window;
        private readonly IUnknown _d3dDevice;
        private readonly ICompositorInterop _interop;
        private readonly IDXGIFactory2 _factory;
        private readonly uint _maxFrameLatency;
        private IDXGISwapChain1? _swapChain;
        private IntPtr _swapChain2;
        private IntPtr _frameLatencyHandle;
        private uint _swapChainFlags;
        private ICompositionSurface? _surface;
        private IUnknown? _backBuffer;
        private PixelSize _size;
        private bool _transparent;
        private bool _lost;
        // A slot of the frame queue is held from the wait that took it until the frame is presented.
        private bool _frameSlotTaken;
        private bool _frameSlotTimedOut;
        // Frames presented since the buffers were created; both buffers hold a frame from the third on.
        private int _presentedFrames;

        private const int BufferCount = 2;

        [DllImport("kernel32", ExactSpelling = true)]
        private static extern uint WaitForSingleObjectEx(IntPtr handle, uint milliseconds, int alertable);

        public PlatformSurfaceColorFormat ColorFormat { get; }

        public bool RetainsFrameContents => true;

        public Action? TextureInvalidating { get; set; }

        public PlatformSurfaceColorVolume? PreferredColorVolume =>
            (_window.WindowInfo as EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfoWithColorVolume)
            ?.PreferredColorVolume;

        public WinUiSwapChainWindowRenderTarget(IPlatformGraphicsContext context,
            WinUiCompositedWindow window, IntPtr device, ICompositor compositor, int maxFrameLatency)
        {
            _context = context;
            _window = window;
            // DXGI accepts 1 to 16.
            _maxFrameLatency = (uint)Math.Clamp(maxFrameLatency, 1, 16);
            ColorFormat = Win32SurfaceColorFormat.Negotiated(context);
            _d3dDevice = MicroComRuntime.CreateProxyFor<IUnknown>(device, false).CloneReference();
            _interop = compositor.QueryInterface<ICompositorInterop>();

            using var dxgiDevice = _d3dDevice.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.Adapter;
            var factoryGuid = MicroComRuntime.GetGuidFor(typeof(IDXGIFactory2));
            _factory = MicroComRuntime.CreateProxyFor<IDXGIFactory2>(adapter.GetParent(&factoryGuid), true);
        }

        public void Dispose()
        {
            _backBuffer?.Dispose();
            _surface?.Dispose();
            ReleaseFrameLatency();
            _swapChain?.Dispose();
            _factory.Dispose();
            _interop.Dispose();
            _d3dDevice.Dispose();
        }

        public PlatformRenderTargetState State =>
            _context.IsLost || _lost ? PlatformRenderTargetState.Corrupted : PlatformRenderTargetState.Ready;

        private DXGI_FORMAT Format => ColorFormat.Encoding == PlatformPixelEncoding.RgbaF16
            ? DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT
            : DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;

        private void CreateSwapChain(PixelSize size, bool transparent)
        {
            TextureInvalidating?.Invoke();
            _presentedFrames = 0;
            _backBuffer?.Dispose();
            _backBuffer = null;
            _surface?.Dispose();
            _surface = null;
            ReleaseFrameLatency();
            _swapChain?.Dispose();
            _swapChain = null;

            var desc = new DXGI_SWAP_CHAIN_DESC1
            {
                Width = (uint)size.Width,
                Height = (uint)size.Height,
                Format = Format,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT,
                BufferCount = BufferCount,
                Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH,
                SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL,
                AlphaMode = transparent
                    ? DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_PREMULTIPLIED
                    : DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE,
                Flags = DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT
            };

            _swapChain = _factory.CreateSwapChainForComposition(_d3dDevice, &desc, null);
            if (!TryLimitFrameLatency(_swapChain))
            {
                // A waitable swap chain queues without bound unless its slots are waited for.
                ReleaseFrameLatency();
                _swapChain.Dispose();
                _swapChain = null;
                desc.Flags = 0;
                _swapChain = _factory.CreateSwapChainForComposition(_d3dDevice, &desc, null);
            }

            _swapChainFlags = desc.Flags;
            _surface = _interop.CreateCompositionSurfaceForSwapChain(_swapChain);
            _size = size;
            _transparent = transparent;
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

        IDirect3D11TextureRenderTargetRenderSession IDirect3D11TextureRenderTarget.BeginDraw()
        {
            var fallbackSceneInfo = new IRenderTarget.RenderTargetSceneInfo(_window.WindowInfo.Size,
                _window.WindowInfo.Scaling, CompositionTransparencyLevel.None);
            return BeginDraw(fallbackSceneInfo);
        }

        public IDirect3D11TextureRenderTargetRenderSession BeginDraw(IRenderTarget.RenderTargetSceneInfo sceneInfo)
        {
            if (State.IsCorrupted)
                throw new RenderTargetCorruptedException();

            var transaction = _window.BeginTransaction();
            try
            {
                var transparent = sceneInfo.TransparencyLevel != CompositionTransparencyLevel.None;
                var size = new PixelSize(Math.Max(1, sceneInfo.Size.Width), Math.Max(1, sceneInfo.Size.Height));
                try
                {
                    if (_swapChain is null || _transparent != transparent)
                    {
                        CreateSwapChain(size, transparent);
                    }
                    else if (_size != size)
                    {
                        TextureInvalidating?.Invoke();
                        _presentedFrames = 0;
                        _backBuffer?.Dispose();
                        _backBuffer = null;
                        _swapChain.ResizeBuffers(BufferCount, (uint)size.Width, (uint)size.Height, Format,
                            _swapChainFlags);
                        _size = size;
                    }

                    if (_backBuffer is null)
                    {
                        var iid = IID_ID3D11Texture2D;
                        _backBuffer = MicroComRuntime.CreateProxyFor<IUnknown>(_swapChain!.GetBuffer(0, &iid), true);
                    }
                }
                catch (Exception e)
                {
                    _lost = true;
                    throw new RenderTargetCorruptedException(e);
                }

                WaitForFrameSlot();

                _window.ResizeIfNeeded(size);
                _window.ApplyEffects(sceneInfo.TransparencyLevel,
                    (sceneInfo.PlatformSpecificSceneInfo as Win32TopLevelSceneInfo)?.ThemeVariant ??
                    PlatformThemeVariant.Light);
                _window.SetSurface(_surface!);

                var session = new Session(this, _swapChain!, _backBuffer, transaction, size, sceneInfo.Scaling,
                    _presentedFrames >= BufferCount ? BufferCount : 0);
                transaction = null;
                return session;
            }
            finally
            {
                transaction?.Dispose();
            }
        }

        private class Session : IDirect3D11TextureRenderTargetRenderSession
        {
            private readonly WinUiSwapChainWindowRenderTarget _target;
            private readonly IDXGISwapChain1 _swapChain;
            private readonly IUnknown _texture;
            private readonly IDisposable _transaction;

            public Session(WinUiSwapChainWindowRenderTarget target, IDXGISwapChain1 swapChain, IUnknown texture,
                IDisposable transaction, PixelSize size, double scaling, int bufferAge)
            {
                _target = target;
                _swapChain = swapChain;
                _texture = texture;
                _transaction = transaction;
                Size = size;
                Scaling = scaling;
                BufferAge = bufferAge;
            }

            public int BufferAge { get; }

            public void Dispose()
            {
                try
                {
                    var presentResult = _swapChain.Present(1, 0);
                    _target._frameSlotTaken = false;
                    // A failed present leaves the buffers in an unknown state; the target is recreated.
                    if (presentResult < 0)
                        _target._lost = true;
                    else if (_target._presentedFrames < BufferCount)
                        _target._presentedFrames++;
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
        }
    }
}
