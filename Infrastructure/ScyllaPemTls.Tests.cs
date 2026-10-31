using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Coflnet.Core.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Infrastructure;

public class ScyllaPemTlsTests
{
    [TestCase(null)]
    [TestCase("false")]
    public void LegacyOptOutDoesNotResolveCredentialsOrReadFiles(string? enabled)
    {
        var config = Configuration(new() {
            ["SCYLLA:TLS:ENABLED"] = enabled,
            ["SCYLLA:TLS:CA_CERT_PATH"] = "/synthetic-missing/ca",
            ["SCYLLA:X509Certificate_PATHS"] = "/synthetic-legacy.pfx" });
        using var services = new ServiceCollection().AddKeyedSingleton<PemTlsOptions>("SCYLLA",
            (_, _) => throw new AssertionException("Opt-out resolved PEM credentials")).BuildServiceProvider();
        Assert.That(Startup.CreatePemScyllaOptions(config, services), Is.Null);
    }

    [Test]
    public void OptInRequiresExplicitVerifiedNameBeforeResolvingCredentials()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => Startup.CreatePemScyllaOptions(
            Configuration(new() { ["SCYLLA:TLS:ENABLED"] = "true" }), services));
    }

    [Test]
    public void MissingPemKeyFailsClosedDespiteLegacyPfxConfiguration()
    {
        var config = Configuration(new() {
            ["SCYLLA:TLS:ENABLED"] = "true", ["SCYLLA:TLS:SERVER_NAME"] = "sky-client",
            ["SCYLLA:TLS:CA_CERT_PATH"] = "/synthetic/ca",
            ["SCYLLA:X509Certificate_PATHS"] = "/synthetic-legacy.pfx" });
        using var services = new ServiceCollection().AddKeyedSingleton<PemTlsOptions>("SCYLLA",
            (_, _) => PemTlsOptions.FromConfiguration(config.GetSection("SCYLLA:TLS"))).BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => Startup.CreatePemScyllaOptions(config, services));
    }

    [Test]
    public void ActualRegistrationIsLazyAndOwnsCredentialLifetime()
    {
        var config = Configuration(new() {
            ["SCYLLA:TLS:CA_CERT_PATH"] = "/synthetic-missing/ca",
            ["SCYLLA:TLS:CERT_PATH"] = "/synthetic-missing/cert",
            ["SCYLLA:TLS:KEY_PATH"] = "/synthetic-missing/key",
            ["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"] = "http://127.0.0.1:4318" });
        var collection = new ServiceCollection().AddSingleton<IConfiguration>(config);
        new Startup(config).ConfigureServices(collection);
        var services = collection.BuildServiceProvider();
        var owner = services.GetRequiredKeyedService<PemTlsOptions>("SCYLLA");
        Assert.Throws<DirectoryNotFoundException>(() => owner.GetCertificate());
        services.Dispose();
        Assert.Throws<ObjectDisposedException>(() => owner.GetCertificate());
    }

    // Direct-root synthetic leaves intentionally contain no intermediates: no registry or user trust-store writes.
    [Test]
    public void NewPhysicalConnectionsUseRotatedKeyAndKeepEarlierIdentityAlive()
    {
        using var fixture = new Certificates();
        using var services = fixture.Services();
        var options = Startup.CreatePemScyllaOptions(fixture.Config, services)!;
        var original = (X509Certificate2)options.CertificateCollection[0];
        Assert.That(options.HostNameResolver(IPAddress.Loopback), Is.EqualTo("sky-client"));
        using var replacement = Certificates.Issue(fixture.NextRoot, client: true);
        fixture.WriteRoots(fixture.Root.ExportCertificatePem() + "\n" + fixture.NextRoot.ExportCertificatePem());
        fixture.WriteClient(replacement);
        Assert.That(options.HostNameResolver(IPAddress.Loopback), Is.EqualTo("sky-client"));
        var current = (X509Certificate2)options.CertificateCollection[0];
        Assert.That(current.Thumbprint, Is.EqualTo(replacement.Thumbprint));
        Assert.That(current.HasPrivateKey, Is.True);
        Assert.That(original.Thumbprint, Is.Not.EqualTo(current.Thumbprint));
        Assert.That(original.HasPrivateKey, Is.True);
    }

    [Test]
    public void ExplicitRootsNameAndServerPurposeAreEnforcedAndTrustReloads()
    {
        using var fixture = new Certificates();
        using var services = fixture.Services();
        var options = Startup.CreatePemScyllaOptions(fixture.Config, services)!;
        using var server = Certificates.Issue(fixture.Root, client: false);
        using var next = Certificates.Issue(fixture.NextRoot, client: false);
        bool Valid(X509Certificate2 cert, SslPolicyErrors errors = SslPolicyErrors.RemoteCertificateChainErrors) =>
            options.RemoteCertValidationCallback(this, cert, null, errors);
        Assert.That(Valid(server), Is.True);
        Assert.That(Valid(next, SslPolicyErrors.None), Is.False);
        Assert.That(Valid(server, SslPolicyErrors.RemoteCertificateNameMismatch), Is.False);
        Assert.That(Valid(fixture.Client), Is.False);
        fixture.WriteRoots(fixture.Root.ExportCertificatePem() + "\n" + fixture.NextRoot.ExportCertificatePem());
        Assert.That(Valid(server) && Valid(next), Is.True);
        fixture.WriteRoots(fixture.NextRoot.ExportCertificatePem());
        Assert.That(Valid(server), Is.False);
        Assert.That(Valid(next), Is.True);
        fixture.WriteRoots("invalid synthetic bundle");
        Assert.That(Valid(next), Is.False);
    }

    [TestCase("expired")]
    [TestCase("server-only")]
    [TestCase("mismatched-key")]
    public void InvalidRenewalClearsIdentityAndFailsClosed(string failure)
    {
        using var fixture = new Certificates();
        using var services = fixture.Services();
        var options = Startup.CreatePemScyllaOptions(fixture.Config, services)!;
        using var invalid = Certificates.Issue(fixture.Root, client: failure != "server-only", expired: failure == "expired");
        fixture.WriteClient(invalid);
        if (failure == "mismatched-key")
        {
            using var key = fixture.Client.GetECDsaPrivateKey()!;
            File.WriteAllText(Path.Combine(fixture.Directory, "key.pem"), key.ExportPkcs8PrivateKeyPem());
        }
        Assert.That(() => options.HostNameResolver(IPAddress.Loopback), Throws.Exception);
        Assert.That(options.CertificateCollection.Count, Is.Zero);
        using var server = Certificates.Issue(fixture.Root, client: false);
        Assert.That(options.RemoteCertValidationCallback(this, server, null, SslPolicyErrors.None), Is.False);
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private sealed class Certificates : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "player-state-pem-test-" + Guid.NewGuid());
        public X509Certificate2 Root { get; } = CreateRoot();
        public X509Certificate2 NextRoot { get; } = CreateRoot();
        public X509Certificate2 Client { get; }
        public IConfiguration Config { get; }
        public Certificates()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Client = Issue(Root, client: true);
            WriteRoots(Root.ExportCertificatePem());
            WriteClient(Client);
            Config = Configuration(new() {
                ["SCYLLA:TLS:ENABLED"] = "true", ["SCYLLA:TLS:SERVER_NAME"] = "sky-client",
                ["SCYLLA:TLS:CA_CERT_PATH"] = Path.Combine(Directory, "ca.pem"),
                ["SCYLLA:TLS:CERT_PATH"] = Path.Combine(Directory, "cert.pem"),
                ["SCYLLA:TLS:KEY_PATH"] = Path.Combine(Directory, "key.pem") });
        }
        public ServiceProvider Services() => new ServiceCollection().AddKeyedSingleton<PemTlsOptions>("SCYLLA",
            (_, _) => PemTlsOptions.FromConfiguration(Config.GetSection("SCYLLA:TLS"))).BuildServiceProvider();
        public void WriteRoots(string pem) => File.WriteAllText(Path.Combine(Directory, "ca.pem"), pem);
        public void WriteClient(X509Certificate2 cert)
        {
            File.WriteAllText(Path.Combine(Directory, "cert.pem"), cert.ExportCertificatePem());
            using var key = cert.GetECDsaPrivateKey()!;
            File.WriteAllText(Path.Combine(Directory, "key.pem"), key.ExportPkcs8PrivateKeyPem());
        }
        private static X509Certificate2 CreateRoot()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=Synthetic-" + Guid.NewGuid(), key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(2));
        }
        public static X509Certificate2 Issue(X509Certificate2 root, bool client, bool expired = false)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=sky-client", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new(client ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1") }, true));
            var now = DateTimeOffset.UtcNow;
            using var cert = request.Create(root, now.AddDays(-1), expired ? now.AddMinutes(-1) : now.AddDays(1), RandomNumberGenerator.GetBytes(16));
            return cert.CopyWithPrivateKey(key);
        }
        public void Dispose()
        {
            Client.Dispose(); Root.Dispose(); NextRoot.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
