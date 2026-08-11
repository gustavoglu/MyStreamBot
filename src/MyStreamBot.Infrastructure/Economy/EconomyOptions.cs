namespace MyStreamBot.Infrastructure.Economy;

public sealed class EconomyOptions
{
    public const string SectionName = "Economy";

    /// <summary>
    /// Usernames that must never receive economy rewards.
    /// Comparison is case-insensitive and ignores leading/trailing spaces.
    /// </summary>
    public List<string> ExcludedUsernames { get; set; } = [];
}
