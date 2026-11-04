using MeetMateBot.Domain.Entities;

namespace MeetMateBot.Application.DTOs;

public sealed record ProfileRecommendation(
    DatingProfile CurrentProfile,
    DatingProfile RecommendedProfile,
    int Score,
    int CompatibilityPercent,
    int MachineLearningScore,
    int TrainingSampleCount,
    IReadOnlyList<string> CommonInterests,
    bool IsSameCity,
    int AgeDifference,
    int CityScore,
    int InterestScore,
    int AgeScore,
    int CompletenessScore,
    bool IsProfileRecentlyUpdated);

