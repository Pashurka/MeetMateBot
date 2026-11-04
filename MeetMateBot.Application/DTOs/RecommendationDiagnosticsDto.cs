namespace MeetMateBot.Application.DTOs;

public sealed record RecommendationDiagnosticsDto(
    int TotalReactions,
    int LikeCount,
    int SkipCount,
    IReadOnlyList<string> PreferredCities,
    IReadOnlyList<string> PreferredInterests,
    ProfileRecommendation? TopRecommendation);
