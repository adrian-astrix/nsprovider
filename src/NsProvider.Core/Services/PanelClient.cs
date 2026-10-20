using System.Text;
using System.Text.Json;

namespace NsProvider.Core.Services;

/// <summary>
/// Cliente do painel web (o motor é controlado por UM painel, estilo Pterodactyl wings).
///  - NSP_PANEL_URL  → URL base do painel (única origem autorizada a usar o motor)
///  - NSP_PANEL_TOKEN → key gerada pelo painel p/ este motor (também vai no header das chamadas)
/// O motor NÃO armazena senha de usuário: a validação de login SFTP é delegada ao painel.
/// </summary>
public sealed class PanelClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public static string? PanelUrl
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("NSP_PANEL_URL")?.Trim();
            return string.IsNullOrEmpty(raw) ? null : raw.TrimEnd('/');
        }
    }

    public static string? PanelToken =>
        Environment.GetEnvironmentVariable("NSP_PANEL_TOKEN") is { Length: > 0 } t ? t : null;

    /// <summary>True quando o motor está integrado a um painel.</summary>
    public bool Enabled => PanelUrl is not null;

    public sealed record SftpAuthResult(Guid UserId, string Name);

    /// <summary>
    /// Valida credenciais SFTP no painel:
    ///   POST {PANEL}/api/remote/sftp/auth  body {username, password, ip}
    ///   200 → {user_id, name}  |  401/403 → credenciais inválidas
    /// Retorna null se negado (ou sem painel configurado).
    /// </summary>
    public async Task<SftpAuthResult?> AuthenticateSftpAsync(string username, string password, string ip)
    {
        var url = PanelUrl;
        if (url is null) return null;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{url}/api/remote/sftp/auth");
            if (PanelToken is { } token)
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            // StringContent = body com Content-Length (JsonContent ia como chunked)
            req.Content = new StringContent(
                JsonSerializer.Serialize(new { username, password, ip }),
                System.Text.Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(req);
            if (resp.StatusCode != System.Net.HttpStatusCode.OK) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var userId = root.TryGetProperty("user_id", out var uid) && uid.ValueKind == JsonValueKind.String
                ? uid.GetString()
                : root.TryGetProperty("userId", out var uid2) ? uid2.GetString() : null;
            if (userId is null || !Guid.TryParse(userId, out var parsed)) return null;

            var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() : null;
            return new SftpAuthResult(parsed, name ?? username);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Console.WriteLine($"[motor] painel inacessível p/ auth SFTP: {ex.Message}");
            return null;
        }
    }
}
