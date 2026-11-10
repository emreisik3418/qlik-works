using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using QlikSheetCleaner.Configuration;
using Serilog;

namespace QlikSheetCleaner.Qlik;

/// <summary>client.pfx ve root.cer yükler; sunucu sertifikasını Qlik root CA'sına göre doğrular.</summary>
public sealed class CertificateProvider : IDisposable
{
    private readonly X509Certificate2 _root;
    private readonly bool _allowNameMismatch;
    private readonly ILogger _log = Log.ForContext<CertificateProvider>();

    public X509Certificate2 ClientCertificate { get; }

    public CertificateProvider(QlikSettings settings)
    {
        if (!File.Exists(settings.ClientCertificatePath))
            throw new FileNotFoundException("İstemci sertifikası bulunamadı.", settings.ClientCertificatePath);
        if (!File.Exists(settings.RootCertificatePath))
            throw new FileNotFoundException("Root sertifikası bulunamadı.", settings.RootCertificatePath);

        // Windows'ta SslStream ile EphemeralKeySet çalışmaz; UserKeySet kullanıyoruz.
        ClientCertificate = new X509Certificate2(
            settings.ClientCertificatePath,
            settings.ClientCertificatePassword,
            X509KeyStorageFlags.UserKeySet);

        if (!ClientCertificate.HasPrivateKey)
            throw new InvalidOperationException("client.pfx private key içermiyor. QMC'de 'Include secret key' ile export edin.");

        _root = new X509Certificate2(settings.RootCertificatePath);
        _allowNameMismatch = settings.AllowCertificateNameMismatch;

        _log.Information("İstemci sertifikası yüklendi: {Subject} (bitiş {NotAfter:yyyy-MM-dd})",
            ClientCertificate.Subject, ClientCertificate.NotAfter);
    }

    public bool ValidateServerCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)) return false;

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) && !_allowNameMismatch)
        {
            _log.Error("Sunucu sertifikası adı eşleşmiyor: {Subject}. Host'u sertifikadaki ad ile verin veya AllowCertificateNameMismatch=true yapın.",
                certificate.Subject);
            return false;
        }

        if (!errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors)) return true;

        // root.cer makineye kurulu değilse zinciri Qlik root CA'sı ile doğrula.
        using var serverCert = new X509Certificate2(certificate);
        using var customChain = new X509Chain();
        customChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        customChain.ChainPolicy.CustomTrustStore.Add(_root);
        customChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (chain is not null)
        {
            foreach (var element in chain.ChainElements)
                customChain.ChainPolicy.ExtraStore.Add(element.Certificate);
        }

        var ok = customChain.Build(serverCert);
        if (!ok)
        {
            var status = string.Join(", ", customChain.ChainStatus.Select(s => s.Status));
            _log.Error("Sunucu sertifikası root.cer ile doğrulanamadı: {Subject} ({Status})", certificate.Subject, status);
        }
        return ok;
    }

    public void Dispose()
    {
        ClientCertificate.Dispose();
        _root.Dispose();
    }
}
