using Microsoft.Extensions.Hosting;
using ObsidianBot.Configuration;
using ObsidianBot.Services;
using Telegram.Bot;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton(ObsidianBotOptions.Load(builder.Configuration));
builder.Services.AddSingleton<ITelegramBotClient>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<ObsidianBotOptions>();
    return new TelegramBotClient(options.TelegramBotToken);
});
builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(60) });
builder.Services.AddSingleton<OpenAiEmbeddingClient>();
builder.Services.AddSingleton<ObsidianVaultWriter>();
builder.Services.AddSingleton<VaultSearchService>();
builder.Services.AddHostedService<VaultSearchIndexer>();
builder.Services.AddHostedService<ObsidianBotService>();

await builder.Build().RunAsync();
