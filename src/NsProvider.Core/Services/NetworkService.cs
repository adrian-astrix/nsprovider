using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NsProvider.Core.Storage;

namespace NsProvider.Core.Services;

/// <summary>
/// IPv4 da máquina: lista os IPs disponíveis pra receber servidores, mostra quantos
/// servidores usam cada um, e valida se um IP realmente pertence à máquina.
/// Um cliente com IPv4 dedicado (comprado na hospedagem) roda o servidor dele
/// naquele IP — inclusive a porta 7777, mesmo que outro cliente já use 7777 no IP principal.
/// Env NSP_IPS="ip1,ip2" força a lista (útil quando o motor roda atrás de NAT).
/// </summary>
public sealed class NetworkService(ServerRepository servers)
{
    public const string SharedIp = "0.0.0.0";

    /// <summary>IPs do motor + uso: quantos servidores e quais portas em cada IP.</summary>
    public object List()
    {
        var all = servers.ListAll();

        object Group(string ip, bool loopback) => new
        {
            ip,
            compartilhado = ip == SharedIp,
            loopback,
            servidores = all.Count(s => ServerRepository.NormalizeIp(s.BindIp) == (ip == SharedIp ? "" : ip)),
            portas = all
                .Where(s => ServerRepository.NormalizeIp(s.BindIp) == (ip == SharedIp ? "" : ip))
                .Select(s => s.Port).OrderBy(p => p).ToList(),
        };

        var groups = new List<object> { Group(SharedIp, false) };
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SharedIp };
        foreach (var ip in LocalIpv4())
        {
            known.Add(ip);
            groups.Add(Group(ip, IPAddress.IsLoopback(IPAddress.Parse(ip))));
        }

        // IPs usados por servidores mas não detectados na interface (ex.: adicionados depois do boot)
        foreach (var ip in all.Select(s => ServerRepository.NormalizeIp(s.BindIp))
                     .Where(ip => ip != "" && !known.Contains(ip)).Distinct())
        {
            groups.Add(Group(ip, IPAddress.IsLoopback(IPAddress.Parse(ip))));
        }

        return new
        {
            ips = groups,
            reservadas = ReservedPorts(),
            dica = "Ip dedicado: cria/edita o servidor com \"ip\": \"<ip da máquina>\" e \"port\": 7777 — " +
                   "a porta é única por IP, então vários clientes podem usar 7777 em IPs diferentes.",
        };
    }

    /// <summary>IPv4 locais da máquina (NSP_IPS sobrescreve).</summary>
    public static IReadOnlyList<string> LocalIpv4()
    {
        var forced = Environment.GetEnvironmentVariable("NSP_IPS");
        if (!string.IsNullOrWhiteSpace(forced))
            return forced.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(IsValidIpv4).ToList();

        var result = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Tunnel) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = ua.Address.ToString();
                    if (ip.StartsWith("169.254.")) continue; // link-local
                    if (!result.Contains(ip)) result.Add(ip);
                }
            }
        }
        catch { /* sem permissão p/ ler interfaces — lista vazia */ }
        return result;
    }

    /// <summary>O IP pertence mesmo a esta máquina? (tenta bindar UDP nele)</summary>
    public static bool IsLocalIp(string ip)
    {
        if (!IsValidIpv4(ip)) return false;
        try
        {
            using var udp = new UdpClient();
            udp.Client.Bind(new IPEndPoint(IPAddress.Parse(ip), 0));
            return true;
        }
        catch (SocketException) { return false; }
    }

    public static bool IsValidIpv4(string ip) =>
        IPAddress.TryParse(ip, out var addr) &&
        addr.AddressFamily == AddressFamily.InterNetwork &&
        addr.ToString() == ip.Trim();

    /// <summary>Portas que o motor usa e que o jogo não pode ocupar.</summary>
    public static IReadOnlyList<int> ReservedPorts()
    {
        var ports = new List<int> { 8383, 8821 };
        if (int.TryParse(Environment.GetEnvironmentVariable("NSP_SFTP_PORT"), out var sftp) && sftp > 0)
            ports.Add(sftp);
        if (Uri.TryCreate(Environment.GetEnvironmentVariable("NSP_API_URL") ?? "", UriKind.Absolute, out var uri)
            && uri.Port > 0)
            ports.Add(uri.Port);
        return ports.Distinct().ToList();
    }
}
