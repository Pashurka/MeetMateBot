namespace MeetMateBot.Domain.Entities;

public sealed class ProfileMatch
{
    public long FirstTelegramChatId { get; set; }

    public long SecondTelegramChatId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public bool IsActive { get; set; } = true;
}
