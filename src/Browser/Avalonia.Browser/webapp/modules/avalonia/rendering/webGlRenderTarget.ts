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

interface HdrToneMapping {
    mode: "standard" | "extended";
}

interface HdrCanvas {
    configureHighDynamicRange?: (options: HdrToneMapping) => void;
}

interface HdrWebGlContext {
    drawingBufferColorSpace: string;
    drawingBufferStorage?: (format: number, width: number, height: number) => void;
    drawingBufferToneMapping?: (options: HdrToneMapping) => HdrToneMapping;
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
    public readonly isHdr: boolean;
    private static _gl: EmscriptenGL | null = null;

    constructor(public canvas: HTMLCanvasElement | OffscreenCanvas, mode: BrowserRenderingMode, enableHdr = false) {
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

        const isHdr = enableHdr && mode !== BrowserRenderingMode.WebGL1 &&
            WebGlRenderTarget.tryEnableHdr(canvas, context as WebGL2RenderingContext);
        const handle = WebGlRenderTarget._gl.registerContext(context, attrs);
        (context as any).gl_handle = handle;
        super(canvas, "webgl");

        this.isHdr = isHdr;
        this.contextHandle = handle;
        this.fboId = context.getParameter(context.FRAMEBUFFER_BINDING)?.id ?? 0;
        this.stencil = context.getParameter(context.STENCIL_BITS);
        this.sample = context.getParameter(context.SAMPLES);
        this.depth = context.getParameter(context.DEPTH_BITS);
        this.attrs = attrs;
    }

    private static tryEnableHdr(canvas: HTMLCanvasElement | OffscreenCanvas, context: WebGL2RenderingContext): boolean {
        const hdrCanvas = canvas as HdrCanvas;
        const hdrContext = context as unknown as HdrWebGlContext;
        if (typeof hdrContext.drawingBufferStorage !== "function" ||
            (typeof hdrContext.drawingBufferToneMapping !== "function" &&
                typeof hdrCanvas.configureHighDynamicRange !== "function") ||
            !context.getExtension("EXT_color_buffer_float")) {
            return false;
        }

        const setToneMapping = (mode: "standard" | "extended") => {
            if (hdrContext.drawingBufferToneMapping) {
                hdrContext.drawingBufferToneMapping({ mode });
            } else {
                hdrCanvas.configureHighDynamicRange!({ mode });
            }
        };

        try {
            hdrContext.drawingBufferColorSpace = "srgb-linear";
            if (hdrContext.drawingBufferColorSpace !== "srgb-linear") {
                throw new Error("Linear sRGB canvas output is not supported.");
            }
            hdrContext.drawingBufferStorage(context.RGBA16F, canvas.width, canvas.height);
            setToneMapping("extended");
            if (context.getError() !== context.NO_ERROR || context.getParameter(context.RED_BITS) !== 16) {
                throw new Error("The browser did not allocate a float16 drawing buffer.");
            }
            return true;
        } catch (error) {
            hdrContext.drawingBufferColorSpace = "srgb";
            hdrContext.drawingBufferStorage(context.RGBA8, canvas.width, canvas.height);
            setToneMapping("standard");
            console.warn("Avalonia: HDR initialization failed; using SDR.", error);
            return false;
        }
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
