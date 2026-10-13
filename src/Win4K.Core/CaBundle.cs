using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Win4K.Core;

/// <summary>
/// Builds a PEM certificate bundle for libmpv.
///
/// <see cref="AppleTrust"/> solves the Apple Root CA problem for .NET's HttpClient, but mpv
/// does its own networking through libcurl and has its own trust store — so streaming still
/// failed with "TLS certificate verification failed" even after our own downloads worked.
///
/// mpv accepts --tls-ca-file, but pointing it at Apple's root alone would *replace* the
/// default trust list and break every other HTTPS source. So the bundle written here is a
/// superset: every root Windows already trusts, plus Apple Root CA. TLS verification stays
/// on — the alternative people usually reach for, --tls-verify=no, would disable certificate
/// checking for all streaming.
/// </summary>
public static class CaBundle
{
    /// <summary>Rebuilt when older than this, so Windows root store updates get picked up.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    /// <summary>
    /// First line of every bundle. Bump it whenever what goes into the bundle changes, so a
    /// cached bundle written by an older build is regenerated instead of lingering for up to
    /// <see cref="MaxAge"/>.
    /// </summary>
    public const string FormatHeader = "# Win4K CA bundle v2: Windows roots + intermediates, expired excluded, + Apple Root CA";

    public static string Path => System.IO.Path.Combine(AppPaths.Root, "ca-bundle.pem");

    /// <summary>
    /// Returns the bundle path, generating it if missing or stale.
    /// Returns null if the bundle could not be produced, in which case the caller should
    /// leave mpv on its own defaults rather than pointing it at a broken file.
    /// </summary>
    public static string? Ensure(bool force = false)
    {
        try
        {
            AppPaths.EnsureRoot();
            var path = Path;

            if (!force && File.Exists(path))
            {
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
                if (age < MaxAge && new FileInfo(path).Length > 0 && HasCurrentFormat(path))
                    return path;
            }

            var pem = Build();
            if (pem.Length == 0)
            {
                Log.Warn("CaBundle: produced no certificates; leaving mpv on its defaults");
                return null;
            }

            var temp = path + ".tmp";
            File.WriteAllText(temp, pem, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, path, overwrite: true);

            Log.Info($"CaBundle: wrote {path}");
            return path;
        }
        catch (Exception ex)
        {
            Log.Error("CaBundle: generation failed", ex);
            return null;
        }
    }

    private static bool HasCurrentFormat(string path)
    {
        try
        {
            return File.ReadLines(path).FirstOrDefault() == FormatHeader;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string Build()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder(256 * 1024);
        var count = 0;
        var skipped = 0;
        var now = DateTime.Now;

        sb.AppendLine(FormatHeader);

        foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            // Root holds trust anchors; CertificateAuthority carries intermediates that some
            // servers fail to send, and including them costs nothing.
            foreach (var storeName in new[] { StoreName.Root, StoreName.CertificateAuthority })
            {
                try
                {
                    using var store = new X509Store(storeName, location);
                    store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);

                    foreach (var cert in store.Certificates)
                    {
                        if (!seen.Add(cert.Thumbprint))
                            continue;

                        // Never ship an expired certificate as a trust anchor. libcurl's
                        // OpenSSL looks in this bundle *first* when building a chain, so an
                        // expired copy of a CA (here: the 2025 "ISRG Root X2" cross-sign
                        // from the intermediate store) is chosen over the valid one the
                        // server sends, and verification fails. That broke every archive.org
                        // stream, because its downloads redirect to Let's Encrypt-signed hosts.
                        if (cert.NotAfter < now || cert.NotBefore > now)
                        {
                            skipped++;
                            continue;
                        }

                        Append(sb, cert);
                        count++;
                    }
                }
                catch (Exception ex)
                {
                    // A store we cannot read is not fatal; the others still contribute.
                    Log.Warn($"CaBundle: could not read {location}/{storeName}: {ex.Message}");
                }
            }
        }

        var apple = AppleTrust.AppleRootCertificate;
        if (apple is not null && seen.Add(apple.Thumbprint))
        {
            Append(sb, apple);
            count++;
            Log.Info("CaBundle: added Apple Root CA (absent from the Windows root store)");
        }

        if (count == 0)
            return "";

        Log.Info($"CaBundle: {count} certificates ({skipped} expired or not yet valid, excluded)");
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, X509Certificate2 cert)
    {
        // A comment line before each entry makes the bundle diagnosable by eye.
        sb.Append("# ").AppendLine(cert.Subject);
        sb.AppendLine(cert.ExportCertificatePem());
    }
}
