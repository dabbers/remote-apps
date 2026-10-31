// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using RemoteApp.Protocol;
using RemoteApp.Server.Hosting;
using Windows.Win32.Foundation;

namespace RemoteApp.Server;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // The capture, encode and input threads run outside the UI loop, and an
        // exception escaping one of them ends the process with nothing in the
        // server's own log. Leave a trace in a crash log first.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrashLog("unhandled exception", e.ExceptionObject as Exception);
        Application.ThreadException += (_, e) => WriteCrashLog("UI thread exception", e.Exception);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        // Self-test of input injection: types the text into a local text box and
        // prints what arrived. Usage: RemoteAppServer --type-test "some text"
        if (args.Length >= 2 && args[0] == "--type-test")
        {
            TypeTest(args[1]);
            return;
        }
        Application.Run(new ServerForm());
    }

    /// <summary>Appends to RemoteAppServer.crash.log beside the regular log (which another writer holds open).</summary>
    private static void WriteCrashLog(string kind, Exception? e)
    {
        try
        {
            var path = Path.Combine(TlsIdentity.Directory, "RemoteAppServer.crash.log");
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {kind}: {e}{Environment.NewLine}");
        }
        catch
        {
            // Nothing sensible left to do while the process is going down.
        }
    }

    private static void TypeTest(string text)
    {
        using var form = new Form { Text = "RemoteApp type test", Width = 480, Height = 120, TopMost = true, StartPosition = FormStartPosition.CenterScreen };
        var box = new TextBox { Width = 440, Left = 10, Top = 20 };
        form.Controls.Add(box);
        form.Show();
        form.Activate();
        box.Focus();
        Application.DoEvents();
        Thread.Sleep(300);

        var bounds = form.RectangleToScreen(form.ClientRectangle);
        using var injector = new InputInjector(new HWND(form.Handle), (uint)Environment.ProcessId, bounds);
        injector.Handle(new KeyInputEvent(KeyEvent.ForText(text)));
        injector.Handle(new KeyInputEvent(KeyEvent.ForKeyCode(KeyCodes.LeftArrow)));
        injector.Handle(new KeyInputEvent(KeyEvent.ForText("!", KeyModifiers.Shift)));
        for (var i = 0; i < 20 && !box.Text.Contains('!'); i++)
        {
            Thread.Sleep(100);
            Application.DoEvents();
        }
        Console.WriteLine($"sent:     \"{text}\" + Left + Shift+!");
        Console.WriteLine($"received: \"{box.Text}\"");
    }
}
