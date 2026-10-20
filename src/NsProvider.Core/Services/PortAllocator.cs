using System.Net.Sockets;
using NsProvider.Core.Storage;

namespace NsProvider.Core.Services;

/// <summary>
/// Aloca portas UDP do jogo a partir de uma base (default 7777).
/// Pula portas já registradas no banco e portas ocupadas no SO.
/// </summary>
public sealed class PortAllocator(ServerRepository servers)
{
    public const int BasePort = 7777;

    public int Allocate()
    {
        var used = servers.ListAll().Select(s => s.Port).ToHashSet();
        for (var port = BasePort; port < BasePort + 1000; port++)
        {
            if (used.Contains(port)) continue;
            if (IsUdpPortFree(port)) return port;
        }
        throw new InvalidOperationException("Nenhuma porta UDP livre encontrada na faixa 7777-8776.");
    }

    private static bool IsUdpPortFree(int port)
    {
        try
        {
            using var udp = new UdpClient();
            udp.Client.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
