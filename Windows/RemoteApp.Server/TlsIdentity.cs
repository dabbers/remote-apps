// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RemoteApp.Server;

/// <summary>
/// The server's TLS identity. On first launch a self-signed certificate is
/// generated into %LOCALAPPDATA%\RemoteAppServer (PFX plus a per-install
/// random passphrase beside it) — nothing is bundled or committed, so a fresh
/// checkout carries no private key. It is only ever used over the tailnet,
/// where WireGuard is the real trust boundary; clients do not verify it.
/// </summary>
public static class TlsIdentity
{
    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteAppServer");

    private static string PfxPath => Path.Combine(Directory, "server.pfx");
    private static string PassPath => Path.Combine(Directory, "server.pfx.pass");

    /// <summary>Loads the stored identity, generating one on first run.</summary>
    public static X509Certificate2 LoadOrCreate()
    {
        System.IO.Directory.CreateDirectory(Directory);
        if (File.Exists(PfxPath) && File.Exists(PassPath))
        {
            try { return Load(); }
            catch (CryptographicException) { /* regenerate below */ }
        }
        Generate();
        return Load();
    }

    private static X509Certificate2 Load()
    {
        var passphrase = File.ReadAllText(PassPath).Trim();
        // UserKeySet (not ephemeral): Schannel needs a persisted private key to serve TLS.
        return X509CertificateLoader.LoadPkcs12FromFile(PfxPath, passphrase,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }

    private static void Generate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=RemoteApp Server, O=RemoteApp", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("remoteapp.local");
        san.AddDnsName(Environment.MachineName);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));   // server auth
        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));

        var passphrase = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        File.WriteAllBytes(PfxPath, certificate.Export(X509ContentType.Pfx, passphrase));
        File.WriteAllText(PassPath, passphrase);
        foreach (var path in new[] { PfxPath, PassPath })
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.NotContentIndexed);
    }

    /// <summary>SHA-256 fingerprint for display (so a user can compare with a client that pins).</summary>
    public static string Fingerprint(X509Certificate2 certificate) =>
        string.Join(':', certificate.GetCertHash(HashAlgorithmName.SHA256).Select(b => b.ToString("X2")));
}
