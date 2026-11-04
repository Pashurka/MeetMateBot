using MeetMateBot.Domain.Entities;

namespace MeetMateBot.Application.Interfaces;

public interface IMatchRepository
{
    Task<IReadOnlyList<ProfileMatch>> GetByTelegramChatIdAsync(long telegramChatId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProfileMatch>> GetAllAsync(CancellationToken cancellationToken);

    Task SaveAsync(ProfileMatch match, CancellationToken cancellationToken);
}
