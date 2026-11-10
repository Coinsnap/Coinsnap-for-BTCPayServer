using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BTCPayServer.Plugins.Coinsnap.ReleaseGate;

/// <summary>
/// A throwaway CA that only the gate's BTCPay trusts (through SSL_CERT_FILE), so the fault proxy can
/// terminate TLS for the dev host without touching the system trust store. It is name-constrained to
/// the dev host and its keys are readable by this user only.
/// </summary>
public static class GateCertificateAuthority
{
    private const string SystemBundle = "/etc/ssl/certs/ca-certificates.crt";
    private const string NameConstraintsOid = "2.5.29.30";
    private const UnixFileMode OwnerOnlyDir = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static string StateDir { get; } = Path.Combine(AppContext.BaseDirectory, "gate-state");

    /// <summary>The proxy's certificate and key, mounted into its container.</summary>
    public static string CertDir => Path.Combine(StateDir, "certs");

    /// <summary>System roots plus the gate CA, for the BTCPay process only.</summary>
    public static string TrustBundlePath => Path.Combine(StateDir, "ca-bundle.pem");

    private static string CaFile => Path.Combine(CertDir, "gate-ca.pem");

    public static void Ensure()
    {
        Directory.CreateDirectory(CertDir);
        File.SetUnixFileMode(CertDir, OwnerOnlyDir);

        if (!File.Exists(CaFile) || !IsComplete())
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=Coinsnap Release Gate CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(PermitOnly(GateConfig.DevHost));
            using var ca = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));

            foreach (var stale in Directory.GetFiles(CertDir))
                File.Delete(stale);
            WriteOwnerOnly(CaFile, key.ExportPkcs8PrivateKeyPem() + "\n" + ca.ExportCertificatePem() + "\n");
            IssueLeaf(ca);
        }

        using var cert = X509Certificate2.CreateFromPemFile(CaFile, CaFile);
        File.WriteAllText(TrustBundlePath, File.ReadAllText(SystemBundle) + "\n" + cert.ExportCertificatePem() + "\n");
    }

    private static void IssueLeaf(X509Certificate2 ca)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={GateConfig.DevHost}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(GateConfig.DevHost);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], false));

        using var leaf = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1),
            RandomNumberGenerator.GetBytes(16));
        WriteOwnerOnly(Path.Combine(CertDir, "leaf.pem"), leaf.ExportCertificatePem() + "\n" + ca.ExportCertificatePem() + "\n");
        WriteOwnerOnly(Path.Combine(CertDir, "leaf.key"), key.ExportPkcs8PrivateKeyPem() + "\n");
    }

    private static void WriteOwnerOnly(string path, string content)
    {
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = OwnerOnlyFile
        });
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }

    private static bool IsComplete()
    {
        using var cert = X509Certificate2.CreateFromPemFile(CaFile, CaFile);
        return cert.Extensions.Any(e => e.Oid?.Value == NameConstraintsOid) &&
               File.Exists(Path.Combine(CertDir, "leaf.pem")) &&
               File.Exists(Path.Combine(CertDir, "leaf.key"));
    }

    // NameConstraints { permittedSubtrees [0] { GeneralSubtree { dNSName [2] host } } }
    private static X509Extension PermitOnly(string host)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true)))
        using (writer.PushSequence())
            writer.WriteCharacterString(UniversalTagNumber.IA5String, host, new Asn1Tag(TagClass.ContextSpecific, 2));
        return new X509Extension(NameConstraintsOid, writer.Encode(), critical: true);
    }
}
