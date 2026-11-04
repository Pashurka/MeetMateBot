namespace MeetMateBot.Domain.Entities;

public sealed class ProfileReaction
{
    public long FromTelegramChatId { get; set; }

    public long ToTelegramChatId { get; set; }

    public bool IsLike { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
