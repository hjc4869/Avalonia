const assert = require("node:assert/strict");
const path = require("node:path");
const { test } = require("node:test");
const vm = require("node:vm");
const { buildSync } = require("esbuild");

const source = buildSync({
    entryPoints: [path.join(__dirname, "../modules/avalonia/rendering/webGlRenderTarget.ts")],
    bundle: true,
    write: false,
    platform: "node",
    format: "cjs"
}).outputFiles[0].text;

function createTarget(options = {}) {
    const calls = [];
    let colorSpace = "srgb";
    let redBits = 8;
    let error = 0;
    const context = {
        RGBA8: 0x8058,
        RGBA16F: 0x881a,
        RED_BITS: 0x0d52,
        NO_ERROR: 0,
        getExtension: () => options.floatExtension === false ? null : {},
        getError: () => { const result = error; error = 0; return result; },
        getParameter: name => name === context.RED_BITS ? redBits : 0,
        get drawingBufferColorSpace() { return colorSpace; },
        set drawingBufferColorSpace(value) {
            if (value === "srgb-linear" && options.linear === false)
                throw new TypeError("Unsupported color space");
            colorSpace = value;
        }
    };
    const setToneMapping = ({ mode }) => {
        calls.push(["toneMapping", mode]);
        if (options.rejectToneMapping && mode === "extended")
            throw new Error("Tone mapping unavailable");
        return { mode };
    };
    const canvas = {
        width: 300,
        height: 150,
        getContext: () => context
    };
    if (options.storage !== false) {
        context.drawingBufferStorage = (format, width, height) => {
            calls.push(["storage", format, width, height]);
            if (options.rejectStorage && format === context.RGBA16F) {
                error = 0x0502;
                return;
            }
            redBits = format === context.RGBA16F && options.ignoreStorage !== true ? 16 : 8;
        };
    }
    if (options.toneMapping === "modern")
        context.drawingBufferToneMapping = setToneMapping;
    else if (options.toneMapping !== false)
        canvas.configureHighDynamicRange = setToneMapping;

    const sandbox = {
        module: { exports: {} },
        console: { warn: () => calls.push(["warning"]) },
        Module: {
            GL: {
                registerContext: () => 1,
                makeContextCurrent: () => true
            }
        }
    };
    vm.runInNewContext(source, sandbox);
    const target = new sandbox.module.exports.WebGlRenderTarget(canvas, options.mode ?? 3, options.preferHdr ?? true);
    return { target, context, calls };
}

test("HDR is opt-in and does not touch an ordinary SDR context", () => {
    const { target, context, calls } = createTarget({ preferHdr: false });
    assert.equal(target.isHdr, false);
    assert.equal(context.drawingBufferColorSpace, "srgb");
    assert.deepEqual(calls, []);
});

test("WebGL1 stays SDR even when HDR is requested", () => {
    const { target, calls } = createTarget({ mode: 2 });
    assert.equal(target.isHdr, false);
    assert.deepEqual(calls, []);
});

for (const toneMapping of ["legacy", "modern"]) {
    test(`negotiates float16 linear sRGB with the ${toneMapping} tone-mapping API`, () => {
        const { target, context, calls } = createTarget({ toneMapping });
        assert.equal(target.isHdr, true);
        assert.equal(context.drawingBufferColorSpace, "srgb-linear");
        assert.equal(context.getParameter(context.RED_BITS), 16);
        assert.deepEqual(calls, [["storage", context.RGBA16F, 300, 150], ["toneMapping", "extended"]]);
    });
}

for (const capability of ["storage", "toneMapping", "floatExtension"]) {
    test(`stays SDR without ${capability}`, () => {
        const { target, context, calls } = createTarget({ [capability]: false });
        assert.equal(target.isHdr, false);
        assert.equal(context.drawingBufferColorSpace, "srgb");
        assert.deepEqual(calls, []);
    });
}

for (const failure of [{ linear: false }, { rejectStorage: true }, { ignoreStorage: true }, { rejectToneMapping: true }]) {
    test(`restores SDR after failed HDR negotiation: ${JSON.stringify(failure)}`, () => {
        const { target, context, calls } = createTarget(failure);
        assert.equal(target.isHdr, false);
        assert.equal(context.drawingBufferColorSpace, "srgb");
        assert.equal(context.getParameter(context.RED_BITS), 8);
        assert.deepEqual(calls.slice(-3), [["storage", context.RGBA8, 300, 150], ["toneMapping", "standard"], ["warning"]]);
    });
}