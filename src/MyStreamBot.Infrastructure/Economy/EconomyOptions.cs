namespace MyStreamBot.Infrastructure.Economy;

public sealed class EconomyOptions
{
    public const string SectionName = "Economy";

    /// <summary>
    /// Pontos concedidos por cada tipo de evento reconhecido do AxelChat.
    /// Valor 0 desabilita a recompensa daquele evento.
    /// </summary>
    public long MessageReward { get; set; } = 10;
    public long FollowReward { get; set; } = 100;
    public long SubscriptionReward { get; set; } = 500;
    public long GiftReward { get; set; } = 0;
    public long DonationReward { get; set; } = 0;

    /// <summary>
    /// Usernames that must never receive economy rewards.
    /// Comparison is case-insensitive and ignores leading/trailing spaces.
    /// </summary>
    public List<string> ExcludedUsernames { get; set; } = [];
}
