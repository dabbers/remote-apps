// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text.Json;

namespace RemoteApp.Server;

/// <summary>Prototype auth: the client must send the 6-digit code shown in the server window.</summary>
public static class PairingCode
{
    public static string Random() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public static bool Verify(string expected, string credential) =>
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(expected),
            System.Text.Encoding.UTF8.GetBytes(credential.Trim()));
}

/// <summary>
/// Remembers which devices have paired so they skip the code next time. One
/// long-lived random token per device, persisted under %LOCALAPPDATA%\RemoteAppServer.
/// </summary>
public sealed class PairingStore
{
    public sealed record Device(string Token, string Name, DateTimeOffset Paired, DateTimeOffset LastSeen);

    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, Device> _devices = new();

    public PairingStore()
    {
        Directory.CreateDirectory(TlsIdentity.Directory);
        _path = Path.Combine(TlsIdentity.Directory, "pairings.json");
        try
        {
            if (File.Exists(_path))
                _devices = JsonSerializer.Deserialize<Dictionary<string, Device>>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception)
        {
            _devices = new();
        }
    }

    private void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_devices, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception) { /* best effort */ }
    }

    /// <summary>True if this device presented a token we issued it.</summary>
    public bool IsValid(string deviceID, string token)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(token) || !_devices.TryGetValue(deviceID, out var device)) return false;
            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(device.Token), System.Text.Encoding.UTF8.GetBytes(token)))
                return false;
            _devices[deviceID] = device with { LastSeen = DateTimeOffset.UtcNow };
            Save();
            return true;
        }
    }

    /// <summary>Mint and store a token after a successful code pairing.</summary>
    public string Issue(string deviceID, string name)
    {
        lock (_lock)
        {
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var now = DateTimeOffset.UtcNow;
            _devices[deviceID] = new Device(token, name, now, now);
            Save();
            return token;
        }
    }

    public void ForgetAll()
    {
        lock (_lock)
        {
            _devices.Clear();
            Save();
        }
    }

    public int Count { get { lock (_lock) return _devices.Count; } }

    public List<string> Names { get { lock (_lock) return _devices.Values.Select(d => d.Name).Order().ToList(); } }
}
