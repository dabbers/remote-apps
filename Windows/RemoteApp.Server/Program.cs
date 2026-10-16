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
        // Self-test of input injection: types the text into a local text box and
        // prints what arrived. Usage: RemoteAppServer --type-test "some text"
        if (args.Length >= 2 && args[0] == "--type-test")
        {
            TypeTest(args[1]);
            return;
        }
        Application.Run(new ServerForm());
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
