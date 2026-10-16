// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteApp.Client;

/// <summary>
/// Per-user settings, stored as JSON under %APPDATA%\RemoteAppClient. Holds the
/// stable device ID, recent hosts and the pairing tokens servers issued us (so
/// the code is entered once per server). Nothing here is bundled or committed.
/// </summary>
public sealed class Settings
{
    public string DeviceID { get; set; } = Guid.NewGuid().ToString();
    public string LastHost { get; set; } = "";
    public List<string> RecentHosts { get; set; } = [];
    /// <summary>Pairing token per server address, as typed.</summary>
    public Dictionary<string, string> PairTokens { get; set; } = [];
    /// <summary>Ctrl sends ⌘ (command) so Ctrl+C becomes ⌘C on a Mac host; off sends ⌃ (control).</summary>
    public bool CtrlSendsCommand { get; set; } = true;
    public bool ShowStatistics { get; set; }
    /// <summary>Initial virtual-window size in points.</summary>
    public int StreamWidth { get; set; } = 1280;
    public int StreamHeight { get; set; } = 800;

    [JsonIgnore]
    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteAppClient");

    private static string FilePath => Path.Combine(Directory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions) ?? new Settings();
        }
        catch
        {
            // Corrupt or unreadable: start fresh (a new device ID means re-pairing once).
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // Best effort; settings are a convenience.
        }
    }

    public void RememberHost(string host)
    {
        LastHost = host;
        RecentHosts.RemoveAll(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));
        RecentHosts.Insert(0, host);
        if (RecentHosts.Count > 8) RecentHosts.RemoveRange(8, RecentHosts.Count - 8);
    }
}
