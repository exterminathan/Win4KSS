using System.Net.Security;
using Win4K.Core;

namespace Win4K.Tests;

public class AppleTrustTests
{
    [Fact]
    public void Embedded_Apple_root_loads_with_the_expected_thumbprint()
    {
        var root = AppleTrust.AppleRootCertificate;

        Assert.NotNull(root);
        Assert.Equal(AppleTrust.AppleRootThumbprint, root!.Thumbprint, ignoreCase: true);
        Assert.Contains("Apple Root CA", root.Subject);

        // Self-signed root.
        Assert.Equal(root.Subject, root.Issuer);
    }

    [Fact]
    public void Embedded_Apple_root_is_still_valid()
    {
        // Expires 2035-02-09. If this ever fails, Apple rotated the root and
        // assets/apple-root-ca.cer needs refreshing.
        var root = AppleTrust.AppleRootCertificate!;
        Assert.True(DateTime.Now < root.NotAfter, $"Apple Root CA expired on {root.NotAfter:d}");
    }

    [Fact]
    public void Clean_handshakes_are_accepted_without_inspection()
    {
        Assert.True(AppleTrust.Validate(new object(), null, null, SslPolicyErrors.None));
    }

    [Theory]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch)]
    [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable)]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors)]
    public void Non_chain_failures_are_never_rescued_by_the_extra_anchor(SslPolicyErrors errors)
    {
        // Pinning Apple's root must not turn into "accept anything from Apple's CDN";
        // a hostname mismatch stays fatal.
        Assert.False(AppleTrust.Validate(new object(), null, null, errors));
    }

    [Fact]
    public void Chain_errors_with_no_certificate_are_rejected()
    {
        Assert.False(AppleTrust.Validate(new object(), null, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }
}
