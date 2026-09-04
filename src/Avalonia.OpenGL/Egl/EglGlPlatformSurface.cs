using System;
using Avalonia.Metadata;
using Avalonia.OpenGL.Surfaces;
using Avalonia.Platform;

namespace Avalonia.OpenGL.Egl
{
    public class EglGlPlatformSurface : EglGlPlatformSurfaceBase
    {
        public interface IEglWindowGlPlatformSurfaceInfo
        {
            IntPtr Handle { get; }
            PixelSize Size { get; }
            double Scaling { get; }
        }

        /// <summary>
        /// Optionally supplies the color volume preferred for a window surface.
        /// </summary>
        [PrivateApi]
        public interface IEglWindowGlPlatformSurfaceInfoWithColorVolume : IEglWindowGlPlatformSurfaceInfo
        {
            /// <summary>
            /// Gets the current preferred color volume, or null when it can't be reported.
            /// </summary>
            PlatformSurfaceColorVolume? PreferredColorVolume { get; }
        }
        
        [PrivateApi]
        public interface IEglWindowGlPlatformSurfaceInfoWithWaitPolicy : IEglWindowGlPlatformSurfaceInfo
        {
            public bool SkipWaits { get; }
        }
        
        private readonly IEglWindowGlPlatformSurfaceInfo _info;
        
        public EglGlPlatformSurface(IEglWindowGlPlatformSurfaceInfo info)
        {
            _info = info;
        }

        public override IGlPlatformSurfaceRenderTarget CreateGlRenderTarget(IGlContext context)
        {
            if (_info.Handle == IntPtr.Zero)
                throw new RenderTargetNotReadyException();

            var eglContext = (EglContext)context;
            
            var glSurface = eglContext.Display.CreateWindowSurface(_info.Handle);
            return new RenderTarget(glSurface, eglContext, _info);
        }

        private class RenderTarget : EglPlatformSurfaceRenderTargetBase
        {
            private EglSurface? _glSurface;
            private readonly IEglWindowGlPlatformSurfaceInfo _info;
            private PixelSize _currentSize;
            private IntPtr _handle;

            public RenderTarget(EglSurface glSurface, EglContext context, IEglWindowGlPlatformSurfaceInfo info) : base(context)
            {
                _glSurface = glSurface;
                _info = info;
                _currentSize = info.Size;
                _handle = _info.Handle;
                SkipWaits = info is IEglWindowGlPlatformSurfaceInfoWithWaitPolicy { SkipWaits: true };
            }

            protected override bool SkipWaits { get; }

            protected override PlatformSurfaceColorVolume? PreferredColorVolume =>
                (_info as IEglWindowGlPlatformSurfaceInfoWithColorVolume)?.PreferredColorVolume;

            public override PlatformRenderTargetState State => _info.Handle == IntPtr.Zero
                ? PlatformRenderTargetState.NotReadyTryLater
                : base.State;

            public override void Dispose() => _glSurface?.Dispose();

            public override IGlPlatformSurfaceRenderingSession BeginDrawCore(IRenderTarget.RenderTargetSceneInfo sceneInfo)
            {
                // TODO: use expectedPixelSize
                var handle = _info.Handle;
                if (handle == IntPtr.Zero)
                    throw new RenderTargetNotReadyException();

                var size = _info.Size;
                if (size != _currentSize
                    || _handle != handle
                    || _glSurface == null)
                {
                    _glSurface?.Dispose();
                    _glSurface = null;
                    _glSurface = Context.Display.CreateWindowSurface(handle);
                    _currentSize = size;
                    _handle = handle;
                }
                return base.BeginDraw(_glSurface, size, _info.Scaling);
            }
        }
    }
}

