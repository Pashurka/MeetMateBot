using Google.Cloud.Firestore;
using MeetMateBot.Application.Interfaces;
using MeetMateBot.Domain.Entities;
using MeetMateBot.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeetMateBot.Infrastructure.Repositories;

public sealed class FirestoreReactionRepository : IReactionRepository
{
    private const string CollectionName = "reactions";

    private readonly Lazy<FirestoreDb?> _database;
    private readonly ILogger<FirestoreReactionRepository> _logger;
    private readonly FirestoreOptions _options;

    public FirestoreReactionRepository(
        IOptions<FirestoreOptions> options,
        ILogger<FirestoreReactionRepository> logger)
    {
        _options = options.Value;
        _logger = logger;
        _database = new Lazy<FirestoreDb?>(CreateDatabase);
    }

    public async Task SaveAsync(ProfileReaction reaction, CancellationToken cancellationToken)
    {
        var database = _database.Value;
        if (database is null)
        {
            _logger.LogWarning("Firestore is not configured. Reaction from {FromId} to {ToId} was not persisted.", reaction.FromTelegramChatId, reaction.ToTelegramChatId);
            return;
        }

        if (reaction.CreatedAtUtc == default)
        {
            reaction.CreatedAtUtc = DateTime.UtcNow;
        }

        var data = new Dictionary<string, object>
        {
            ["fromTelegramChatId"] = reaction.FromTelegramChatId,
            ["toTelegramChatId"] = reaction.ToTelegramChatId,
            ["isLike"] = reaction.IsLike,
            ["createdAtUtc"] = Timestamp.FromDateTime(reaction.CreatedAtUtc)
        };

        await database
            .Collection(CollectionName)
            .Document(GetReactionId(reaction.FromTelegramChatId, reaction.ToTelegramChatId))
            .SetAsync(data, SetOptions.MergeAll, cancellationToken);
    }

    public async Task<bool> HasLikeAsync(long fromTelegramChatId, long toTelegramChatId, CancellationToken cancellationToken)
    {
        var database = _database.Value;
        if (database is null)
        {
            return false;
        }

        var snapshot = await database
            .Collection(CollectionName)
            .Document(GetReactionId(fromTelegramChatId, toTelegramChatId))
            .GetSnapshotAsync(cancellationToken);

        return snapshot.Exists
            && snapshot.ContainsField("isLike")
            && snapshot.GetValue<bool>("isLike");
    }

    public async Task<IReadOnlySet<long>> GetReactedProfileIdsAsync(long fromTelegramChatId, CancellationToken cancellationToken)
    {
        var database = _database.Value;
        if (database is null)
        {
            return new HashSet<long>();
        }

        var snapshot = await database
            .Collection(CollectionName)
            .WhereEqualTo("fromTelegramChatId", fromTelegramChatId)
            .GetSnapshotAsync(cancellationToken);

        return snapshot.Documents
            .Where(document => document.Exists && document.ContainsField("toTelegramChatId"))
            .Select(document => document.GetValue<long>("toTelegramChatId"))
            .ToHashSet();
    }

    public async Task<IReadOnlyList<ProfileReaction>> GetByFromTelegramChatIdAsync(long fromTelegramChatId, CancellationToken cancellationToken)
    {
        var database = _database.Value;
        if (database is null)
        {
            return [];
        }

        var snapshot = await database
            .Collection(CollectionName)
            .WhereEqualTo("fromTelegramChatId", fromTelegramChatId)
            .GetSnapshotAsync(cancellationToken);

        return snapshot.Documents
            .Where(document => document.Exists)
            .Select(MapReaction)
            .OrderByDescending(reaction => reaction.CreatedAtUtc)
            .ToList();
    }

    public async Task<IReadOnlyList<ProfileReaction>> GetAllAsync(CancellationToken cancellationToken)
    {
        var database = _database.Value;
        if (database is null)
        {
            return [];
        }

        var snapshot = await database
            .Collection(CollectionName)
            .GetSnapshotAsync(cancellationToken);

        return snapshot.Documents
            .Where(document => document.Exists)
            .Select(MapReaction)
            .OrderByDescending(reaction => reaction.CreatedAtUtc)
            .ToList();
    }

    private FirestoreDb? CreateDatabase()
    {
        if (!_options.IsConfigured)
        {
            _logger.LogWarning("Firestore settings are not configured. Fill Firestore:ProjectId and Firestore:CredentialsPath in appsettings.json.");
            return null;
        }

        var credentialsPath = FirestoreCredentialsPathResolver.Resolve(_options.CredentialsPath);
        if (credentialsPath is null)
        {
            _logger.LogWarning(
                "Firestore credentials file was not found. Configured path: {CredentialsPath}.",
                _options.CredentialsPath);
            return null;
        }

        Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", credentialsPath);
        return FirestoreDb.Create(_options.ProjectId);
    }

    private static string GetReactionId(long fromTelegramChatId, long toTelegramChatId)
    {
        return $"{fromTelegramChatId}_{toTelegramChatId}";
    }

    private static ProfileReaction MapReaction(DocumentSnapshot snapshot)
    {
        return new ProfileReaction
        {
            FromTelegramChatId = snapshot.ContainsField("fromTelegramChatId") ? snapshot.GetValue<long>("fromTelegramChatId") : 0,
            ToTelegramChatId = snapshot.ContainsField("toTelegramChatId") ? snapshot.GetValue<long>("toTelegramChatId") : 0,
            IsLike = snapshot.ContainsField("isLike") && snapshot.GetValue<bool>("isLike"),
            CreatedAtUtc = snapshot.ContainsField("createdAtUtc") ? snapshot.GetValue<Timestamp>("createdAtUtc").ToDateTime() : default
        };
    }
}
