// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

namespace RemoteApp.Protocol;

/// <summary>A server address as the user types it: a host (MagicDNS name or IP) and an optional port.</summary>
public sealed record Endpoint(string Host, int Port)
{
    /// <summary>
    /// Parses "host", "host:port", "[v6]:port" or a bare IPv6 address. The port
    /// defaults to <see cref="ProtocolConstants.DefaultPort"/>.
    /// </summary>
    public static Endpoint Parse(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) throw new FormatException("empty address");

        // "[::1]:53124"
        if (trimmed.StartsWith('['))
        {
            var close = trimmed.IndexOf(']');
            if (close < 0) throw new FormatException("unterminated IPv6 literal");
            var host = trimmed[1..close];
            var rest = trimmed[(close + 1)..];
            if (rest.Length == 0) return new Endpoint(host, ProtocolConstants.DefaultPort);
            if (rest[0] == ':' && int.TryParse(rest[1..], out var p6)) return new Endpoint(host, p6);
            throw new FormatException("bad port");
        }

        var colon = trimmed.LastIndexOf(':');
        // Exactly one colon → host:port. More than one → bare IPv6, no port.
        if (colon > 0 && trimmed.IndexOf(':') == colon && int.TryParse(trimmed[(colon + 1)..], out var port))
            return new Endpoint(trimmed[..colon], port);
        return new Endpoint(trimmed, ProtocolConstants.DefaultPort);
    }

    public override string ToString() =>
        Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
}
