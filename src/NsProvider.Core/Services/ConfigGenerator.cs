using System.Text.Json.Nodes;

namespace NsProvider.Core.Services;

/// <summary>
/// Gera a config de cada servidor a partir do estado no banco:
///   SA-MP  → server.cfg (texto "chave valor")
///   open.mp→ config.json (patch do config.json do template)
/// Slots = maxplayers / max_players.
/// </summary>
public static class ConfigGenerator
{
    public static void Write(GameServer s, string serverDir)
    {
        if (s.Type == ServerType.Samp)
            WriteSamp(s, serverDir);
        else
            WriteOpenMp(s, serverDir);
    }

    private static void WriteSamp(GameServer s, string serverDir)
    {
        var cfg = $"""
            echo Executing Server Config...
            lanmode 0
            rcon_password {s.RconPassword}
            maxplayers {s.MaxPlayers}
            port {s.Port}
            hostname {s.Hostname}
            gamemode0 {s.Gamemode} 1
            filterscripts
            announce {(s.Announce ? 1 : 0)}
            query 1
            weburl nsprovider.local
            maxnpc 0
            onfoot_rate 40
            incar_rate 40
            weapon_rate 40
            stream_distance 300.0
            stream_rate 1000
            """;
        File.WriteAllText(Path.Combine(serverDir, "server.cfg"), cfg + Environment.NewLine);
    }

    private static void WriteOpenMp(GameServer s, string serverDir)
    {
        var configPath = Path.Combine(serverDir, "config.json");
        JsonNode root;

        if (File.Exists(configPath))
        {
            root = JsonNode.Parse(File.ReadAllText(configPath))
                   ?? throw new InvalidOperationException("config.json do template é inválido.");
        }
        else
        {
            // Template sem config.json → mínimo funcional do open.mp.
            root = new JsonObject
            {
                ["announce"] = s.Announce,
                ["enable_query"] = true,
                ["language"] = "",
                ["max_bots"] = 0,
                ["max_players"] = s.MaxPlayers,
                ["name"] = s.Hostname,
                ["password"] = "",
                ["website"] = "nsprovider.local",
                ["game"] = new JsonObject(),
                ["network"] = new JsonObject(),
                ["pawn"] = new JsonObject(),
                ["rcon"] = new JsonObject(),
            };
        }

        root["name"] = s.Hostname;
        root["max_players"] = s.MaxPlayers;
        root["announce"] = s.Announce;

        var network = root["network"] ??= new JsonObject();
        network["port"] = s.Port;

        // artwork (skins custom) usa a mesma porta por padrão
        if (root["artwork"] is JsonObject artwork)
            artwork["port"] = s.Port;

        var rcon = root["rcon"] ??= new JsonObject();
        rcon["password"] = s.RconPassword;
        rcon["enable"] = true;

        var pawn = root["pawn"] ??= new JsonObject();
        pawn["main_scripts"] = new JsonArray($"{s.Gamemode} 1");

        File.WriteAllText(configPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
        }));
    }

    /// <summary>Lista os gamemodes (.amx) disponíveis na pasta do servidor.</summary>
    public static IReadOnlyList<string> ListGamemodes(ServerType type, string serverDir)
    {
        var dir = Path.Combine(serverDir, "gamemodes");
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir, "*.amx")
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(n => n)
            .ToList();
    }
}
