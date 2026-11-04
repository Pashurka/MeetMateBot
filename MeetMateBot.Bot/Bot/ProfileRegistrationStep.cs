namespace MeetMateBot.Bot.Bot;

public enum ProfileRegistrationStep
{
    None,
    WaitingForName,
    WaitingForAge,
    WaitingForGender,
    WaitingForLookingForGender,
    WaitingForCity,
    WaitingForInterests,
    WaitingForBio,
    EditingName,
    EditingAge,
    EditingCity,
    EditingInterests,
    EditingBio,
    Completed
}
