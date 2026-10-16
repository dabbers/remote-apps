// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Text.Json;

namespace RemoteApp.Server;

/// <summary>
/// This PC's tailnet identity from the Tailscale local API (<c>tailscale status
/// --json</c>), so the server can bind to the tailnet address and show its
/// MagicDNS name. Nothing leaves the machine to find this out.
/// </summary>
public sealed record TailscaleInfo(string IPv4, string? IPv6, string DnsName, string HostName, bool Online)
{
    private static readonly string[] CliPaths =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Tailscale", "tailscale.exe"),
        "tailscale.exe",
    ];

    /// <summary>Null if Tailscale is not installed or the daemon is not reachable.</summary>
    public static TailscaleInfo? Current()
    {
        foreach (var cli in CliPaths)
        {
            var json = Run(cli, "status --json");
            if (json == null) continue;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("Self", out var self)) return null;
                string? v4 = null, v6 = null;
                if (self.TryGetProperty("TailscaleIPs", out var ips) && ips.ValueKind == JsonValueKind.Array)
                {
                    foreach (var ip in ips.EnumerateArray())
                    {
                        var s = ip.GetString();
                        if (s == null) continue;
                        if (s.Contains(':')) v6 ??= s; else v4 ??= s;
                    }
                }
                if (v4 == null) return null;
                var dns = (self.TryGetProperty("DNSName", out var d) ? d.GetString() : null)?.TrimEnd('.') ?? "";
                var host = self.TryGetProperty("HostName", out var h) ? h.GetString() ?? "PC" : "PC";
                var online = self.TryGetProperty("Online", out var o) && o.ValueKind == JsonValueKind.True;
                return new TailscaleInfo(v4, v6, dns.Length == 0 ? v4 : dns, host, online);
            }
            catch (JsonException)
            {
                return null;
            }
        }
        return null;
    }

    private static string? Run(string path, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process == null) return null;
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000)) { try { process.Kill(); } catch { } return null; }
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
