using MeetMateBot.Domain.Entities;

namespace MeetMateBot.Application.Interfaces;

public interface IProfileRepository
{
    Task<DatingProfile?> GetByTelegramChatIdAsync(long telegramChatId, CancellationToken cancellationToken);

    Task<IReadOnlyList<DatingProfile>> GetAllAsync(CancellationToken cancellationToken);

    Task SaveAsync(DatingProfile profile, CancellationToken cancellationToken);
}
