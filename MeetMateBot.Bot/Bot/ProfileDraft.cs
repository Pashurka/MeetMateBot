namespace MeetMateBot.Bot.Bot;

public sealed class ProfileDraft
{
    public ProfileRegistrationStep Step { get; set; } = ProfileRegistrationStep.None;

    public string? Name { get; set; }

    public int? Age { get; set; }

    public string? Gender { get; set; }

    public string? LookingForGender { get; set; }

    public string? City { get; set; }

    public List<string> Interests { get; } = [];

    public string? Bio { get; set; }
}
