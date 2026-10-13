using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Win4K.Core;

namespace Win4K.Tests;

/// <summary>
/// The PEM bundle handed to mpv's libcurl. Built from this machine's certificate stores, so
/// these assert properties of whatever the stores contain rather than exact contents.
/// </summary>
public class CaBundleTests
{
    private static List<X509Certificate2> Read(string path) =>
        Regex.Matches(File.ReadAllText(path), "-----BEGIN CERTIFICATE-----.*?-----END CERTIFICATE-----", RegexOptions.Singleline)
             .Select(m => X509Certificate2.CreateFromPem(m.Value))
             .ToList();

    [Fact]
    public void Bundle_never_contains_expired_certificates()
    {
        // An expired ISRG Root X2 cross-sign in the bundle was picked over the valid one the
        // server sent, failing TLS for every archive.org download host.
        var path = CaBundle.Ensure(force: true);
        Assert.NotNull(path);

        var now = DateTime.Now;
        var certs = Read(path!);

        Assert.NotEmpty(certs);
        Assert.All(certs, c => Assert.True(c.NotAfter >= now && c.NotBefore <= now,
            $"{c.Subject} is outside its validity period ({c.NotBefore:d} – {c.NotAfter:d})"));
    }

    [Fact]
    public void Bundle_includes_Apple_Root_CA()
    {
        var path = CaBundle.Ensure(force: true);
        Assert.NotNull(path);

        var apple = AppleTrust.AppleRootCertificate;
        Assert.NotNull(apple);
        Assert.Contains(Read(path!), c => c.Thumbprint == apple!.Thumbprint);
    }

    [Fact]
    public void A_bundle_from_an_older_build_is_regenerated()
    {
        var path = CaBundle.Ensure(force: true)!;

        // Simulate a fresh-looking bundle written before the format header existed.
        File.WriteAllText(path, "-----BEGIN CERTIFICATE-----\n-----END CERTIFICATE-----\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);

        var again = CaBundle.Ensure();

        Assert.Equal(CaBundle.FormatHeader, File.ReadLines(again!).First());
    }
}
