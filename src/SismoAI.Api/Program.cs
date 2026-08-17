using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.HttpOverrides;
using System.Collections.Generic;
using SismoAI.Analytics;
using SismoAI.Application;
using SismoAI.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
var renderPort = Environment.GetEnvironmentVariable("PORT");
var useHttpsRedirection = builder.Configuration.GetValue<bool?>("App:UseHttpsRedirection")
    ?? string.IsNullOrWhiteSpace(renderPort);
var configuredOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?.Where(origin => !string.IsNullOrWhiteSpace(origin))
    .Select(CorsOriginHelper.NormalizeOrigin)
    .ToArray()
    ?? [];
var allowedOrigins = new HashSet<string>(
    configuredOrigins.Concat(new[]
    {
        "http://localhost:5173",
        "https://sismo-five.vercel.app"
    }.Select(CorsOriginHelper.NormalizeOrigin)),
    StringComparer.OrdinalIgnoreCase);

if (!string.IsNullOrWhiteSpace(renderPort))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{renderPort}");
}

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSignalR();
builder.Services.AddCors(options =>
{
    options.AddPolicy("frontend", policy =>
    {
        policy
            .SetIsOriginAllowed(origin => CorsOriginHelper.IsAllowedOrigin(origin, allowedOrigins))
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

builder.Services.AddSingleton<IAnalyticsEngine, StatisticalAnalyticsEngine>();
builder.Services.AddSingleton<IRealtimeNotifier, SignalRRealtimeNotifier>();
builder.Services.AddSismoInfrastructure(builder.Configuration);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<SismoDbContext>();
    await dbContext.Database.EnsureCreatedAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

app.UseCors("frontend");
if (useHttpsRedirection)
{
    app.UseHttpsRedirection();
}
app.UseAuthorization();

app.MapControllers();
app.MapHub<DashboardHub>("/hubs/dashboard");
app.MapGet("/health", () => Results.Ok(new { status = "ok", timestampUtc = DateTimeOffset.UtcNow }));

app.Run();

public partial class Program;

public sealed class DashboardHub : Hub;

public sealed class SignalRRealtimeNotifier(IHubContext<DashboardHub> hubContext) : IRealtimeNotifier
{
    public Task PublishDashboardAsync(DashboardSnapshotDto snapshot, CancellationToken cancellationToken)
    {
        return hubContext.Clients.All.SendAsync("dashboardUpdated", snapshot, cancellationToken);
    }
}

internal static class CorsOriginHelper
{
    public static string NormalizeOrigin(string origin)
    {
        return origin.Trim().TrimEnd('/');
    }

    public static bool IsAllowedOrigin(string origin, HashSet<string> allowedOrigins)
    {
        var normalized = NormalizeOrigin(origin);
        if (allowedOrigins.Contains(normalized))
        {
            return true;
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttps
            && uri.Host.EndsWith(".vercel.app", StringComparison.OrdinalIgnoreCase);
    }
}
