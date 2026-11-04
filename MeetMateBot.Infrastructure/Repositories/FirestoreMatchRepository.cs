using Google.Cloud.Firestore;
using MeetMateBot.Application.Interfaces;
using MeetMateBot.Domain.Entities;
using MeetMateBot.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeetMateBot.Infrastructure.Repositories;

public sealed class FirestoreMatchRepository : IMatchRepository
{
    private const string CollectionName = "matches";

    private readonly Lazy<FirestoreDb?> _database;
    private readonly ILogger<FirestoreMatchRepository> _logger;
    private readonly FirestoreOptions _options;

    public FirestoreMatchRepository(
        IOptions<FirestoreOptions> options,
        ILogger<FirestoreMatchRepository> logger)
    {
        _options = options.Value;
        _logger = logger;
        _database = new Lazy<FirestoreDb?>(CreateDatabase);
    }

    public async Task<IReadOnlyList<ProfileMatch>> GetByTelegramChatIdAsync(long telegramChatId, CancellationToken cancellationToken)
    {
        var database = _database.Value;
        if (database is null)
        {
            return [];
        }

        var firstSnapshot = await database
            .Collection(CollectionName)
            .WhereEqualTo("firstTelegramChatId", telegramChatId)
            .WhereEqualTo("isActive", true)
            .GetSnapshotAsync(cancellationToken);

        var secondSnapshot = await database
            .Collection(CollectionName)
            .WhereEqualTo("secondTelegramChatId", telegramChatId)
            .WhereEqualTo("isActive", true)
            .GetSnapshotAsync(cancellationToken);

        return firstSnapshot.Documents
            .Concat(secondSnapshot.Documents)
            .Where(document => document.Exists)
            .Select(MapMatch)
            .ToList();
    }

    public async Task<IReadOnlyList<ProfileMatch>> GetAllAsync(CancellationToken cancellationToken)
    {
        var database = _database.Value;
        if (database is null)
        {
            return [];
        }

        var snapshot = await database
            .Collection(CollectionName)
            .WhereEqualTo("isActive", true)
            .GetSnapshotAsync(cancellationToken);

        return snapshot.Documents
            .Where(document => document.Exists)
            .Select(MapMatch)
            .ToList();
    }

    public async Task SaveAsync(ProfileMatch match, CancellationToken cancellationToken)
    {
        var database = _database.Value;
        if (database is null)
        {
            _logger.LogWarning("Firestore is not configured. Match between {FirstId} and {SecondId} was not persisted.", match.FirstTelegramChatId, match.SecondTelegramChatId);
            return;
        }

        if (match.CreatedAtUtc == default)
        {
            match.CreatedAtUtc = DateTime.UtcNow;
        }

        var firstId = Math.Min(match.FirstTelegramChatId, match.SecondTelegramChatId);
        var secondId = Math.Max(match.FirstTelegramChatId, match.SecondTelegramChatId);

        var data = new Dictionary<string, object>
        {
            ["firstTelegramChatId"] = firstId,
            ["secondTelegramChatId"] = secondId,
            ["createdAtUtc"] = Timestamp.FromDateTime(match.CreatedAtUtc),
            ["isActive"] = match.IsActive
        };

        await database
            .Collection(CollectionName)
            .Document(GetMatchId(firstId, secondId))
            .SetAsync(data, SetOptions.MergeAll, cancellationToken);
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

    private static ProfileMatch MapMatch(DocumentSnapshot snapshot)
    {
        return new ProfileMatch
        {
            FirstTelegramChatId = snapshot.ContainsField("firstTelegramChatId") ? snapshot.GetValue<long>("firstTelegramChatId") : 0,
            SecondTelegramChatId = snapshot.ContainsField("secondTelegramChatId") ? snapshot.GetValue<long>("secondTelegramChatId") : 0,
            CreatedAtUtc = snapshot.ContainsField("createdAtUtc") ? snapshot.GetValue<Timestamp>("createdAtUtc").ToDateTime() : default,
            IsActive = !snapshot.ContainsField("isActive") || snapshot.GetValue<bool>("isActive")
        };
    }

    private static string GetMatchId(long firstTelegramChatId, long secondTelegramChatId)
    {
        return $"{firstTelegramChatId}_{secondTelegramChatId}";
    }
}
