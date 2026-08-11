using Microsoft.Extensions.Hosting;

namespace MyStreamBot.Worker;
public sealed class Worker(ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("MyStreamBot iniciado em {Time}", DateTimeOffset.Now);
        while (!stoppingToken.IsCancellationRequested) await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
    }
}
