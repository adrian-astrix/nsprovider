using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NsProvider.Core.Services;

/// <summary>
/// Cliente RCON do protocolo SA-MP/open.mp (UDP, opcode 'x').
/// Header legacy de 11 bytes: "SAMP" + IP(4) + porta(2, LITTLE-ENDIAN) + opcode.
/// Payload: ushort LE (len senha) + senha + ushort LE (len comando) + comando.
/// Resposta: header ecoado + ushort LE (len) + texto.
/// </summary>
public static class RconClient
{
    public static string? Send(string host, int port, string password, string command, int timeoutMs = 2000)
    {
        var ipBytes = Dns.GetHostAddresses(host).First(a => a.AddressFamily == AddressFamily.InterNetwork).GetAddressBytes();

        var pw = Encoding.ASCII.GetBytes(password);
        var cmd = Encoding.ASCII.GetBytes(command);
        var packet = new byte[11 + 2 + pw.Length + 2 + cmd.Length];
        var i = 0;
        packet[i++] = (byte)'S';
        packet[i++] = (byte)'A';
        packet[i++] = (byte)'M';
        packet[i++] = (byte)'P';
        Buffer.BlockCopy(ipBytes, 0, packet, i, 4);
        i += 4;
        packet[i++] = (byte)(port & 0xFF); // porta em LITTLE-ENDIAN
        packet[i++] = (byte)(port >> 8);
        packet[i++] = (byte)'x'; // opcode rcon
        packet[i++] = (byte)(pw.Length & 0xFF);
        packet[i++] = (byte)(pw.Length >> 8);
        Buffer.BlockCopy(pw, 0, packet, i, pw.Length);
        i += pw.Length;
        packet[i++] = (byte)(cmd.Length & 0xFF);
        packet[i++] = (byte)(cmd.Length >> 8);
        Buffer.BlockCopy(cmd, 0, packet, i, cmd.Length);

        using var udp = new UdpClient();
        udp.Client.ReceiveTimeout = timeoutMs;
        udp.Client.SendTimeout = timeoutMs;
        udp.Connect(host, port);
        udp.Send(packet, packet.Length);

        try
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);
            var data = udp.Receive(ref remote);
            if (data.Length < 13 || data[10] != (byte)'x')
                return null;
            var len = data[11] | (data[12] << 8);
            if (13 + len > data.Length) len = data.Length - 13;
            return Encoding.ASCII.GetString(data, 13, len);
        }
        catch (SocketException)
        {
            return null; // sem resposta dentro do timeout
        }
    }
}
