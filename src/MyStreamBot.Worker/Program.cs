using Microsoft.EntityFrameworkCore;
using MyStreamBot.Infrastructure;
using MyStreamBot.Infrastructure.Persistence;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

var builder = Host.CreateApplicationBuilder(args);

var dbPath = ResolveDatabasePath();

Directory.CreateDirectory(
    Path.GetDirectoryName(dbPath)!);

builder.Services.AddMyStreamBotInfrastructure(
    $"Data Source={dbPath}",
    builder.Configuration);

var host = builder.Build();

await EnsureDatabaseSchemaAsync(host.Services);

await host.RunAsync();

static string ResolveDatabasePath()
{
    var commonDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MyStreamBot");

    Directory.CreateDirectory(commonDirectory);

    var commonPath = Path.Combine(commonDirectory, "mystreambot.db");

    // Compatibilidade com as versões anteriores: se o banco antigo ainda
    // existir ao lado do Worker e o banco compartilhado não existir, copia-o
    // uma única vez para o local comum usado pelo Worker e pelo Overlay.
    var legacyPath = Path.Combine(
        AppContext.BaseDirectory,
        "data",
        "mystreambot.db");

    if (!File.Exists(commonPath) && File.Exists(legacyPath))
    {
        File.Copy(legacyPath, commonPath);
        Console.WriteLine($"Banco existente migrado para: {commonPath}");
    }

    Console.WriteLine($"Banco SQLite: {commonPath}");

    return commonPath;
}

static async Task EnsureDatabaseSchemaAsync(
    IServiceProvider services)
{
    await using var scope =
        services.CreateAsyncScope();

    var db = scope.ServiceProvider
        .GetRequiredService<MyStreamBotDbContext>();

    await db.Database.EnsureCreatedAsync();

    var connection = db.Database.GetDbConnection();

    await connection.OpenAsync();

    await EnsureColumnAsync(
        connection,
        "Users",
        "AvatarUrl",
        "TEXT NULL");

    await EnsureColumnAsync(
        connection,
        "Users",
        "LastChatMessageNormalized",
        "TEXT NULL");

    await EnsureColumnAsync(
        connection,
        "PointTransactions",
        "SourceMessageKey",
        "TEXT NULL");

    await using (var command = connection.CreateCommand())
    {
        command.CommandText =
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_PointTransactions_SourceMessageKey " +
            "ON PointTransactions(SourceMessageKey) " +
            "WHERE SourceMessageKey IS NOT NULL;";

        await command.ExecuteNonQueryAsync();
    }
}

static async Task EnsureColumnAsync(
    System.Data.Common.DbConnection connection,
    string tableName,
    string columnName,
    string columnDefinition)
{
    var exists = false;

    await using (var command = connection.CreateCommand())
    {
        command.CommandText = $"PRAGMA table_info(\"{tableName}\");";

        await using var reader =
            await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var currentName = reader.GetString(1);

            if (string.Equals(
                    currentName,
                    columnName,
                    StringComparison.OrdinalIgnoreCase))
            {
                exists = true;
                break;
            }
        }
    }

    if (exists)
        return;

    await using (var command = connection.CreateCommand())
    {
        command.CommandText =
            $"ALTER TABLE \"{tableName}\" ADD COLUMN \"{columnName}\" {columnDefinition};";

        await command.ExecuteNonQueryAsync();
    }

    Console.WriteLine(
        $"Banco atualizado: coluna {tableName}.{columnName} adicionada.");
}
