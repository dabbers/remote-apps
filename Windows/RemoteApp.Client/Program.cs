// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using RemoteApp.Client.Forms;

namespace RemoteApp.Client;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // Unattended smoke test (used to verify a build against a running server):
        //   RemoteAppClient --smoke <host[:port]> <code|-> <app name|-> <report.txt> <screenshot.png> [seconds]
        if (args.Length >= 6 && args[0] == "--smoke")
        {
            var seconds = args.Length >= 7 && int.TryParse(args[6], out var s) ? s : 8;
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            var options = new SmokeOptions(args[1], args[2] == "-" ? null : args[2], args[3] == "-" ? null : args[3], args[4], args[5], seconds);
            Application.Run(new ClientApp(options));
            return;
        }
        Application.Run(new ClientApp());
    }
}
