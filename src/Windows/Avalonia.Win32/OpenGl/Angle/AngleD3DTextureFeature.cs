using System;
using System.Runtime.InteropServices;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Egl;
using Avalonia.OpenGL.Surfaces;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;
using Avalonia.Win32.DirectX;

namespace Avalonia.Win32.OpenGl.Angle;

internal class AngleD3DTextureFeature  : IGlPlatformSurfaceRenderTargetFactory
{
    public bool CanRenderToSurface(IGlContext context, IPlatformRenderSurface surface) =>
        context is EglContext
        {
            Display: AngleWin32EglDisplay { PlatformApi: AngleOptions.PlatformApi.DirectX11 }
        } && surface is IDirect3D11TexturePlatformSurface2;

    private class RenderTargetWrapper : EglPlatformSurfaceRenderTargetBase
    {
        private readonly AngleWin32EglDisplay _angle;
        private readonly IDirect3D11TextureRenderTarget2 _target;

        // A wrapper around a texture that keeps its identity, reused across frames.
        private EglSurface? _retainedSurface;
        private IntPtr _retainedTexture;
        private PixelSize _retainedSize;

        public RenderTargetWrapper(EglContext context,
            AngleWin32EglDisplay angle,
            IDirect3D11TextureRenderTarget2 target) : base(context)
        {
            _angle = angle;
            _target = target;
            if (_target.SupportsPartialPresentation)
                _target.TextureInvalidating = ReleaseRetainedSurface;
        }

        // The output is drawn from the retained layer: Skia's framebuffer snapshots are undefined for the
        // floating point formats used for HDR, and partial presentation only copies the layer's dirty rects.
        protected override RenderTargetProperties Properties => new()
        {
            SupportsPartialPresentation = _target.SupportsPartialPresentation
        };

        private void ReleaseRetainedSurface()
        {
            if (_retainedSurface is null)
                return;
            try
            {
                using (Context.EnsureCurrent())
                    _retainedSurface.Dispose();
            }
            catch (PlatformGraphicsContextLostException)
            {
                // Destroying the surface does not need the lost context to be current.
                _retainedSurface.Dispose();
            }

            _retainedSurface = null;
            _retainedTexture = IntPtr.Zero;
        }

        public override IGlPlatformSurfaceRenderingSession BeginDrawCore(IRenderTarget.RenderTargetSceneInfo sceneInfo)
        {
            // TODO: use expectedPixelSize
            var success = false;
            var contextLock = Context.EnsureCurrent();
            IDirect3D11TextureRenderTargetRenderSession? session = null;
            EglSurface? surface = null;
            var retained = false;
            try
            {
                try
                {
                    session = _target.BeginDraw(sceneInfo);
                }
                catch (RenderTargetCorruptedException e)
                {
                    if (e.InnerException is COMException com
                        && ((DXGI_ERROR)com.HResult).IsDeviceLostError()) 
                        Context.NotifyContextLost();

                    throw;
                }

                if (_target.SupportsPartialPresentation)
                {
                    if (_retainedSurface is null || _retainedTexture != session.D3D11Texture2D
                                                 || _retainedSize != session.Size)
                    {
                        _retainedSurface?.Dispose();
                        _retainedSurface = null;
                        _retainedSurface = _angle.WrapDirect3D11Texture(session.D3D11Texture2D, 0, 0,
                            session.Size.Width, session.Size.Height);
                        _retainedTexture = session.D3D11Texture2D;
                        _retainedSize = session.Size;
                    }

                    surface = _retainedSurface;
                    retained = true;
                }
                else
                {
                    surface = _angle.WrapDirect3D11Texture(session.D3D11Texture2D, session.Offset.X, session.Offset.Y,
                        session.Size.Width, session.Size.Height);
                }

                var rv = BeginDraw(surface, session.Size, session.Scaling, () =>
                {
                    using(contextLock)
                    using (session)
                    using (retained ? null : surface)
                    {
                    }
                }, true, null, session.PartialPresentation);
                success = true;
                return rv;
            }
            finally
            {
                if (!success)
                {
                    using(contextLock)
                    using (session)
                    using (retained ? null : surface)
                    {
                    }
                }
            }
        }

        public override void Dispose()
        {
            ReleaseRetainedSurface();
            _target.Dispose();
            base.Dispose();
        }

        public override PlatformSurfaceColorFormat ColorFormat => _target.ColorFormat;

        protected override PlatformSurfaceColorVolume? PreferredColorVolume =>
            _target.PreferredColorVolume;

        public override PlatformRenderTargetState State =>
            base.IsCorrupted ? PlatformRenderTargetState.Corrupted : _target.State;
    }
    
    public IGlPlatformSurfaceRenderTarget CreateRenderTarget(IGlContext context, IPlatformRenderSurface surface)
    {
        var ctx = (EglContext)context;
        var angle = (AngleWin32EglDisplay)ctx.Display;
        var textureSurface = (IDirect3D11TexturePlatformSurface2)surface;
        try
        {
            var target = textureSurface.CreateRenderTarget(context, angle.GetDirect3DDevice());
            return new RenderTargetWrapper(ctx, angle, target);
        }
        catch (COMException com)
        {
            if (((DXGI_ERROR)com.HResult).IsDeviceLostError())
                ctx.NotifyContextLost();
            throw;
        }
    }
}
