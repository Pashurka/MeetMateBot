using MeetMateBot.Domain.Entities;

namespace MeetMateBot.Application.Interfaces;

public interface IReactionRepository
{
    Task SaveAsync(ProfileReaction reaction, CancellationToken cancellationToken);

    Task<bool> HasLikeAsync(long fromTelegramChatId, long toTelegramChatId, CancellationToken cancellationToken);

    Task<IReadOnlySet<long>> GetReactedProfileIdsAsync(long fromTelegramChatId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProfileReaction>> GetByFromTelegramChatIdAsync(long fromTelegramChatId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProfileReaction>> GetAllAsync(CancellationToken cancellationToken);
}
