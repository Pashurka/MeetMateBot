using Google.Cloud.Firestore;
using MeetMateBot.Application.Interfaces;
using MeetMateBot.Domain.Entities;
using MeetMateBot.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeetMateBot.Infrastructure.Repositories;

public sealed class FirestoreProfileRepository : IProfileRepository
{
    private const string CollectionName = "profiles";

    private readonly Lazy<FirestoreDb?> _database;
    private readonly ILogger<FirestoreProfileRepository> _logger;
    private readonly FirestoreOptions _options;

    public FirestoreProfileRepository(
        IOptions<FirestoreOptions> options,
        ILogger<FirestoreProfileRepository> logger)
    {
        _options = options.Value;
        _logger = logger;
        _database = new Lazy<FirestoreDb?>(CreateDatabase);
    }

    public async Task<DatingProfile?> GetByTelegramChatIdAsync(long telegramChatId, CancellationToken cancellationToken)
    {
        var database = _database.Value;
        if (database is null)
        {
            return null;
        }

        var snapshot = await database
            .Collection(CollectionName)
            .Document(telegramChatId.ToString())
            .GetSnapshotAsync(cancellationToken);

        return snapshot.Exists ? MapProfile(snapshot, telegramChatId) : null;
    }

    public async Task<IReadOnlyList<DatingProfile>> GetAllAsync(CancellationToken cancellationToken)
    {
        var database = _database.Value;
        if (database is null)
        {
            return [];
        }

        var querySnapshot = await database
            .Collection(CollectionName)
            .GetSnapshotAsync(cancellationToken);

        return querySnapshot.Documents
            .Where(document => document.Exists)
            .Select(document => MapProfile(document))
            .ToList();
    }

    public async Task SaveAsync(DatingProfile profile, CancellationToken cancellationToken)
    {
        var database = _database.Value;
        if (database is null)
        {
            _logger.LogWarning("Firestore is not configured. Profile for chat {ChatId} was kept only in memory.", profile.TelegramChatId);
            return;
        }

        var now = DateTime.UtcNow;
        if (profile.CreatedAtUtc == default)
        {
            profile.CreatedAtUtc = now;
        }

        profile.UpdatedAtUtc = now;

        var data = new Dictionary<string, object>
        {
            ["telegramChatId"] = profile.TelegramChatId,
            ["name"] = profile.Name,
            ["age"] = profile.Age,
            ["gender"] = profile.Gender,
            ["lookingForGender"] = profile.LookingForGender,
            ["city"] = profile.City,
            ["interests"] = profile.Interests,
            ["bio"] = profile.Bio,
            ["createdAtUtc"] = Timestamp.FromDateTime(profile.CreatedAtUtc),
            ["updatedAtUtc"] = Timestamp.FromDateTime(profile.UpdatedAtUtc)
        };

        await database
            .Collection(CollectionName)
            .Document(profile.TelegramChatId.ToString())
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

    private static DatingProfile MapProfile(DocumentSnapshot snapshot, long? telegramChatId = null)
    {
        return new DatingProfile
        {
            TelegramChatId = telegramChatId ?? GetInt64(snapshot, "telegramChatId"),
            Name = GetString(snapshot, "name"),
            Age = snapshot.ContainsField("age") ? snapshot.GetValue<int>("age") : 0,
            Gender = GetString(snapshot, "gender"),
            LookingForGender = GetString(snapshot, "lookingForGender"),
            City = GetString(snapshot, "city"),
            Interests = snapshot.ContainsField("interests")
                ? snapshot.GetValue<List<string>>("interests")
                : [],
            Bio = GetString(snapshot, "bio"),
            CreatedAtUtc = GetDateTime(snapshot, "createdAtUtc"),
            UpdatedAtUtc = GetDateTime(snapshot, "updatedAtUtc")
        };
    }

    private static string GetString(DocumentSnapshot snapshot, string fieldName)
    {
        return snapshot.ContainsField(fieldName) ? snapshot.GetValue<string>(fieldName) : string.Empty;
    }

    private static long GetInt64(DocumentSnapshot snapshot, string fieldName)
    {
        return snapshot.ContainsField(fieldName) ? snapshot.GetValue<long>(fieldName) : 0;
    }

    private static DateTime GetDateTime(DocumentSnapshot snapshot, string fieldName)
    {
        return snapshot.ContainsField(fieldName)
            ? snapshot.GetValue<Timestamp>(fieldName).ToDateTime()
            : default;
    }
}
