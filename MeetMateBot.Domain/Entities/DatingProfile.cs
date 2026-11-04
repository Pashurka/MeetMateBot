namespace MeetMateBot.Domain.Entities;

public sealed class DatingProfile
{
    public long TelegramChatId { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Age { get; set; }

    public string Gender { get; set; } = string.Empty;

    public string LookingForGender { get; set; } = string.Empty;

    public string City { get; set; } = string.Empty;

    public List<string> Interests { get; set; } = [];

    public string Bio { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
