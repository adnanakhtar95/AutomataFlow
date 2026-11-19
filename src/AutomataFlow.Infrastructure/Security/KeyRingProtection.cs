using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

namespace AutomataFlow.Infrastructure.Security;

/// <summary>
/// Encryption of the Data Protection key ring at rest (ADR-079). With <c>DataProtection:CertificatePath</c> (a PKCS#12
/// file) and <c>DataProtection:CertificatePassword</c>, every NEW key is written encrypted with the certificate's public
/// key, so a copy of the key volume alone (a backup, a stolen disk, another container that mounts it) cannot decrypt
/// workspace secrets or sensitive record inputs. Keys written before the certificate was configured stay readable and
/// are replaced by the normal key rotation.
/// <para>Rotation: configure the new certificate as <c>CertificatePath</c> and keep the old one as
/// <c>PreviousCertificatePath</c>/<c>PreviousCertificatePassword</c> until every key encrypted with it has expired
/// (90 days by default).</para>
/// A configured but unreadable certificate stops startup: running with a key ring that cannot be decrypted would fail
/// every secret later, at execution time.
/// </summary>
public static class KeyRingProtection
{
    public static void Configure(IDataProtectionBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);
        if (Load(configuration, "DataProtection:CertificatePath", "DataProtection:CertificatePassword") is not { } current)
        {
            return;
        }

        builder.ProtectKeysWithCertificate(current);
        var previous = Load(configuration, "DataProtection:PreviousCertificatePath", "DataProtection:PreviousCertificatePassword");
        builder.UnprotectKeysWithAnyCertificate(previous is null ? [current] : [current, previous]);
    }

    private static X509Certificate2? Load(IConfiguration configuration, string pathKey, string passwordKey)
    {
        if (configuration[pathKey] is not { Length: > 0 } path)
        {
            return null;
        }

        try
        {
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, configuration[passwordKey], X509KeyStorageFlags.EphemeralKeySet);
            if (!certificate.HasPrivateKey)
            {
                throw new InvalidOperationException($"{pathKey}: the certificate has no private key.");
            }

            return certificate;
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            // Never echo the password; the path is configuration, not a secret.
            throw new InvalidOperationException($"{pathKey}: the key ring certificate could not be loaded (wrong password or not a PKCS#12 file).", ex);
        }
    }
}
