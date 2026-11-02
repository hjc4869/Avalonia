const assert = require("node:assert/strict");
const path = require("node:path");
const { test } = require("node:test");
const vm = require("node:vm");
const { buildSync } = require("esbuild");

const source = buildSync({
    entryPoints: [path.join(__dirname, "../modules/avalonia/screens.ts")],
    bundle: true,
    write: false,
    platform: "node",
    format: "cjs"
}).outputFiles[0].text;

async function createHelper(state = "granted") {
    const calls = [];
    const screen = Object.assign(new EventTarget(), { hdrHeadroom: 2 });
    const details = Object.assign(new EventTarget(), { screens: [screen], currentScreen: screen });
    const permission = Object.assign(new EventTarget(), { state });
    let requests = 0;
    const window = {
        screen: new EventTarget(),
        navigator: { permissions: { query: async () => permission } },
        getScreenDetails: async () => { requests++; return details; },
        setTimeout: callback => queueMicrotask(callback)
    };
    const sandbox = {
        module: { exports: {} },
        getDotnetRuntime: () => ({
            getAssemblyExports: async () => ({
                Avalonia: { Browser: { Interop: { DomHelper: { ScreensChanged: value => calls.push(value) } } } }
            })
        })
    };
    vm.runInNewContext(source, sandbox);
    await Promise.resolve();
    await Promise.resolve();
    return { helper: sandbox.module.exports.ScreenHelper, window, details, screen, permission, calls, requests: () => requests };
}

test("reads logarithmic headroom without converting stops to a ratio", async () => {
    const { helper } = await createHelper();
    for (const value of [0, 1, 2, 1.5449177])
        assert.equal(helper.getHdrHeadroom({ hdrHeadroom: value }), value);
});

test("missing or invalid headroom is unknown, not SDR", async () => {
    const { helper } = await createHelper();
    for (const value of [undefined, null, "2", NaN, Infinity, -Infinity, -1])
        assert.ok(Number.isNaN(helper.getHdrHeadroom({ hdrHeadroom: value })));
});

test("permission grant reports initial headroom and runtime changes", async () => {
    const { helper, window, screen, calls } = await createHelper();
    await helper.checkPermissions(window);
    assert.deepEqual(calls, [2]);
    screen.hdrHeadroom = 1;
    screen.dispatchEvent(new Event("hdrheadroomchange"));
    assert.deepEqual(calls, [2, 1]);
});

test("switching screens reports the current display", async () => {
    const { helper, window, details, calls } = await createHelper();
    await helper.checkPermissions(window);
    details.currentScreen = Object.assign(new EventTarget(), { hdrHeadroom: 0 });
    details.dispatchEvent(new Event("currentscreenchange"));
    assert.deepEqual(calls, [2, 0]);
});

test("monitor replacement rebinds headroom listeners and removes old ones", async () => {
    const { helper, window, details, screen, calls } = await createHelper();
    await helper.checkPermissions(window);
    const replacement = Object.assign(new EventTarget(), { hdrHeadroom: 3 });
    details.screens = [replacement];
    details.currentScreen = replacement;
    details.dispatchEvent(new Event("screenschange"));
    replacement.hdrHeadroom = 2;
    replacement.dispatchEvent(new Event("hdrheadroomchange"));
    screen.dispatchEvent(new Event("hdrheadroomchange"));
    assert.deepEqual(calls, [2, 3, 2]);
});

test("permission denial does not request screen details", async () => {
    const { helper, window, calls, requests } = await createHelper("denied");
    await helper.checkPermissions(window);
    assert.equal(requests(), 0);
    assert.equal(calls.length, 1);
    assert.ok(Number.isNaN(calls[0]));
});

test("permission revocation clears headroom and removes detailed listeners", async () => {
    const { helper, window, screen, details, permission, calls } = await createHelper();
    await helper.checkPermissions(window);
    permission.state = "denied";
    permission.dispatchEvent(new Event("change"));
    assert.equal(helper.detailedScreens, undefined);
    assert.equal(calls.length, 2);
    assert.ok(Number.isNaN(calls[1]));
    screen.dispatchEvent(new Event("hdrheadroomchange"));
    details.dispatchEvent(new Event("currentscreenchange"));
    assert.equal(calls.length, 2);
});

test("unsupported permission queries still report unknown headroom", async () => {
    const { helper, window, calls } = await createHelper();
    window.navigator.permissions.query = async () => { throw new TypeError("Unsupported permission"); };
    await helper.checkPermissions(window);
    assert.equal(calls.length, 1);
    assert.ok(Number.isNaN(calls[0]));
});
