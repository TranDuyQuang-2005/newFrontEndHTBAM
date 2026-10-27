using HTBAM.Api.Hubs;
using HTBAM.Application.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace HTBAM.Api.Support;

public sealed class SessionFinalizationWorker(
    IServiceScopeFactory scopeFactory,
    IHubContext<SessionHub> hub,
    ILogger<SessionFinalizationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();
                var completed = await sessions.ProcessPendingFinalizationsAsync(stoppingToken);
                foreach (var sessionId in completed)
                    await hub.Clients.Group($"session:{sessionId}").SendAsync(
                        "sessionStatus", new { sessionId, status = "COMPLETED" }, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background session finalization poll failed");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }
}
