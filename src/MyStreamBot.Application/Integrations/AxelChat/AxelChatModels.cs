using MyStreamBot.Core.Enums;

namespace MyStreamBot.Application.Integrations.AxelChat;

public sealed record AxelChatMessage(
    string MessageId,
    string UserId,
    string Username,
    string? AvatarUrl,
    Platform Platform,
    string Text,
    DateTime? PublishedAt,
    bool Deleted,
    string EventType,
    string RawJson);
public sealed record AxelChatServiceState(
    string TypeId,
    string ConnectionState,
    bool Enabled,
    int Viewers,
    int Followers);

public sealed record AxelChatStateChanged(
    IReadOnlyList<AxelChatServiceState> Services,
    int Viewers);

public sealed record AxelChatEvent(
    string Type,
    IReadOnlyList<AxelChatMessage> Messages,
    AxelChatStateChanged? State,
    string RawJson);
