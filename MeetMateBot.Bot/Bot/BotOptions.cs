namespace MeetMateBot.Bot.Bot;

public sealed class BotOptions
{
    public const string SectionName = "TelegramBot";

    public string Token { get; init; } = string.Empty;
}
