import { BrowserRenderingMode } from "./renderingMode";
import { WebRenderTarget } from "./webRenderTarget";
interface EmscriptenGlContext {
    handle: number;
}

interface EmscriptenGL {
    registerContext: (ctx: WebGLRenderingContext, attrs: WebGLContextAttributes) => number;
    currentContext?: EmscriptenGlContext;
    makeContextCurrent: (handle: number) => boolean;
}

function getGL(): EmscriptenGL {
    const self = globalThis as any;
    const module = self.Module ?? self.getDotnetRuntime(0)?.Module;
    return (module?.GL ?? self.AvaloniaGL ?? self.SkiaSharpGL) as EmscriptenGL;
}

export class WebGlRenderTarget extends WebRenderTarget {
    public contextHandle?: number;
    public attrs: WebGLContextAttributes;
    public fboId?: number;
    public stencil?: number;
    public sample?: number;
    public depth?: number;
    public context: WebGLRenderingContext;
    private static _gl: EmscriptenGL | null = null;

    constructor(public canvas: HTMLCanvasElement | OffscreenCanvas, mode: BrowserRenderingMode) {
        // Skia only understands WebGL context wrapped in Emscripten.
        if (WebGlRenderTarget._gl == null) { WebGlRenderTarget._gl = getGL(); }
        if (!WebGlRenderTarget._gl) {
            throw new Error("Module.GL object wasn't initialized, WebGL can't be used.");
        }

        const attrs: WebGLContextAttributes | any =
            {
                alpha: true,
                depth: true,
                stencil: true,
                antialias: false,
                premultipliedAlpha: true,
                preserveDrawingBuffer: false,
                // only supported on older browsers, which is perfect as we want to fallback to 2d there.
                failIfMajorPerformanceCaveat: true,
                // attrs used by Emscripten:
                majorVersion: mode === BrowserRenderingMode.WebGL1 ? 1 : 2,
                minorVersion: 0,
                enableExtensionsByDefault: 1,
                explicitSwapControl: 0
            };

        const context = (mode === BrowserRenderingMode.WebGL1
            ? canvas.getContext("webgl", attrs)
            : canvas.getContext("webgl2", attrs)) as WebGLRenderingContext;
        if (!context) {
            throw new Error("HTMLCanvasElement.getContext returned null.");
        }

        const handle = WebGlRenderTarget._gl.registerContext(context, attrs);
        (context as any).gl_handle = handle;
        super(canvas, "webgl");

        this.contextHandle = handle;
        this.context = context;
        this.fboId = context.getParameter(context.FRAMEBUFFER_BINDING)?.id ?? 0;
        this.stencil = context.getParameter(context.STENCIL_BITS);
        this.sample = context.getParameter(context.SAMPLES);
        this.depth = context.getParameter(context.DEPTH_BITS);
        this.attrs = attrs;
    }

    /**
     * Requests a drawing buffer color space and returns the one that is actually in effect.
     * Setting an unsupported value throws, and some browsers silently keep the old value, so the
     * result always has to be read back rather than assumed.
     */
    public static setColorSpace(target: WebGlRenderTarget, colorSpace: string): string {
        const context = target.context as any;
        if (typeof context.drawingBufferColorSpace !== "string") {
            return "srgb";
        }

        try {
            context.drawingBufferColorSpace = colorSpace;
        } catch (e) {
            // Unsupported enum value: the property keeps its previous value.
        }

        return context.drawingBufferColorSpace;
    }

    /**
     * Tries to switch the drawing buffer to 16 bit float, which avoids banding when a wide gamut
     * color space is spread over only 8 bits per channel. Returns whether it took effect.
     *
     * RGBA16F is only a legal drawingBufferStorage format once a float color buffer extension has
     * been enabled on the context, otherwise it raises INVALID_ENUM. The chosen format persists
     * across canvas resizes, so this only has to be done once.
     */
    public static tryUseFloat16(target: WebGlRenderTarget): boolean {
        const context = target.context as any;
        if (typeof context.drawingBufferStorage !== "function" || context.RGBA16F === undefined) {
            return false;
        }

        if (!context.getExtension("EXT_color_buffer_half_float") && !context.getExtension("EXT_color_buffer_float")) {
            return false;
        }

        context.drawingBufferStorage(context.RGBA16F, target.canvas.width, target.canvas.height);
        if (context.getError() !== 0) {
            return false;
        }

        return context.drawingBufferFormat === context.RGBA16F;
    }

    public static getCurrentContext(): number {
        return WebGlRenderTarget._gl?.currentContext?.handle ?? 0;
    }

    public static makeContextCurrent(handle: number): boolean {
        if (WebGlRenderTarget._gl == null) { return false; }
        const ret = WebGlRenderTarget._gl.makeContextCurrent(handle);
        return handle === 0 || ret;
    }
}
