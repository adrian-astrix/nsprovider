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
builder.Services.AddSingleton<PortAllocator>();
builder.Services.AddSingleton<TemplateManager>();
builder.Services.AddSingleton<ProcessManager>();
builder.Services.AddSingleton<ServerManager>();
builder.Services.AddSingleton<SftpHost>();
builder.Services.AddSingleton<CgroupManager>();
builder.Services.AddSingleton<DiskQuotaService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DiskQuotaService>());

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

// API key opcional (env NSP_API_KEY) — protege a API quando exposta
var apiKey = Environment.GetEnvironmentVariable("NSP_API_KEY");
if (!string.IsNullOrEmpty(apiKey))
{
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/health"))
        {
            await next();
            return;
        }
        if (!ctx.Request.Headers.TryGetValue("X-Api-Key", out var given) || given != apiKey)
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { error = "API key inválida." });
            return;
        }
        await next();
    });
}

app.MapGet("/health", () => Results.Ok(new { status = "ok", motor = "nsprovider", versao = "0.1.0" }));

Endpoints.MapUsers(app);
Endpoints.MapServers(app);
Endpoints.MapFiles(app);
Endpoints.MapTemplates(app);

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
