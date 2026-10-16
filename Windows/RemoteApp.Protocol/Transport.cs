// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace RemoteApp.Protocol;

/// <summary>
/// TLS-over-TCP transport (PROTOCOL.md §1). The server presents a self-signed
/// certificate and the client accepts any certificate for the ALPN: the
/// tailnet's WireGuard layer is the real trust boundary.
/// </summary>
public static class Transport
{
    private static readonly List<SslApplicationProtocol> Alpn = [new SslApplicationProtocol(ProtocolConstants.Alpn)];

    /// <summary>Connects to a server and completes the TLS handshake.</summary>
    public static async Task<Stream> ConnectAsync(Endpoint endpoint, CancellationToken cancellationToken)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(endpoint.Host, endpoint.Port, cancellationToken).ConfigureAwait(false);
            var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                // SNI needs a hostname; an IP literal is skipped by SslStream itself.
                TargetHost = IPAddress.TryParse(endpoint.Host, out _) ? "remoteapp.local" : endpoint.Host,
                ApplicationProtocols = Alpn,
                EnabledSslProtocols = SslProtocols.None,   // OS default: TLS 1.3 where available, else 1.2
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, cancellationToken).ConfigureAwait(false);
            return ssl;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>Completes the server side of the TLS handshake on an accepted socket.</summary>
    public static async Task<Stream> AcceptAsync(Socket socket, X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        socket.NoDelay = true;
        var ssl = new SslStream(new NetworkStream(socket, ownsSocket: true), leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ApplicationProtocols = Alpn,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.None,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, cancellationToken).ConfigureAwait(false);
            return ssl;
        }
        catch
        {
            ssl.Dispose();
            throw;
        }
    }
}
