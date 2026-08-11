using System.Text;
using System.Text.Json;
using MyStreamBot.Application.Integrations.AxelChat;

namespace MyStreamBot.Infrastructure.AxelChat;

public sealed class ChatHistoryLogger
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private string LogsDirectory => Path.Combine(AppContext.BaseDirectory, "Logs");

    public async Task WriteMessageAsync(
        AxelChatMessage message,
        string result,
        long? pointsAwarded,
        long? balance,
        string? reason,
        CancellationToken cancellationToken = default,
        string? sourceMessageKey = null)
    {
        var now = DateTimeOffset.Now;

        var entry = new
        {
            TimestampUtc = now.UtcDateTime,
            TimestampLocal = now,
            Type = "CHAT_MESSAGE",
            Result = result,
            PointsAwarded = pointsAwarded,
            Balance = balance,
            Reason = reason,
            SourceMessageKey = sourceMessageKey,
            MessageId = message.MessageId,
            UserId = message.UserId,
            Username = message.Username,
            AvatarUrl = message.AvatarUrl,
            Platform = message.Platform.ToString(),
            Text = message.Text,
            PublishedAt = message.PublishedAt,
            Deleted = message.Deleted,
            EventType = message.EventType,
            RawJson = message.RawJson
        };

        await WriteAsync(entry, now, cancellationToken);
    }

    public async Task WriteEventAsync(
        string eventType,
        string rawJson,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.Now;

        var entry = new
        {
            TimestampUtc = now.UtcDateTime,
            TimestampLocal = now,
            Type = "AXELCHAT_EVENT",
            EventType = eventType,
            RawJson = rawJson
        };

        await WriteAsync(entry, now, cancellationToken);
    }

    public async Task WriteSystemAsync(
        string eventType,
        string message,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.Now;

        var entry = new
        {
            TimestampUtc = now.UtcDateTime,
            TimestampLocal = now,
            Type = "SYSTEM",
            EventType = eventType,
            Message = message
        };

        await WriteAsync(entry, now, cancellationToken);
    }

    private async Task WriteAsync(
        object entry,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(LogsDirectory);

        var fileName = $"axelchat-{timestamp:yyyy-MM-dd}.jsonl";
        var path = Path.Combine(LogsDirectory, fileName);
        var line = JsonSerializer.Serialize(entry, _jsonOptions) + Environment.NewLine;
        var bytes = Encoding.UTF8.GetBytes(line);

        await _lock.WaitAsync(cancellationToken);

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 8192,
                useAsync: true);

            await stream.WriteAsync(bytes, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }
}
