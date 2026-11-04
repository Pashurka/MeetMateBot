using System.Collections.Concurrent;
using System.Text;
using MeetMateBot.Application.DTOs;
using MeetMateBot.Application.Interfaces;
using MeetMateBot.Domain.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace MeetMateBot.Bot.Bot;

public sealed class TelegramBotWorker : BackgroundService
{
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ConcurrentDictionary<long, ProfileDraft> _profiles = new();
    private readonly ILogger<TelegramBotWorker> _logger;
    private readonly BotOptions _options;
    private readonly IProfileRepository _profileRepository;
    private readonly IReactionRepository _reactionRepository;
    private readonly IMatchRepository _matchRepository;
    private readonly IRecommendationService _recommendationService;

    public TelegramBotWorker(
        IHostApplicationLifetime applicationLifetime,
        ILogger<TelegramBotWorker> logger,
        IOptions<BotOptions> options,
        IProfileRepository profileRepository,
        IReactionRepository reactionRepository,
        IMatchRepository matchRepository,
        IRecommendationService recommendationService)
    {
        _applicationLifetime = applicationLifetime;
        _logger = logger;
        _options = options.Value;
        _profileRepository = profileRepository;
        _reactionRepository = reactionRepository;
        _matchRepository = matchRepository;
        _recommendationService = recommendationService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Token) || _options.Token == "PASTE_YOUR_TELEGRAM_BOT_TOKEN_HERE")
        {
            _logger.LogWarning("Telegram bot token is not configured. Add token to appsettings.json or user secrets.");
            _applicationLifetime.StopApplication();
            return;
        }

        var botClient = new TelegramBotClient(_options.Token);
        var me = await botClient.GetMe(stoppingToken);

        _logger.LogInformation("MeetMateBot started as @{Username}.", me.Username);

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery]
        };

        botClient.StartReceiving(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandlePollingErrorAsync,
            receiverOptions: receiverOptions,
            cancellationToken: stoppingToken);

        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);

        async Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken cancellationToken)
        {
            if (update.Message is { Text: { } text } message)
            {
                var chatId = message.Chat.Id;
                var input = text.Trim();

                _logger.LogInformation("Received message from chat {ChatId}: {Text}", chatId, input);

                var response = await BuildResponseAsync(chatId, input, cancellationToken);

                await client.SendMessage(
                    chatId: chatId,
                    text: response.Text,
                    replyMarkup: response.ReplyMarkup,
                    cancellationToken: cancellationToken);

                return;
            }

            if (update.CallbackQuery is { Data: { } data } callbackQuery)
            {
                await HandleCallbackQueryAsync(client, callbackQuery, data, cancellationToken);
            }
        }

        async Task HandleCallbackQueryAsync(ITelegramBotClient client, CallbackQuery callbackQuery, string data, CancellationToken cancellationToken)
        {
            if (callbackQuery.Message is null)
            {
                return;
            }

            var chatId = callbackQuery.Message.Chat.Id;
            var parts = data.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !long.TryParse(parts[1], out var targetChatId))
            {
                await client.AnswerCallbackQuery(callbackQuery.Id, "Невідома дія.", cancellationToken: cancellationToken);
                return;
            }

            var isLike = parts[0].Equals("like", StringComparison.OrdinalIgnoreCase);
            var isSkip = parts[0].Equals("skip", StringComparison.OrdinalIgnoreCase);
            if (!isLike && !isSkip)
            {
                await client.AnswerCallbackQuery(callbackQuery.Id, "Невідома дія.", cancellationToken: cancellationToken);
                return;
            }

            var currentProfile = await GetCurrentProfileAsync(chatId, cancellationToken);
            if (currentProfile is null)
            {
                await client.AnswerCallbackQuery(callbackQuery.Id, "Спочатку створи анкету.", cancellationToken: cancellationToken);
                await client.SendMessage(chatId, "Спочатку створи анкету через /start.", cancellationToken: cancellationToken);
                return;
            }

            var targetProfile = await _profileRepository.GetByTelegramChatIdAsync(targetChatId, cancellationToken);
            if (targetProfile is null)
            {
                await client.AnswerCallbackQuery(callbackQuery.Id, "Анкету не знайдено.", cancellationToken: cancellationToken);
                await client.SendMessage(chatId, "Анкету не знайдено. Спробуй /search ще раз.", cancellationToken: cancellationToken);
                return;
            }

            await _reactionRepository.SaveAsync(new ProfileReaction
            {
                FromTelegramChatId = chatId,
                ToTelegramChatId = targetChatId,
                IsLike = isLike,
                CreatedAtUtc = DateTime.UtcNow
            }, cancellationToken);

            if (isLike && await _reactionRepository.HasLikeAsync(targetChatId, chatId, cancellationToken))
            {
                await _matchRepository.SaveAsync(new ProfileMatch
                {
                    FirstTelegramChatId = chatId,
                    SecondTelegramChatId = targetChatId,
                    CreatedAtUtc = DateTime.UtcNow,
                    IsActive = true
                }, cancellationToken);

                await client.AnswerCallbackQuery(callbackQuery.Id, "Взаємний лайк!", cancellationToken: cancellationToken);
                await client.SendMessage(chatId, $"Є взаємний лайк з анкетою {targetProfile.Name}! Матч збережено.", cancellationToken: cancellationToken);
                return;
            }

            await client.AnswerCallbackQuery(
                callbackQuery.Id,
                isLike ? "Лайк збережено." : "Анкету пропущено.",
                cancellationToken: cancellationToken);

            var nextRecommendation = await BuildSearchResponseAsync(chatId, cancellationToken);
            await client.SendMessage(
                chatId: chatId,
                text: isLike ? "Лайк збережено. Наступна рекомендація:" : "Анкету пропущено. Наступна рекомендація:",
                cancellationToken: cancellationToken);
            await client.SendMessage(
                chatId: chatId,
                text: nextRecommendation.Text,
                replyMarkup: nextRecommendation.ReplyMarkup,
                cancellationToken: cancellationToken);
        }

        Task HandlePollingErrorAsync(ITelegramBotClient client, Exception exception, CancellationToken cancellationToken)
        {
            var errorMessage = exception switch
            {
                ApiRequestException apiException =>
                    $"Telegram API Error: [{apiException.ErrorCode}] {apiException.Message}",
                _ => exception.Message
            };

            _logger.LogError(exception, "Polling error: {ErrorMessage}", errorMessage);
            return Task.CompletedTask;
        }
    }

    private async Task<BotResponse> BuildResponseAsync(long chatId, string input, CancellationToken cancellationToken)
    {
        if (input.Equals("/start", StringComparison.OrdinalIgnoreCase))
        {
            var draft = new ProfileDraft { Step = ProfileRegistrationStep.WaitingForName };
            _profiles[chatId] = draft;

            return BotResponse.TextOnly("Вітаю! Я MeetMateBot. Створимо твою анкету.\n\nВведи ім'я для анкети:");
        }

        if (input.Equals("/help", StringComparison.OrdinalIgnoreCase))
        {
            return BotResponse.TextOnly("Доступні команди:\n/start - створити анкету заново\n/profile - переглянути анкету\n/edit - редагувати анкету\n/search - знайти рекомендовану анкету\n/matches - переглянути взаємні лайки\n/stats - загальна статистика бота\n/admin_stats - розширена статистика\n/report - звіт для демонстрації\n/demo_match - створити тестовий матч\n/seed - додати тестові анкети\n/ml_stats - статистика навчання моделі\n/debug_reco - пояснення поточної рекомендації\n/help - допомога");
        }

        if (input.Equals("/profile", StringComparison.OrdinalIgnoreCase))
        {
            var currentProfile = await GetCurrentProfileAsync(chatId, cancellationToken);
            return BotResponse.TextOnly(currentProfile is not null
                ? FormatProfile(currentProfile)
                : "Анкета ще не створена. Натисни /start, щоб почати.");
        }

        if (input.Equals("/edit", StringComparison.OrdinalIgnoreCase))
        {
            return BotResponse.TextOnly("Що редагувати?\n/edit_name - імя\n/edit_age - вік\n/edit_city - місто\n/edit_interests - інтереси\n/edit_bio - опис");
        }

        if (input.Equals("/edit_name", StringComparison.OrdinalIgnoreCase))
        {
            return await StartProfileEditingAsync(chatId, ProfileRegistrationStep.EditingName, "Введи нове імя:", cancellationToken);
        }

        if (input.Equals("/edit_age", StringComparison.OrdinalIgnoreCase))
        {
            return await StartProfileEditingAsync(chatId, ProfileRegistrationStep.EditingAge, "Введи новий вік:", cancellationToken);
        }

        if (input.Equals("/edit_city", StringComparison.OrdinalIgnoreCase))
        {
            return await StartProfileEditingAsync(chatId, ProfileRegistrationStep.EditingCity, "Введи нове місто:", cancellationToken);
        }

        if (input.Equals("/edit_interests", StringComparison.OrdinalIgnoreCase))
        {
            return await StartProfileEditingAsync(chatId, ProfileRegistrationStep.EditingInterests, "Введи нові інтереси через кому:", cancellationToken);
        }

        if (input.Equals("/edit_bio", StringComparison.OrdinalIgnoreCase))
        {
            return await StartProfileEditingAsync(chatId, ProfileRegistrationStep.EditingBio, "Введи новий опис:", cancellationToken);
        }

        if (input.Equals("/search", StringComparison.OrdinalIgnoreCase))
        {
            return await BuildSearchResponseAsync(chatId, cancellationToken);
        }

        if (input.Equals("/matches", StringComparison.OrdinalIgnoreCase))
        {
            return await BuildMatchesResponseAsync(chatId, cancellationToken);
        }

        if (input.Equals("/stats", StringComparison.OrdinalIgnoreCase))
        {
            return await BuildGlobalStatsResponseAsync(cancellationToken);
        }

        if (input.Equals("/admin_stats", StringComparison.OrdinalIgnoreCase))
        {
            return await BuildAdminStatsResponseAsync(cancellationToken);
        }

        if (input.Equals("/report", StringComparison.OrdinalIgnoreCase))
        {
            return await BuildReportResponseAsync(cancellationToken);
        }

        if (input.Equals("/ml_stats", StringComparison.OrdinalIgnoreCase))
        {
            return await BuildMachineLearningStatsResponseAsync(chatId, cancellationToken);
        }

        if (input.Equals("/debug_reco", StringComparison.OrdinalIgnoreCase))
        {
            return await BuildRecommendationDebugResponseAsync(chatId, cancellationToken);
        }

        if (input.Equals("/demo_match", StringComparison.OrdinalIgnoreCase))
        {
            return await BuildDemoMatchResponseAsync(chatId, cancellationToken);
        }

        if (input.Equals("/seed", StringComparison.OrdinalIgnoreCase))
        {
            return BotResponse.TextOnly(await SeedDemoProfilesAsync(cancellationToken));
        }

        if (!_profiles.TryGetValue(chatId, out var profile))
        {
            return BotResponse.TextOnly("Натисни /start, щоб створити анкету.");
        }

        var response = HandleProfileRegistration(profile, input);

        if (profile.Step == ProfileRegistrationStep.Completed)
        {
            await _profileRepository.SaveAsync(ToDatingProfile(chatId, profile), cancellationToken);
        }

        return BotResponse.TextOnly(response);
    }

    private async Task<BotResponse> StartProfileEditingAsync(long chatId, ProfileRegistrationStep step, string prompt, CancellationToken cancellationToken)
    {
        var currentProfile = await GetCurrentProfileAsync(chatId, cancellationToken);
        if (currentProfile is null)
        {
            return BotResponse.TextOnly("Анкета ще не створена. Натисни /start, щоб почати.");
        }

        var draft = ToProfileDraft(currentProfile);
        draft.Step = step;
        _profiles[chatId] = draft;

        return BotResponse.TextOnly(prompt);
    }

    private async Task<BotResponse> BuildSearchResponseAsync(long chatId, CancellationToken cancellationToken)
    {
        var currentProfile = await GetCurrentProfileAsync(chatId, cancellationToken);
        if (currentProfile is null)
        {
            return BotResponse.TextOnly("Спочатку створи анкету через /start.");
        }

        var recommendation = await _recommendationService.GetBestRecommendationAsync(chatId, cancellationToken);
        if (recommendation is null)
        {
            return BotResponse.TextOnly("Поки немає нових анкет для рекомендацій. Можна додати тестові анкети через /seed або зачекати нових користувачів.");
        }

        return new BotResponse(
            FormatRecommendation(recommendation),
            BuildReactionKeyboard(recommendation.RecommendedProfile.TelegramChatId));
    }
    private async Task<BotResponse> BuildMatchesResponseAsync(long chatId, CancellationToken cancellationToken)
    {
        var currentProfile = await GetCurrentProfileAsync(chatId, cancellationToken);
        if (currentProfile is null)
        {
            return BotResponse.TextOnly("Спочатку створи анкету через /start.");
        }

        var matches = await _matchRepository.GetByTelegramChatIdAsync(chatId, cancellationToken);
        if (matches.Count == 0)
        {
            return BotResponse.TextOnly("Поки немає взаємних лайків. Натисни /search і став лайки анкетам.");
        }

        var builder = new StringBuilder();
        builder.AppendLine("Твої матчі:");
        builder.AppendLine();

        var counter = 1;
        foreach (var match in matches.OrderByDescending(match => match.CreatedAtUtc))
        {
            var otherChatId = match.FirstTelegramChatId == chatId
                ? match.SecondTelegramChatId
                : match.FirstTelegramChatId;
            var profile = await _profileRepository.GetByTelegramChatIdAsync(otherChatId, cancellationToken);

            if (profile is null)
            {
                continue;
            }

            builder.AppendLine($"{counter}. {profile.Name}, {profile.Age}, {profile.City}");
            if (match.CreatedAtUtc != default)
            {
                builder.AppendLine($"   Дата матчу: {match.CreatedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm}");
            }
            builder.AppendLine($"   Інтереси: {string.Join(", ", profile.Interests)}");
            builder.AppendLine($"   Опис: {profile.Bio}");
            builder.AppendLine();
            counter++;
        }

        return counter == 1
            ? BotResponse.TextOnly("Матчі знайдені, але анкети користувачів не вдалося завантажити.")
            : BotResponse.TextOnly(builder.ToString());
    }

    private async Task<BotResponse> BuildMachineLearningStatsResponseAsync(long chatId, CancellationToken cancellationToken)
    {
        var diagnostics = await _recommendationService.GetRecommendationDiagnosticsAsync(chatId, cancellationToken);
        if (diagnostics is null)
        {
            return BotResponse.TextOnly("Спочатку створи анкету через /start.");
        }

        var builder = new StringBuilder();
        builder.AppendLine("Статистика ML-моделі:");
        builder.AppendLine($"Реакцій у навчанні: {diagnostics.TotalReactions}");
        builder.AppendLine($"Лайків: {diagnostics.LikeCount}");
        builder.AppendLine($"Пропусків: {diagnostics.SkipCount}");
        builder.AppendLine($"Уподобані міста: {FormatListOrFallback(diagnostics.PreferredCities, "ще недостатньо даних")}");
        builder.AppendLine($"Уподобані інтереси: {FormatListOrFallback(diagnostics.PreferredInterests, "ще недостатньо даних")}");

        if (diagnostics.TopRecommendation is not null)
        {
            builder.AppendLine();
            builder.AppendLine("Поточний найкращий кандидат:");
            builder.AppendLine($"{diagnostics.TopRecommendation.RecommendedProfile.Name} ({diagnostics.TopRecommendation.CompatibilityPercent}% сумісності)");
            builder.AppendLine($"ML-оцінка: {diagnostics.TopRecommendation.MachineLearningScore}%");
        }

        return BotResponse.TextOnly(builder.ToString());
    }

    private async Task<BotResponse> BuildGlobalStatsResponseAsync(CancellationToken cancellationToken)
    {
        var profiles = await _profileRepository.GetAllAsync(cancellationToken);
        var reactions = await _reactionRepository.GetAllAsync(cancellationToken);
        var matches = await _matchRepository.GetAllAsync(cancellationToken);

        var likeCount = reactions.Count(reaction => reaction.IsLike);
        var skipCount = reactions.Count - likeCount;
        var uniqueUsers = profiles
            .Where(profile => profile.TelegramChatId > 0)
            .Select(profile => profile.TelegramChatId)
            .Distinct()
            .Count();
        var demoProfiles = profiles.Count(profile => profile.TelegramChatId < 0);
        var maleProfiles = profiles.Count(profile => string.Equals(profile.Gender, "чоловіча", StringComparison.OrdinalIgnoreCase) || string.Equals(profile.Gender, "male", StringComparison.OrdinalIgnoreCase));
        var femaleProfiles = profiles.Count(profile => string.Equals(profile.Gender, "жіноча", StringComparison.OrdinalIgnoreCase) || string.Equals(profile.Gender, "female", StringComparison.OrdinalIgnoreCase));
        var likeRate = reactions.Count == 0 ? 0 : likeCount * 100.0 / reactions.Count;
        var matchRate = likeCount == 0 ? 0 : matches.Count * 100.0 / likeCount;
        var recentProfiles = profiles.Count(profile => profile.UpdatedAtUtc != default && profile.UpdatedAtUtc >= DateTime.UtcNow.AddDays(-30));

        var builder = new StringBuilder();
        builder.AppendLine("Загальна статистика MeetMateBot:");
        builder.AppendLine($"Усього анкет: {profiles.Count}");
        builder.AppendLine($"Реальних користувачів: {uniqueUsers}");
        builder.AppendLine($"Тестових анкет: {demoProfiles}");
        builder.AppendLine($"Чоловічих анкет: {maleProfiles}");
        builder.AppendLine($"Жіночих анкет: {femaleProfiles}");
        builder.AppendLine($"Анкет, оновлених за 30 днів: {recentProfiles}");
        builder.AppendLine();
        builder.AppendLine($"Усього реакцій: {reactions.Count}");
        builder.AppendLine($"Лайків: {likeCount}");
        builder.AppendLine($"Пропусків: {skipCount}");
        builder.AppendLine($"Частка лайків: {likeRate:F1}%");
        builder.AppendLine();
        builder.AppendLine($"Активних матчів: {matches.Count}");
        builder.AppendLine($"Конверсія лайк -> матч: {matchRate:F1}%");

        return BotResponse.TextOnly(builder.ToString());
    }

    private async Task<BotResponse> BuildAdminStatsResponseAsync(CancellationToken cancellationToken)
    {
        var profiles = await _profileRepository.GetAllAsync(cancellationToken);
        var reactions = await _reactionRepository.GetAllAsync(cancellationToken);
        var matches = await _matchRepository.GetAllAsync(cancellationToken);

        var builder = new StringBuilder();
        builder.AppendLine("Розширена статистика:");
        builder.AppendLine($"Топ міст: {FormatListOrFallback(GetTopCities(profiles, 5), "даних недостатньо")}");
        builder.AppendLine($"Топ інтересів: {FormatListOrFallback(GetTopInterests(profiles, 7), "даних недостатньо")}");
        builder.AppendLine($"Останні матчі: {FormatListOrFallback(GetRecentMatchesSummary(matches, 5), "ще немає матчів")}");
        builder.AppendLine($"Конверсія реакція -> лайк: {CalculatePercent(reactions.Count(reaction => reaction.IsLike), reactions.Count):F1}%");
        builder.AppendLine($"Конверсія лайк -> матч: {CalculatePercent(matches.Count, reactions.Count(reaction => reaction.IsLike)):F1}%");
        builder.AppendLine($"Конверсія анкета -> матч: {CalculatePercent(matches.Count, profiles.Count(profile => profile.TelegramChatId > 0)):F1}%");

        return BotResponse.TextOnly(builder.ToString());
    }

    private async Task<BotResponse> BuildReportResponseAsync(CancellationToken cancellationToken)
    {
        var profiles = await _profileRepository.GetAllAsync(cancellationToken);
        var reactions = await _reactionRepository.GetAllAsync(cancellationToken);
        var matches = await _matchRepository.GetAllAsync(cancellationToken);

        var realProfilesCount = profiles.Count(profile => profile.TelegramChatId > 0);
        var likeCount = reactions.Count(reaction => reaction.IsLike);
        var skipCount = reactions.Count - likeCount;

        var builder = new StringBuilder();
        builder.AppendLine("Звіт про роботу MeetMateBot:");
        builder.AppendLine($"У системі зареєстровано {realProfilesCount} реальних користувачів та {profiles.Count(profile => profile.TelegramChatId < 0)} тестових анкет.");
        builder.AppendLine($"Зафіксовано {reactions.Count} реакцій користувачів, з них {likeCount} лайків та {skipCount} пропусків.");
        builder.AppendLine($"Створено {matches.Count} активних матчів.");
        builder.AppendLine($"Конверсія лайків у матчі становить {CalculatePercent(matches.Count, likeCount):F1}%.");
        builder.AppendLine($"Найпопулярніші міста: {FormatListOrFallback(GetTopCities(profiles, 3), "даних недостатньо")}.");
        builder.AppendLine($"Найпоширеніші інтереси: {FormatListOrFallback(GetTopInterests(profiles, 5), "даних недостатньо")}.");
        builder.AppendLine($"Останні матчі: {FormatListOrFallback(GetRecentMatchesSummary(matches, 3), "ще немає матчів")}.");
        builder.AppendLine("Рекомендаційна система використовує анкетні ознаки, історію реакцій та ML-оцінку для персоналізації підбору кандидатів.");

        return BotResponse.TextOnly(builder.ToString());
    }

    private async Task<BotResponse> BuildRecommendationDebugResponseAsync(long chatId, CancellationToken cancellationToken)
    {
        var diagnostics = await _recommendationService.GetRecommendationDiagnosticsAsync(chatId, cancellationToken);
        if (diagnostics is null)
        {
            return BotResponse.TextOnly("Спочатку створи анкету через /start.");
        }

        if (diagnostics.TopRecommendation is null)
        {
            return BotResponse.TextOnly("Для пояснення рекомендації поки немає доступних кандидатів. Спробуй /seed або зачекай нових користувачів.");
        }

        var recommendation = diagnostics.TopRecommendation;
        var builder = new StringBuilder();
        builder.AppendLine("Деталі рекомендації:");
        builder.AppendLine($"Кандидат: {recommendation.RecommendedProfile.Name}");
        builder.AppendLine($"Підсумкова сумісність: {recommendation.CompatibilityPercent}%");
        builder.AppendLine($"ML-оцінка: {recommendation.MachineLearningScore}%");
        builder.AppendLine($"Навчальних реакцій: {recommendation.TrainingSampleCount}");
        builder.AppendLine($"Скоринг за містом: {recommendation.CityScore}");
        builder.AppendLine($"Скоринг за інтересами: {recommendation.InterestScore}");
        builder.AppendLine($"Скоринг за віком: {recommendation.AgeScore}");
        builder.AppendLine($"Скоринг за повнотою анкети: {recommendation.CompletenessScore}");
        builder.AppendLine($"Спільні інтереси: {FormatListOrFallback(recommendation.CommonInterests, "не знайдено")}");
        builder.AppendLine($"Різниця у віці: {recommendation.AgeDifference}");
        builder.AppendLine(recommendation.IsSameCity
            ? "Місто збігається."
            : "Місто не збігається.");

        return BotResponse.TextOnly(builder.ToString());
    }

    private async Task<BotResponse> BuildDemoMatchResponseAsync(long chatId, CancellationToken cancellationToken)
    {
        var currentProfile = await GetCurrentProfileAsync(chatId, cancellationToken);
        if (currentProfile is null)
        {
            return BotResponse.TextOnly("Спочатку створи анкету через /start.");
        }

        var allProfiles = await _profileRepository.GetAllAsync(cancellationToken);
        var demoProfile = allProfiles
            .Where(profile => profile.TelegramChatId != chatId)
            .Where(profile => profile.TelegramChatId < 0)
            .Where(profile => _recommendationService.IsGenderCompatible(currentProfile, profile))
            .OrderByDescending(profile => _recommendationService.CalculateScore(currentProfile, profile))
            .FirstOrDefault()
            ?? allProfiles.FirstOrDefault(profile => profile.TelegramChatId != chatId && profile.TelegramChatId < 0);

        if (demoProfile is null)
        {
            return BotResponse.TextOnly("Тестових анкет не знайдено. Спочатку виконай /seed.");
        }

        await _reactionRepository.SaveAsync(new ProfileReaction
        {
            FromTelegramChatId = chatId,
            ToTelegramChatId = demoProfile.TelegramChatId,
            IsLike = true,
            CreatedAtUtc = DateTime.UtcNow
        }, cancellationToken);

        await _reactionRepository.SaveAsync(new ProfileReaction
        {
            FromTelegramChatId = demoProfile.TelegramChatId,
            ToTelegramChatId = chatId,
            IsLike = true,
            CreatedAtUtc = DateTime.UtcNow
        }, cancellationToken);

        await _matchRepository.SaveAsync(new ProfileMatch
        {
            FirstTelegramChatId = chatId,
            SecondTelegramChatId = demoProfile.TelegramChatId,
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = true
        }, cancellationToken);

        return BotResponse.TextOnly($"Тестовий матч створено з анкетою {demoProfile.Name}. Тепер перевір /matches.");
    }

    private async Task<DatingProfile?> GetCurrentProfileAsync(long chatId, CancellationToken cancellationToken)
    {
        if (_profiles.TryGetValue(chatId, out var draft) && draft.Step == ProfileRegistrationStep.Completed)
        {
            return ToDatingProfile(chatId, draft);
        }

        return await _profileRepository.GetByTelegramChatIdAsync(chatId, cancellationToken);
    }

    private async Task<string> SeedDemoProfilesAsync(CancellationToken cancellationToken)
    {
        var femaleNames = new[]
        {
            "Олена", "Марія", "Анна", "Софія", "Вікторія", "Катерина", "Ірина", "Наталія", "Юлія", "Дарина",
            "Анастасія", "Христина", "Вероніка", "Аліна", "Єлизавета", "Поліна", "Тетяна", "Оксана", "Лілія", "Діана",
            "Марта", "Зоряна", "Валерія", "Каріна", "Емілія"
        };
        var maleNames = new[]
        {
            "Андрій", "Дмитро", "Олександр", "Максим", "Іван", "Богдан", "Роман", "Владислав", "Назар", "Артем",
            "Денис", "Михайло", "Павло", "Віталій", "Сергій", "Ярослав", "Тарас", "Олег", "Кирило", "Матвій",
            "Данило", "Єгор", "Арсен", "Микита", "Вадим"
        };
        var cities = new[] { "Київ", "Львів", "Одеса", "Харків", "Дніпро", "Вінниця", "Полтава", "Чернівці", "Івано-Франківськ", "Тернопіль" };
        var interestPool = new[]
        {
            "музика", "кіно", "подорожі", "спорт", "книги", "кава", "фотографія", "програмування", "ігри", "театр",
            "танці", "кулінарія", "велосипед", "фітнес", "психологія", "мистецтво", "волонтерство", "прогулянки", "мови", "стартапи"
        };
        var bios = new[]
        {
            "Люблю щирі розмови, прогулянки містом і нові враження.",
            "Ціную почуття гумору, активний відпочинок і цікаві знайомства.",
            "У вільний час читаю, слухаю музику та відкриваю нові місця.",
            "Шукаю людину для легкого спілкування, кави та спільних планів.",
            "Люблю подорожі, добрі фільми і людей, з якими можна бути собою."
        };

        for (var index = 0; index < 100; index++)
        {
            var isFemale = index % 2 == 0;
            var name = isFemale
                ? femaleNames[index / 2 % femaleNames.Length]
                : maleNames[index / 2 % maleNames.Length];
            var gender = isFemale ? "жіноча" : "чоловіча";
            var lookingForGender = (index % 4) switch
            {
                0 => "чоловіча",
                1 => "жіноча",
                2 => "жіноча",
                _ => "чоловіча"
            };

            var profile = new DatingProfile
            {
                TelegramChatId = -2001 - index,
                Name = $"{name} {index + 1}",
                Age = 18 + index % 18,
                Gender = gender,
                LookingForGender = lookingForGender,
                City = cities[index % cities.Length],
                Interests =
                [
                    interestPool[index % interestPool.Length],
                    interestPool[(index + 5) % interestPool.Length],
                    interestPool[(index + 11) % interestPool.Length]
                ],
                Bio = bios[index % bios.Length]
            };

            await _profileRepository.SaveAsync(profile, cancellationToken);
        }

        return "100 тестових анкет додано у Firestore. Тепер можна перевірити /search.";
    }

    private static string HandleProfileRegistration(ProfileDraft profile, string input)
    {
        switch (profile.Step)
        {
            case ProfileRegistrationStep.EditingName:
                if (input.Length < 2)
                {
                    return "Ім'я має містити хоча б 2 символи. Введи ім'я ще раз:";
                }

                profile.Name = input;
                profile.Step = ProfileRegistrationStep.Completed;
                return "Ім'я оновлено.\n\n" + FormatProfile(profile);

            case ProfileRegistrationStep.EditingAge:
                if (!int.TryParse(input, out var editedAge) || editedAge < 18 || editedAge > 100)
                {
                    return "Вік має бути числом від 18 до 100. Введи вік ще раз:";
                }

                profile.Age = editedAge;
                profile.Step = ProfileRegistrationStep.Completed;
                return "Вік оновлено.\n\n" + FormatProfile(profile);

            case ProfileRegistrationStep.EditingCity:
                if (input.Length < 2)
                {
                    return "Назва міста має містити хоча б 2 символи. Введи місто ще раз:";
                }

                profile.City = input;
                profile.Step = ProfileRegistrationStep.Completed;
                return "Місто оновлено.\n\n" + FormatProfile(profile);

            case ProfileRegistrationStep.EditingInterests:
                var editedInterests = ParseInterests(input);
                if (editedInterests.Count == 0)
                {
                    return "Введи хоча б один інтерес. Наприклад: музика, спорт";
                }

                profile.Interests.Clear();
                profile.Interests.AddRange(editedInterests);
                profile.Step = ProfileRegistrationStep.Completed;
                return "Інтереси оновлено.\n\n" + FormatProfile(profile);

            case ProfileRegistrationStep.EditingBio:
                if (input.Length < 10)
                {
                    return "Опис має містити хоча б 10 символів. Напиши трохи більше про себе:";
                }

                profile.Bio = input;
                profile.Step = ProfileRegistrationStep.Completed;
                return "Опис оновлено.\n\n" + FormatProfile(profile);

            case ProfileRegistrationStep.WaitingForName:
                if (input.Length < 2)
                {
                    return "Ім'я має містити хоча б 2 символи. Введи ім'я ще раз:";
                }

                profile.Name = input;
                profile.Step = ProfileRegistrationStep.WaitingForAge;
                return "Введи свій вік:";

            case ProfileRegistrationStep.WaitingForAge:
                if (!int.TryParse(input, out var age) || age < 18 || age > 100)
                {
                    return "Вік має бути числом від 18 до 100. Введи вік ще раз:";
                }

                profile.Age = age;
                profile.Step = ProfileRegistrationStep.WaitingForGender;
                return "Вкажи свою стать: чоловіча або жіноча";

            case ProfileRegistrationStep.WaitingForGender:
                if (!IsSupportedGender(input))
                {
                    return "Будь ласка, введи: чоловіча або жіноча";
                }

                profile.Gender = NormalizeGender(input);
                profile.Step = ProfileRegistrationStep.WaitingForLookingForGender;
                return "Кого шукаєш: чоловіча або жіноча?";

            case ProfileRegistrationStep.WaitingForLookingForGender:
                if (!IsSupportedGender(input))
                {
                    return "Будь ласка, введи: чоловіча або жіноча";
                }

                profile.LookingForGender = NormalizeGender(input);
                profile.Step = ProfileRegistrationStep.WaitingForCity;
                return "Введи місто:";

            case ProfileRegistrationStep.WaitingForCity:
                if (input.Length < 2)
                {
                    return "Назва міста має містити хоча б 2 символи. Введи місто ще раз:";
                }

                profile.City = input;
                profile.Step = ProfileRegistrationStep.WaitingForInterests;
                return "Введи інтереси через кому. Наприклад: музика, кіно, подорожі";

            case ProfileRegistrationStep.WaitingForInterests:
                var interests = ParseInterests(input);
                if (interests.Count == 0)
                {
                    return "Введи хоча б один інтерес. Наприклад: музика, спорт";
                }

                profile.Interests.Clear();
                profile.Interests.AddRange(interests);
                profile.Step = ProfileRegistrationStep.WaitingForBio;
                return "Напиши короткий опис про себе:";

            case ProfileRegistrationStep.WaitingForBio:
                if (input.Length < 10)
                {
                    return "Опис має містити хоча б 10 символів. Напиши трохи більше про себе:";
                }

                profile.Bio = input;
                profile.Step = ProfileRegistrationStep.Completed;
                return "Анкету створено!\n\n" + FormatProfile(profile);

            case ProfileRegistrationStep.Completed:
                return "Анкета вже створена. Команда /profile покаже її, /search знайде рекомендацію, а /edit дозволить редагувати.";

            default:
                profile.Step = ProfileRegistrationStep.WaitingForName;
                return "Почнемо створення анкети. Введи ім'я:";
        }
    }

    private static List<string> ParseInterests(string input)
    {
        return input
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(interest => interest.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
    private static InlineKeyboardMarkup BuildReactionKeyboard(long targetChatId)
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Подобається", $"like:{targetChatId}"),
                InlineKeyboardButton.WithCallbackData("Пропустити", $"skip:{targetChatId}")
            }
        });
    }

    private static bool IsGenderCompatible(DatingProfile currentProfile, DatingProfile candidate)
    {
        return string.Equals(candidate.Gender, currentProfile.LookingForGender, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.LookingForGender, currentProfile.Gender, StringComparison.OrdinalIgnoreCase);
    }

    private static int CalculateRecommendationScore(DatingProfile currentProfile, DatingProfile candidate)
    {
        var score = 0;

        if (string.Equals(currentProfile.City, candidate.City, StringComparison.OrdinalIgnoreCase))
        {
            score += 40;
        }

        var commonInterests = currentProfile.Interests
            .Intersect(candidate.Interests, StringComparer.OrdinalIgnoreCase)
            .Count();
        score += commonInterests * 10;

        var ageDifference = Math.Abs(currentProfile.Age - candidate.Age);
        if (ageDifference <= 3)
        {
            score += 20;
        }
        else if (ageDifference <= 5)
        {
            score += 10;
        }

        if (IsGenderCompatible(currentProfile, candidate))
        {
            score += 30;
        }

        return score;
    }

    private static bool IsSupportedGender(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "чоловіча" or "чоловіча стать" or "чоловік" or "male" or "m"
            or "жіноча" or "жіноча стать" or "жінка" or "female" or "f";
    }

    private static string NormalizeGender(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "чоловіча" or "чоловіча стать" or "чоловік" or "male" or "m"
            ? "чоловіча"
            : "жіноча";
    }

    private static ProfileDraft ToProfileDraft(DatingProfile profile)
    {
        var draft = new ProfileDraft
        {
            Step = ProfileRegistrationStep.Completed,
            Name = profile.Name,
            Age = profile.Age,
            Gender = profile.Gender,
            LookingForGender = profile.LookingForGender,
            City = profile.City,
            Bio = profile.Bio
        };

        draft.Interests.AddRange(profile.Interests);
        return draft;
    }

    private static DatingProfile ToDatingProfile(long chatId, ProfileDraft profile)
    {
        return new DatingProfile
        {
            TelegramChatId = chatId,
            Name = profile.Name ?? string.Empty,
            Age = profile.Age ?? 0,
            Gender = profile.Gender ?? string.Empty,
            LookingForGender = profile.LookingForGender ?? string.Empty,
            City = profile.City ?? string.Empty,
            Interests = [.. profile.Interests],
            Bio = profile.Bio ?? string.Empty
        };
    }

    private static string FormatProfile(ProfileDraft profile)
    {
        var builder = new StringBuilder();

        builder.AppendLine($"Анкета: {profile.Name}, {profile.Age}");
        builder.AppendLine($"Стать: {profile.Gender}");
        builder.AppendLine($"Шукає: {profile.LookingForGender}");
        builder.AppendLine($"Місто: {profile.City}");
        builder.AppendLine($"Інтереси: {string.Join(", ", profile.Interests)}");
        builder.AppendLine();
        builder.AppendLine(profile.Bio);

        return builder.ToString();
    }

    private static string FormatProfile(DatingProfile profile)
    {
        var builder = new StringBuilder();

        builder.AppendLine($"Анкета: {profile.Name}, {profile.Age}");
        builder.AppendLine($"Стать: {profile.Gender}");
        builder.AppendLine($"Шукає: {profile.LookingForGender}");
        builder.AppendLine($"Місто: {profile.City}");
        builder.AppendLine($"Інтереси: {string.Join(", ", profile.Interests)}");
        builder.AppendLine();
        builder.AppendLine(profile.Bio);

        return builder.ToString();
    }

    private static string FormatRecommendation(ProfileRecommendation recommendation)
    {
        var profile = recommendation.RecommendedProfile;
        var builder = new StringBuilder();

        builder.AppendLine($"Рекомендована анкета, сумісність: {recommendation.CompatibilityPercent}%");
        builder.AppendLine($"ML-оцінка: {recommendation.MachineLearningScore}% на основі {recommendation.TrainingSampleCount} реакцій");
        builder.AppendLine($"Скоринг: місто {recommendation.CityScore}, інтереси {recommendation.InterestScore}, вік {recommendation.AgeScore}, повнота {recommendation.CompletenessScore}");
        builder.AppendLine();
        builder.Append(FormatProfile(profile));
        builder.AppendLine();
        builder.AppendLine("Причини рекомендації:");
        builder.AppendLine(recommendation.IsSameCity
            ? "- однакове місто"
            : "- різні міста");
        builder.AppendLine(recommendation.CommonInterests.Count > 0
            ? $"- спільні інтереси: {string.Join(", ", recommendation.CommonInterests)}"
            : "- спільних інтересів поки не знайдено");
        builder.AppendLine($"- різниця у віці: {recommendation.AgeDifference}");
        builder.AppendLine(recommendation.IsProfileRecentlyUpdated
            ? "- анкета нещодавно оновлювалась"
            : "- анкета давно не оновлювалась");

        return builder.ToString();
    }

    private static string FormatListOrFallback(IReadOnlyList<string> values, string fallback)
    {
        return values.Count == 0
            ? fallback
            : string.Join(", ", values);
    }

    private static List<string> GetTopCities(IReadOnlyList<DatingProfile> profiles, int take)
    {
        return profiles
            .Where(profile => !string.IsNullOrWhiteSpace(profile.City))
            .GroupBy(profile => profile.City, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .Select(group => $"{group.Key} ({group.Count()})")
            .ToList();
    }

    private static List<string> GetTopInterests(IReadOnlyList<DatingProfile> profiles, int take)
    {
        return profiles
            .SelectMany(profile => profile.Interests)
            .Where(static interest => !string.IsNullOrWhiteSpace(interest))
            .GroupBy(interest => interest, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .Select(group => $"{group.Key} ({group.Count()})")
            .ToList();
    }

    private static List<string> GetRecentMatchesSummary(IReadOnlyList<ProfileMatch> matches, int take)
    {
        return matches
            .OrderByDescending(match => match.CreatedAtUtc)
            .Take(take)
            .Select(match => match.CreatedAtUtc == default
                ? $"{match.FirstTelegramChatId} ↔ {match.SecondTelegramChatId}"
                : $"{match.FirstTelegramChatId} ↔ {match.SecondTelegramChatId} ({match.CreatedAtUtc.ToLocalTime():dd.MM HH:mm})")
            .ToList();
    }

    private static double CalculatePercent(int numerator, int denominator)
    {
        return denominator == 0 ? 0 : numerator * 100.0 / denominator;
    }

    private sealed record BotResponse(string Text, InlineKeyboardMarkup? ReplyMarkup)
    {
        public static BotResponse TextOnly(string text) => new(text, null);
    }
}






