sealed record CachedValue<T>(T Value)
{
    public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;
    public bool IsValid(TimeSpan lifetime) => DateTime.UtcNow - CreatedAtUtc < lifetime;
}

sealed record TopUserDto(long Id, string Username, string? AvatarUrl, long Points);

sealed record RecentItemDto(
    long Id,
    long Amount,
    string Type,
    string? Description,
    DateTime CreatedAtUtc,
    RecentUserDto? User);

sealed record RecentUserDto(string Username, string? AvatarUrl, long Points);