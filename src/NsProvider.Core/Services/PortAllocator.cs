using System.Net;
using System.Net.Sockets;
using NsProvider.Core.Storage;

namespace NsProvider.Core.Services;

/// <summary>
/// Aloca portas UDP do jogo a partir de uma base (default 7777) POR IP.
/// Dois servidores podem usar 7777 em IPs diferentes (IP dedicado por cliente);
/// no mesmo IP, a porta é única. Pula portas registradas no banco e ocupadas no SO.
/// </summary>
public sealed class PortAllocator(ServerRepository servers)
{
    public const int BasePort = 7777;

    public int Allocate(string? bindIp = null)
    {
        var ip = ServerRepository.NormalizeIp(bindIp);
        var used = servers.ListAll()
            .Where(s => ServerRepository.NormalizeIp(s.BindIp) == ip)
            .Select(s => s.Port).ToHashSet();

        for (var port = BasePort; port < BasePort + 1000; port++)
        {
            if (used.Contains(port)) continue;
            if (IsUdpPortFree(ip, port)) return port;
        }
        throw new InvalidOperationException("Nenhuma porta UDP livre encontrada na faixa 7777-8776.");
    }

    /// <summary>Porta pedida explicitamente está livre naquele IP?</summary>
    public bool IsPortAvailable(string? bindIp, int port)
    {
        var ip = ServerRepository.NormalizeIp(bindIp);
        var dbFree = !servers.ListAll()
            .Any(s => ServerRepository.NormalizeIp(s.BindIp) == ip && s.Port == port);
        return dbFree && IsUdpPortFree(ip, port);
    }

    private static bool IsUdpPortFree(string ip, int port)
    {
        try
        {
            using var udp = new UdpClient();
            var addr = ip == "" ? IPAddress.Any : IPAddress.Parse(ip);
            udp.Client.Bind(new IPEndPoint(addr, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
