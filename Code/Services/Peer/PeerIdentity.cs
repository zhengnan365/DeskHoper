using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MonitorSwitch.Services;

/// 本机在局域网里的身份：一张自签证书。私钥放在当前用户的证书存储里（不落盘、不写密码文件），
/// 指纹用来在对端 pin 住，属于“第一次见面信任、以后认指纹”的 TOFU 模型。
public sealed class PeerIdentity : IDisposable
{
    private readonly X509Certificate2 _certificate;
    private readonly bool _persisted;

    private PeerIdentity(X509Certificate2 certificate, bool persisted)
    {
        _certificate = certificate;
        _persisted = persisted;
        Fingerprint = ComputeFingerprint(certificate);
    }

    public string Fingerprint { get; }
    public X509Certificate2 Certificate => _certificate;
    public byte[] CertificateDer => _certificate.RawData;

    public static string ComputeFingerprint(X509Certificate2 certificate) => Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();

    public static PeerIdentity LoadOrCreate(string subjectName)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        var found = store.Certificates.Find(X509FindType.FindBySubjectName, subjectName, false);
        if (found.Count > 0) return new PeerIdentity(new X509Certificate2(found[0]!), persisted: true);

        using var key = RSA.Create(3072);
        var request = new CertificateRequest($"CN={subjectName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddYears(3));
        // 导出成 pfx 只是为了让私钥以“用户密钥集”的形式落进证书存储，这个口令是一次性随机值，不写进任何文件。
        var temporaryPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var importable = new X509Certificate2(generated.Export(X509ContentType.Pfx, temporaryPassword), temporaryPassword,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);
        store.Add(importable);
        var stored = store.Certificates.Find(X509FindType.FindByThumbprint, importable.Thumbprint, false);
        return new PeerIdentity(new X509Certificate2(stored[0]!), persisted: true);
    }

    /// 从当前用户证书存储里删除本机身份（卸载/重置时用，测试也用它收尾）。
    public static void Remove(string subjectName)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        foreach (var certificate in store.Certificates.Find(X509FindType.FindBySubjectName, subjectName, false))
            store.Remove(certificate);
    }

    /// 测试用的临时身份，不碰证书存储。
    public static PeerIdentity CreateEphemeral(string subjectName)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={subjectName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return new PeerIdentity(request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddMinutes(30)), persisted: false);
    }

    public byte[] Sign(byte[] challenge)
    {
        using var rsa = _certificate.GetRSAPrivateKey() ?? throw new InvalidOperationException("本机证书没有可用的私钥。");
        return rsa.SignData(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    public static bool Verify(byte[] certificateDer, byte[] challenge, byte[] signature)
    {
        try
        {
            using var certificate = new X509Certificate2(certificateDer);
            using var rsa = certificate.GetRSAPublicKey();
            return rsa is not null && rsa.VerifyData(challenge, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch { return false; }
    }

    public static string FingerprintOf(byte[] certificateDer) => ComputeFingerprint(new X509Certificate2(certificateDer));

    public void Dispose()
    {
        if (!_persisted) _certificate.Dispose();
    }
}
