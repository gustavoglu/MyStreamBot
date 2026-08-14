using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyStreamBot.Application.Abstractions;
using MyStreamBot.Application.Integrations.AxelChat;
using MyStreamBot.Application.Services;
using MyStreamBot.Infrastructure.AxelChat;
using MyStreamBot.Infrastructure.Economy;
using MyStreamBot.Infrastructure.Persistence;
using MyStreamBot.Infrastructure.Repositories;
using MyStreamBot.Infrastructure.Tts;

namespace MyStreamBot.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddMyStreamBotInfrastructure(
        this IServiceCollection services,
        string connectionString,
        IConfiguration configuration)
    {
        services.AddDbContext<MyStreamBotDbContext>(
            o => o.UseSqlite(connectionString));

        services.AddScoped<IUserRepository, SqliteUserRepository>();
        services.AddScoped<EconomyService>();

        services.AddSingleton<ChatHistoryLogger>();

        services.Configure<EconomyOptions>(
            configuration.GetSection(EconomyOptions.SectionName));

        services.Configure<AxelChatOptions>(
            configuration.GetSection(AxelChatOptions.SectionName));

        services.Configure<ElevenLabsOptions>(
            configuration.GetSection(ElevenLabsOptions.SectionName));

        services.AddHttpClient<ITtsService, ElevenLabsTtsService>((serviceProvider, client) =>
        {
            var options = serviceProvider
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<ElevenLabsOptions>>()
                .Value;

            client.BaseAddress = new Uri(options.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });

        services.AddSingleton<IAxelChatClient, AxelChatWebSocketClient>();
        services.AddHostedService<AxelChatHostedService>();

        return services;
    }
}
