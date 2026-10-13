using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;

namespace Win4K.Core;

/// <summary>
/// Lets us talk to sylvan.apple.com over HTTPS on Windows.
///
/// The problem: sylvan.apple.com presents a genuine Apple certificate chaining
///     sylvan.apple.com -> Apple Server Authentication CA -> Apple Root CA
/// and *Apple Root CA is not in the Windows trusted root store*. It is trusted on Apple's
/// own platforms, so this never troubled the macOS Aerial project, but on Windows every
/// TLS handshake to Apple's aerial CDN fails with "unable to get local issuer certificate".
///
/// The two bad answers are (a) downgrade to http://, which does work but throws away
/// authenticity, and (b) install Apple Root CA into the machine's trust store, which
/// silently widens trust for every application on the system.
///
/// What we do instead: keep HTTPS, and add Apple Root CA as a trust anchor *for this
/// process only*. A certificate is accepted only if it chains to the real Apple Root CA
/// (SHA-1 611E5B662C593A08FF58D14AE22452D198DF6C60, valid to 2035), whose public
/// certificate is embedded from assets/apple-root-ca.cer. Nothing else is loosened:
/// name mismatches are still fatal, and certificates that do not terminate at either a
/// Windows-trusted root or Apple's are still rejected.
/// </summary>
public static class AppleTrust
{
    /// <summary>Publicly documented SHA-1 thumbprint of Apple Root CA.</summary>
    public const string AppleRootThumbprint = "611E5B662C593A08FF58D14AE22452D198DF6C60";

    private static readonly Lazy<X509Certificate2?> Root = new(LoadEmbeddedRoot);

    public static X509Certificate2? AppleRootCertificate => Root.Value;

    private static X509Certificate2? LoadEmbeddedRoot()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var name = assembly.GetManifestResourceNames()
                               .FirstOrDefault(n => n.EndsWith("apple-root-ca.cer", StringComparison.OrdinalIgnoreCase));
            if (name is null)
            {
                Log.Warn("AppleTrust: embedded Apple Root CA not found");
                return null;
            }

            using var stream = assembly.GetManifestResourceStream(name)!;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);

            var cert = X509CertificateLoader.LoadCertificate(buffer.ToArray());

            // Refuse to anchor trust to anything but the certificate we intended to ship.
            if (!string.Equals(cert.Thumbprint, AppleRootThumbprint, StringComparison.OrdinalIgnoreCase))
            {
                Log.Error($"AppleTrust: embedded root has unexpected thumbprint {cert.Thumbprint}; ignoring it");
                return null;
            }

            return cert;
        }
        catch (Exception ex)
        {
            Log.Error("AppleTrust: failed to load embedded Apple Root CA", ex);
            return null;
        }
    }

    /// <summary>
    /// Validation callback for <see cref="System.Net.Http.SocketsHttpHandler"/>.
    /// Defers entirely to the platform first; only chain-trust failures get a second look.
    /// </summary>
    public static bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
            return true;

        // A hostname mismatch or a missing certificate is never something an extra root
        // anchor should rescue, so only chain errors are reconsidered.
        if (errors != SslPolicyErrors.RemoteCertificateChainErrors || certificate is null)
            return false;

        var appleRoot = Root.Value;
        if (appleRoot is null)
            return false;

        try
        {
            using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            using var rebuilt = new X509Chain();

            rebuilt.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            rebuilt.ChainPolicy.CustomTrustStore.Add(appleRoot);

            // Apple's intermediate is sent by the server; carry the presented chain across so
            // we are not relying on AIA fetching, which is what fails on a locked-down machine.
            if (chain is not null)
            {
                foreach (var element in chain.ChainElements)
                    rebuilt.ChainPolicy.ExtraStore.Add(element.Certificate);
            }

            // Revocation for Apple's CA is not reliably reachable from a non-Apple platform,
            // and a screen saver must not hang on an OCSP timeout at wake-up.
            rebuilt.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            rebuilt.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;

            var ok = rebuilt.Build(leaf);
            if (!ok)
            {
                var reasons = string.Join(", ", rebuilt.ChainStatus.Select(s => s.Status.ToString()));
                Log.Warn($"AppleTrust: chain rejected ({reasons})");
                return false;
            }

            // Belt and braces: the chain must actually terminate at Apple Root CA, not merely
            // build successfully against some other anchor that crept into the store.
            var anchor = rebuilt.ChainElements[^1].Certificate;
            var anchored = string.Equals(anchor.Thumbprint, AppleRootThumbprint, StringComparison.OrdinalIgnoreCase);
            if (!anchored)
                Log.Warn($"AppleTrust: chain terminated at unexpected anchor {anchor.Subject}");

            return anchored;
        }
        catch (Exception ex)
        {
            Log.Error("AppleTrust: validation threw", ex);
            return false;
        }
    }
}
