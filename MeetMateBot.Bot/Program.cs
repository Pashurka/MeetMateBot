using MeetMateBot.Application.Interfaces;
using MeetMateBot.Application.Services;
using MeetMateBot.Bot.Bot;
using MeetMateBot.Infrastructure.Configuration;
using MeetMateBot.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables();

builder.Services.Configure<BotOptions>(builder.Configuration.GetSection(BotOptions.SectionName));
builder.Services.Configure<FirestoreOptions>(builder.Configuration.GetSection(FirestoreOptions.SectionName));
builder.Services.Configure<MachineLearningOptions>(builder.Configuration.GetSection(MachineLearningOptions.SectionName));
builder.Services.AddSingleton<IProfileRepository, FirestoreProfileRepository>();
builder.Services.AddSingleton<IReactionRepository, FirestoreReactionRepository>();
builder.Services.AddSingleton<IMatchRepository, FirestoreMatchRepository>();
builder.Services.AddSingleton<IRecommendationService, RecommendationService>();
builder.Services.AddHostedService<TelegramBotWorker>();

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

await builder.Build().RunAsync();


