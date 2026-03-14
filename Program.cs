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
builder.Services.AddSingleton<ObsidianVaultWriter>();
builder.Services.AddHostedService<ObsidianBotService>();

await builder.Build().RunAsync();
