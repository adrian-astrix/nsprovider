using System.Security.Cryptography;
using FxSsh;
using FxSsh.Logging;
using FxSsh.Services;
using FxSsh.Services.Sftp;
using NsProvider.Core;
using NsProvider.Core.Storage;

namespace NsProvider.Sftp;

/// <summary>
/// Servidor SSH/SFTP embutido do motor (FxSsh, vendorado — sem dependências externas).
///  - Porta 8821 (configurável via NSP_SFTP_PORT)
///  - Autenticação: usuários do motor (SQLite, senha PBKDF2)
///  - Chroot: cada usuário vê / = {Base}/servers/{userId}
/// Host keys RSA + ECDSA geradas na 1ª execução e persistidas em {Base}/data/keys.
/// </summary>
public sealed class SftpHost : IAsyncDisposable
{
    private readonly UserRepository _users;
    private readonly int _port;
    private SshServer? _server;

    public SftpHost(UserRepository users)
    {
        _users = users;
        _port = int.TryParse(Environment.GetEnvironmentVariable("NSP_SFTP_PORT"), out var p) ? p : 8821;
    }

    public void Start()
    {
        var (rsaPem, ecPem) = EnsureHostKeys();

        var server = new SshServer(new StartingInfo(
            System.Net.IPAddress.IPv6Any, _port, "SSH-2.0-NsProvider")); // dual-stack: aceita IPv4 e IPv6
        server.AddHostKey("rsa-sha2-256", rsaPem);
        server.AddHostKey("rsa-sha2-512", rsaPem);
        server.AddHostKey("ecdsa-sha2-nistp256", ecPem);
        server.ConnectionAccepted += OnConnectionAccepted;

        server.Start();
        _server = server;
        Console.WriteLine($"[motor] SFTP embutido ouvindo na porta {_port} (chroot por usuário)");
    }

    private void OnConnectionAccepted(object? sender, Session session)
    {
        session.ServiceRegistered += (s, service) =>
        {
            if (service is UserAuthService userAuth)
            {
                userAuth.UserAuth += (ss, e) =>
                {
                    // apenas senha; "none" e "publickey" ficam desligados
                    if (e.AuthMethod != "password") { e.Result = false; return; }
                    var user = _users.GetByName(e.Username);
                    e.Result = user is not null &&
                               Database.VerifyPassword(e.Password, user.PasswordHash);
                };
            }
            else if (service is ConnectionService conn)
            {
                conn.CommandOpened += (ss, e) =>
                {
                    // sem shell/exec — o motor é só SFTP
                    e.Agreed = false;
                };
                conn.SubsystemRequested += (ss, e) =>
                {
                    if (e.Name != "sftp") return;
                    var user = _users.GetByName(e.AttachedUserAuthArgs.Username);
                    if (user is null) return;

                    var root = AppPaths.UserDir(user.Id);
                    Directory.CreateDirectory(root);

                    e.Agreed = true;
                    var sftp = new SftpService(root); // chroot: "/" do cliente = pasta do usuário
                    sftp.Attach(e.Channel);
                };
            }
        };
    }

    private static (string RsaPem, string EcPem) EnsureHostKeys()
    {
        var rsaPath = Path.Combine(AppPaths.KeysDir, "host_rsa.pem");
        var ecPath = Path.Combine(AppPaths.KeysDir, "host_ecdsa.pem");

        var rsaPem = File.Exists(rsaPath)
            ? File.ReadAllText(rsaPath)
            : PersistKey(rsaPath, CreateRsaPem());

        var ecPem = File.Exists(ecPath)
            ? File.ReadAllText(ecPath)
            : PersistKey(ecPath, CreateEcPem());

        return (rsaPem, ecPem);
    }

    private static string PersistKey(string path, string pem)
    {
        File.WriteAllText(path, pem);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return pem;
    }

    private static string CreateRsaPem()
    {
        using var rsa = RSA.Create(4096);
        return rsa.ExportPkcs8PrivateKeyPem();
    }

    private static string CreateEcPem()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return ec.ExportPkcs8PrivateKeyPem();
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
            await _server.DisposeAsync();
    }
}
