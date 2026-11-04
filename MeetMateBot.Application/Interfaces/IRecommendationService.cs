using MeetMateBot.Application.DTOs;
using MeetMateBot.Domain.Entities;

namespace MeetMateBot.Application.Interfaces;

public interface IRecommendationService
{
    Task<ProfileRecommendation?> GetBestRecommendationAsync(long telegramChatId, CancellationToken cancellationToken);

    Task<RecommendationDiagnosticsDto?> GetRecommendationDiagnosticsAsync(long telegramChatId, CancellationToken cancellationToken);

    bool IsGenderCompatible(DatingProfile currentProfile, DatingProfile candidate);

    int CalculateScore(DatingProfile currentProfile, DatingProfile candidate);
}
