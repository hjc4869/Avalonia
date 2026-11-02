using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Input.TextInput;
using Avalonia.Threading;
using Avalonia.Win32;
using Avalonia.Win32.Input;
using Avalonia.Win32.WinRT;
using MicroCom.Runtime;
using Xunit;
using static Avalonia.Win32.Interop.UnmanagedMethods;

namespace Avalonia.IntegrationTests.Win32;

public class Imm32InputMethodTests : IDisposable
{
    private readonly WindowImpl _window = new();
    private readonly TestInputMethod _inputMethod = new();

    public Imm32InputMethodTests()
    {
        _window.Show(true, false);
        Assert.Equal(_window.Handle.Handle, GetActiveWindow());
        _inputMethod.SetLanguageAndWindow(_window, _window.Handle.Handle, GetKeyboardLayout(0));
    }

    [Fact]
    public async Task Requests_Keyboard_On_Text_Focus_And_Hides_On_Blur()
    {
        _inputMethod.SetClient(new TestClient());
        await DrainDispatcher();
        Assert.Equal(new[] { true }, _inputMethod.VisibilityRequests);

        _inputMethod.SetClient(null);
        await DrainDispatcher();
        Assert.Equal(new[] { true, false }, _inputMethod.VisibilityRequests);
    }

    [Fact]
    public async Task Activation_Request_Reopens_Keyboard_Without_Changing_Focus()
    {
        var client = new TestClient();
        _inputMethod.SetClient(client);
        await DrainDispatcher();
        _inputMethod.VisibilityRequests.Clear();

        client.RequestInputPane();

        Assert.Equal(new[] { true }, _inputMethod.VisibilityRequests);
    }

    [Fact]
    public async Task Replacing_Client_Unsubscribes_Previous_Activation_Requests()
    {
        var previous = new TestClient();
        var current = new TestClient();
        _inputMethod.SetClient(previous);
        _inputMethod.SetClient(current);
        _inputMethod.SetClient(current);
        await DrainDispatcher();
        _inputMethod.VisibilityRequests.Clear();

        previous.RequestInputPane();
        Assert.Empty(_inputMethod.VisibilityRequests);

        current.RequestInputPane();
        Assert.Equal(new[] { true }, _inputMethod.VisibilityRequests);
    }

    [Fact]
    public async Task Rapid_Focus_Changes_Do_Not_Hide_Keyboard_Between_Text_Clients()
    {
        _inputMethod.SetClient(new TestClient());
        await DrainDispatcher();
        _inputMethod.VisibilityRequests.Clear();

        _inputMethod.SetClient(null);
        _inputMethod.SetClient(new TestClient());
        await DrainDispatcher();

        Assert.NotEmpty(_inputMethod.VisibilityRequests);
        Assert.All(_inputMethod.VisibilityRequests, visible => Assert.True(visible));
    }

    [Fact]
    public async Task Inactive_Window_Does_Not_Change_Keyboard_Visibility()
    {
        using var other = new WindowImpl();
        other.Show(true, false);
        Assert.Equal(other.Handle.Handle, GetActiveWindow());

        var client = new TestClient();
        _inputMethod.SetClient(client);
        await DrainDispatcher();
        client.RequestInputPane();
        _inputMethod.SetClient(null);
        await DrainDispatcher();

        Assert.Empty(_inputMethod.VisibilityRequests);
    }

    [Fact]
    public async Task Clearing_Window_Detaches_Client_And_Ignores_Pending_Requests()
    {
        var previous = new TestClient();
        _inputMethod.SetClient(previous);
        _inputMethod.ClearLanguageAndWindow();
        await DrainDispatcher();
        Assert.Null(_inputMethod.Client);
        Assert.Empty(_inputMethod.VisibilityRequests);

        // Reuse the thread's input method for another focus session. A stale subscription
        // would now reopen the keyboard on behalf of the old client.
        _inputMethod.SetLanguageAndWindow(_window, _window.Handle.Handle, GetKeyboardLayout(0));
        _inputMethod.SetClient(new TestClient());
        await DrainDispatcher();
        _inputMethod.VisibilityRequests.Clear();
        previous.RequestInputPane();

        Assert.Empty(_inputMethod.VisibilityRequests);
    }

    [Fact]
    public unsafe void InputPane_Interop_Can_Obtain_Desktop_InputPane2()
    {
        if (Win32Platform.WindowsVersion < new Version(10, 0, 14393) ||
            !WinRTApiInformation.IsMethodPresent("Windows.UI.ViewManagement.InputPane", "TryShow"))
            return;

        using var interop = NativeWinRTMethods.CreateActivationFactory<IInputPaneInterop>(
            "Windows.UI.ViewManagement.InputPane");
        var iid = MicroComRuntime.GetGuidFor(typeof(IInputPane2));
        using var pane = MicroComRuntime.CreateProxyFor<IInputPane2>(
            (IntPtr)interop.GetForWindow(_window.Handle.Handle, &iid), true);

        Assert.NotNull(pane);
    }

    private static async Task DrainDispatcher()
        => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

    public void Dispose()
    {
        _inputMethod.ClearLanguageAndWindow();
        _window.Dispose();
    }

    private sealed class TestInputMethod : Imm32InputMethod
    {
        public List<bool> VisibilityRequests { get; } = new();

        // Record requests rather than requiring a touch device or changing the real keyboard.
        protected override void SetInputPaneVisible(bool visible) => VisibilityRequests.Add(visible);
    }

    private sealed class TestClient : TextInputMethodClient
    {
        public override Visual TextViewVisual { get; } = new Visual();
        public override bool SupportsPreedit => false;
        public override bool SupportsSurroundingText => false;
        public override string SurroundingText => string.Empty;
        public override Rect CursorRectangle => default;
        public override TextSelection Selection { get; set; }

        public void RequestInputPane() => RaiseInputPaneActivationRequested();
    }
}