using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace AndroidMultiGameManager;

public sealed record NetworkPreflightResult(
    bool Ok,
    bool InspectionDetected,
    string Host,
    string Subject,
    string Issuer,
    string Message);

public static class NetworkPreflightService
{
    public static async Task<NetworkPreflightResult> CheckGoogleTlsAsync(
        string host="ssl.gstatic.com",
        int port=443,
        int timeoutMs=10000)
    {
        using var tcp=new TcpClient();
        using var cts=new CancellationTokenSource(timeoutMs);
        await tcp.ConnectAsync(host,port,cts.Token);

        X509Certificate2? remote=null;
        using var ssl=new SslStream(
            tcp.GetStream(),
            leaveInnerStreamOpen:false,
            (sender,certificate,chain,errors)=>
            {
                if(certificate is not null)
                    remote=new X509Certificate2(certificate);
                return true;
            });

        await ssl.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost=host,
                EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13,
                CertificateRevocationCheckMode=X509RevocationMode.NoCheck
            },
            cts.Token);

        if(remote is null)
            return new(false,false,host,"","","TLS 인증서를 확인하지 못했습니다.");

        var subject=remote.Subject??"";
        var issuer=remote.Issuer??"";
        var inspection=
            issuer.Contains("GNE_CERT",StringComparison.OrdinalIgnoreCase)||
            issuer.Contains("GNE",StringComparison.OrdinalIgnoreCase)||
            subject.Contains("GNE_CERT",StringComparison.OrdinalIgnoreCase);

        if(inspection)
        {
            return new(
                false,
                true,
                host,
                subject,
                issuer,
                $"HTTPS 보안 검사가 감지되었습니다.\n인증서 발급자: {issuer}\n\n현재 네트워크에서는 Android의 Google 로그인이 실패할 수 있습니다. 휴대폰 핫스팟 등 SSL 검사가 없는 네트워크로 전환한 뒤 다시 시도하세요.");
        }

        return new(
            true,
            false,
            host,
            subject,
            issuer,
            $"Google TLS 점검 정상\n인증서 발급자: {issuer}");
    }
}
