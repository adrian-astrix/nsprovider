using System.Security.Cryptography;
using NsProvider.Api;
using NsProvider.Core;
using NsProvider.Core.Services;
using NsProvider.Core.Storage;
using NsProvider.Sftp;

var builder = WebApplication.CreateBuilder(args);

// API interna: http://127.0.0.1:8383 (configurável via NSP_API_URL)
// OBS: não usar 8080 — costuma ser o painel Pterodactyl na mesma máquina.
builder.WebHost.UseUrls(
    Environment.GetEnvironmentVariable("NSP_API_URL") ?? "http://127.0.0.1:8383");

builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<UserRepository>();
builder.Services.AddSingleton<ServerRepository>();
builder.Services.AddSingleton<BackupRepository>();
builder.Services.AddSingleton<PortAllocator>();
builder.Services.AddSingleton<NetworkService>();
builder.Services.AddSingleton<ServerEventHub>();
builder.Services.AddSingleton<ConsoleTailService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ConsoleTailService>());
builder.Services.AddSingleton<TemplateManager>();
builder.Services.AddSingleton<ProcessManager>();
builder.Services.AddSingleton<ServerManager>();
builder.Services.AddSingleton<PanelClient>();
builder.Services.AddSingleton<SftpHost>();
builder.Services.AddSingleton<CgroupManager>();
builder.Services.AddSingleton<DiskQuotaService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DiskQuotaService>());
builder.Services.AddSingleton<ServerUsageService>();
builder.Services.AddSingleton<SystemStatsService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SystemStatsService>());
builder.Services.AddSingleton<BackupService>();

// CORS: só a origem do painel web (NSP_PANEL_URL) pode chamar a API do browser.
// Sem painel configurado → aberto (modo dev/local).
var panelUrl = PanelClient.PanelUrl;
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (!string.IsNullOrEmpty(panelUrl))
    {
        var origin = Uri.TryCreate(panelUrl, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority) : panelUrl;
        p.WithOrigins(origin).AllowAnyHeader().AllowAnyMethod();
        Console.WriteLine($"[motor] origem autorizada: {origin}");
    }
    else
    {
        p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    }
}));

// cgroups v2: slice do motor + limpeza de grupos vazios
var cgroups = new CgroupManager();
cgroups.PrepareSlice();
cgroups.CleanupEmpty();

// Uploads de templates (pacotes de até 1GB)
builder.WebHost.ConfigureKestrel(o =>
    o.Limits.MaxRequestBodySize = 1_073_741_824);

var app = builder.Build();

// Reaper: mata servidores órfãos de execuções anteriores do motor
ProcessManager.CleanupOrphanProcesses();

app.UseCors();

// Token do painel (NSP_PANEL_TOKEN, fallback NSP_API_KEY) — protege toda a /api.
// A key é gerada e controlada pelo painel web; /health fica aberto pra monitoração.
var panelToken = PanelClient.PanelToken ?? Environment.GetEnvironmentVariable("NSP_API_KEY");
if (!string.IsNullOrEmpty(panelToken))
{
    var expectedBytes = System.Text.Encoding.UTF8.GetBytes(panelToken);
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/health"))
        {
            await next();
            return;
        }
        string? given = ctx.Request.Headers["X-Api-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(given))
        {
            var auth = ctx.Request.Headers["Authorization"].FirstOrDefault();
            if (auth?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
                given = auth["Bearer ".Length..].Trim();
        }
        // EventSource do browser não aceita headers custom → token também via
        // query "?access_token=..." (CORS já limita a origem ao painel)
        if (string.IsNullOrEmpty(given))
            given = ctx.Request.Query["access_token"].FirstOrDefault();
        var givenBytes = System.Text.Encoding.UTF8.GetBytes(given ?? "");
        if (!CryptographicOperations.FixedTimeEquals(expectedBytes, givenBytes))
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { error = "Token do painel inválido ou ausente." });
            return;
        }
        await next();
    });
}
else
{
    Console.WriteLine("[motor] AVISO: sem NSP_PANEL_TOKEN/NSP_API_KEY — API sem autenticação (modo dev)");
}

app.MapGet("/health", () => Results.Ok(new { status = "ok", motor = "nsprovider", versao = "0.4.1" }));

Endpoints.MapUsers(app);
Endpoints.MapServers(app);
Endpoints.MapFiles(app);
Endpoints.MapTemplates(app);
Endpoints.MapStats(app);
Endpoints.MapBackups(app);
Endpoints.MapNetwork(app);
Endpoints.MapEvents(app);

// SFTP embutido: sobe junto com a API e desce no shutdown
var sftp = app.Services.GetRequiredService<SftpHost>();
var lifetime = app.Lifetime;
lifetime.ApplicationStarted.Register(() =>
{
    try { sftp.Start(); }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[motor] ERRO ao iniciar SFTP: {ex.Message}");
        lifetime.StopApplication();
    }
});
lifetime.ApplicationStopping.Register(() => sftp.DisposeAsync().AsTask().Wait());

app.Run();
