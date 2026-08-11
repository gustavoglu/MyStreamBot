using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyStreamBot.Application.Abstractions;
using MyStreamBot.Application.Integrations.AxelChat;
using MyStreamBot.Application.Services;
using MyStreamBot.Infrastructure.AxelChat;
using Microsoft.Extensions.Configuration;
using MyStreamBot.Infrastructure.Persistence;
using MyStreamBot.Infrastructure.Repositories;
namespace MyStreamBot.Infrastructure;
public static class DependencyInjection
{
    public static IServiceCollection AddMyStreamBotInfrastructure(
        this IServiceCollection services,
        string connectionString,
        IConfiguration configuration)
    {
        services.AddDbContext<MyStreamBotDbContext>(o => o.UseSqlite(connectionString));
        services.AddScoped<IUserRepository, SqliteUserRepository>();
        services.AddScoped<EconomyService>();
        services.AddSingleton<ChatHistoryLogger>();

        services.Configure<AxelChatOptions>(
            configuration.GetSection(AxelChatOptions.SectionName));
        services.AddSingleton<IAxelChatClient, AxelChatWebSocketClient>();
        services.AddHostedService<AxelChatHostedService>();

        return services;
    }
}
