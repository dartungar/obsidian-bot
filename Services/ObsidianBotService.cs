using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ObsidianBot.Configuration;
using ObsidianBot.Models;
using ObsidianBot.Telegram;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace ObsidianBot.Services;

public sealed class ObsidianBotService : BackgroundService
{
    private readonly ILogger<ObsidianBotService> _logger;
    private readonly ObsidianBotOptions _options;
    private readonly ITelegramBotClient _bot;
    private readonly ObsidianVaultWriter _vaultWriter;
    private readonly ConcurrentDictionary<long, PendingCapture> _pendingByChat = new();
    private readonly ConcurrentDictionary<long, DateTimeOffset> _awaitingDateByChat = new();

    private int _offset;

    public ObsidianBotService(
        ILogger<ObsidianBotService> logger,
        ObsidianBotOptions options,
        ITelegramBotClient bot,
        ObsidianVaultWriter vaultWriter)
    {
        _logger = logger;
        _options = options;
        _bot = bot;
        _vaultWriter = vaultWriter;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var me = await _bot.GetMeAsync(stoppingToken);
        await ConfigureCommandsAsync(stoppingToken);
        _logger.LogInformation("Obsidian bot started as @{Username}", me.Username);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updates = await _bot.GetUpdatesAsync(
                    offset: _offset,
                    timeout: 30,
                    cancellationToken: stoppingToken);

                foreach (var update in updates)
                {
                    _offset = update.Id + 1;
                    await HandleUpdateAsync(update, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Polling failed; retrying");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private async Task ConfigureCommandsAsync(CancellationToken ct)
    {
        try
        {
            await _bot.SetMyCommandsAsync(
                TelegramKeyboards.GetBotCommands(),
                BotCommandScope.Chat(_options.AllowedUserId),
                cancellationToken: ct);

            await _bot.SetChatMenuButtonAsync(
                _options.AllowedUserId,
                new MenuButtonCommands(),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to configure Telegram commands or menu button");
        }
    }

    private async Task HandleUpdateAsync(Update update, CancellationToken ct)
    {
        if (update.CallbackQuery is { } callbackQuery)
        {
            await HandleCallbackAsync(callbackQuery, ct);
            return;
        }

        if (update.Message is { } message)
        {
            await HandleMessageAsync(message, ct);
        }
    }

    private async Task HandleMessageAsync(Message message, CancellationToken ct)
    {
        var chatId = message.Chat.Id;
        var userId = message.From?.Id;

        if (userId != _options.AllowedUserId)
        {
            await _bot.SendTextMessageAsync(chatId, "Unauthorized.", cancellationToken: ct);
            return;
        }

        if (await TryHandleDateReplyAsync(chatId, message, ct))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(message.Text))
        {
            await HandleTextMessageAsync(chatId, message.Text.Trim(), ct);
            return;
        }

        if (message.Voice is not null)
        {
            var bytes = await DownloadFileAsync(message.Voice.FileId, ct);
            await PromptDestinationAsync(chatId, new PendingCapture
            {
                MediaBytes = bytes,
                MediaFileExtension = ".ogg"
            }, ct);
            return;
        }

        if (message.Photo is { Length: > 0 })
        {
            var largest = message.Photo.OrderByDescending(photo => photo.FileSize).First();
            var bytes = await DownloadFileAsync(largest.FileId, ct);

            await PromptDestinationAsync(chatId, new PendingCapture
            {
                MediaBytes = bytes,
                MediaFileExtension = ".jpg",
                TextContent = string.IsNullOrWhiteSpace(message.Caption) ? null : message.Caption.Trim()
            }, ct);
        }
    }

    private async Task<bool> TryHandleDateReplyAsync(long chatId, Message message, CancellationToken ct)
    {
        if (!_awaitingDateByChat.TryGetValue(chatId, out var setAt))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - setAt >= TimeSpan.FromMinutes(5))
        {
            _awaitingDateByChat.TryRemove(chatId, out _);
            return false;
        }

        if (string.IsNullOrWhiteSpace(message.Text) || message.Text.StartsWith('/'))
        {
            return false;
        }

        await HandleDateInputAsync(chatId, message.Text.Trim(), ct);
        return true;
    }

    private async Task HandleTextMessageAsync(long chatId, string text, CancellationToken ct)
    {
        if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("/help", StringComparison.OrdinalIgnoreCase))
        {
            await _bot.SendTextMessageAsync(
                chatId,
                "Send text, voice, or photo and I will save it to Obsidian.\nUse /add <text> for quick notes.\nUse /addtask <text> to add a checkbox task.",
                replyMarkup: TelegramKeyboards.BuildMainReplyKeyboard(),
                cancellationToken: ct);
            return;
        }

        if (text.StartsWith("/cancel", StringComparison.OrdinalIgnoreCase))
        {
            ClearPendingState(chatId);
            await _bot.SendTextMessageAsync(
                chatId,
                "Cancelled.",
                replyMarkup: TelegramKeyboards.BuildMainReplyKeyboard(),
                cancellationToken: ct);
            return;
        }

        if (text.StartsWith("/addtask", StringComparison.OrdinalIgnoreCase))
        {
            var content = text[8..].Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                await _bot.SendTextMessageAsync(
                    chatId,
                    "Usage: /addtask your task text",
                    replyMarkup: TelegramKeyboards.BuildMainReplyKeyboard(),
                    cancellationToken: ct);
                return;
            }

            await PromptDestinationAsync(chatId, new PendingCapture { TextContent = content, IsTask = true }, ct);
            return;
        }

        if (text.StartsWith("/add", StringComparison.OrdinalIgnoreCase))
        {
            var content = text[4..].Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                await _bot.SendTextMessageAsync(
                    chatId,
                    "Usage: /add your note text",
                    replyMarkup: TelegramKeyboards.BuildMainReplyKeyboard(),
                    cancellationToken: ct);
                return;
            }

            await PromptDestinationAsync(chatId, new PendingCapture { TextContent = content }, ct);
            return;
        }

        if (text.StartsWith('/'))
        {
            await _bot.SendTextMessageAsync(
                chatId,
                "Unknown command. Use /help.",
                replyMarkup: TelegramKeyboards.BuildMainReplyKeyboard(),
                cancellationToken: ct);
            return;
        }

        await PromptDestinationAsync(chatId, new PendingCapture { TextContent = text }, ct);
    }

    private async Task HandleCallbackAsync(CallbackQuery callbackQuery, CancellationToken ct)
    {
        var chatId = callbackQuery.Message?.Chat.Id;
        if (chatId is null || string.IsNullOrWhiteSpace(callbackQuery.Data))
        {
            await _bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        if (callbackQuery.From.Id != _options.AllowedUserId)
        {
            await _bot.AnswerCallbackQueryAsync(callbackQuery.Id, "Unauthorized.", cancellationToken: ct);
            return;
        }

        var data = callbackQuery.Data;
        if (!data.StartsWith("obs:", StringComparison.Ordinal))
        {
            await _bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        var action = data[4..];
        await _bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);

        if (action == "cancel")
        {
            ClearPendingState(chatId.Value);
            await EditOrSendAsync(callbackQuery.Message, "Cancelled.", ct);
            return;
        }

        if (!_pendingByChat.TryGetValue(chatId.Value, out var pending))
        {
            await EditOrSendAsync(callbackQuery.Message, "Session expired. Send content again.", ct);
            return;
        }

        if (action == "date")
        {
            _awaitingDateByChat[chatId.Value] = DateTimeOffset.UtcNow;
            await EditOrSendAsync(callbackQuery.Message, "Send date as YYYY-MM-DD", ct);
            return;
        }

        try
        {
            var result = pending.IsTask
                ? await SaveTaskFromActionAsync(action, pending, ct)
                : await SaveCaptureFromActionAsync(action, pending, ct);

            ClearPendingState(chatId.Value);
            await EditOrSendAsync(callbackQuery.Message, BuildSavedMessage(result), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save pending content");
            await EditOrSendAsync(callbackQuery.Message, $"Save failed: {ex.Message}", ct);
        }
    }

    private Task<SaveResult> SaveCaptureFromActionAsync(string action, PendingCapture pending, CancellationToken ct)
    {
        return action switch
        {
            "today" => _vaultWriter.SaveToDailyNoteAsync(_vaultWriter.GetLocalDateToday(), pending, ct),
            "yesterday" => _vaultWriter.SaveToDailyNoteAsync(_vaultWriter.GetLocalDateToday().AddDays(-1), pending, ct),
            "inbox" => _vaultWriter.SaveToInboxAsync(pending, ct),
            _ => throw new InvalidOperationException("Unknown action.")
        };
    }

    private Task<SaveResult> SaveTaskFromActionAsync(string action, PendingCapture pending, CancellationToken ct)
    {
        var taskText = pending.TextContent?.Trim();
        if (string.IsNullOrWhiteSpace(taskText))
        {
            throw new InvalidOperationException("Task text is empty.");
        }

        return action switch
        {
            "task:today" => _vaultWriter.SaveTaskToDailyNoteAsync(_vaultWriter.GetLocalDateToday(), taskText, ct),
            "task:tomorrow" => _vaultWriter.SaveTaskToDailyNoteAsync(_vaultWriter.GetLocalDateToday().AddDays(1), taskText, ct),
            "task:inbox" => _vaultWriter.SaveTaskToInboxAsync(taskText, ct),
            _ => throw new InvalidOperationException("Unknown action.")
        };
    }

    private async Task HandleDateInputAsync(long chatId, string text, CancellationToken ct)
    {
        if (!_pendingByChat.TryGetValue(chatId, out var pending))
        {
            _awaitingDateByChat.TryRemove(chatId, out _);
            await _bot.SendTextMessageAsync(
                chatId,
                "Session expired. Send content again.",
                replyMarkup: TelegramKeyboards.BuildMainReplyKeyboard(),
                cancellationToken: ct);
            return;
        }

        if (!TryParseDate(text, out var date))
        {
            await _bot.SendTextMessageAsync(
                chatId,
                "Invalid date. Use YYYY-MM-DD.",
                replyMarkup: TelegramKeyboards.BuildMainReplyKeyboard(),
                cancellationToken: ct);
            return;
        }

        try
        {
            var result = await _vaultWriter.SaveToDailyNoteAsync(date, pending, ct);
            ClearPendingState(chatId);

            await _bot.SendTextMessageAsync(
                chatId,
                BuildSavedMessage(result),
                replyMarkup: TelegramKeyboards.BuildMainReplyKeyboard(),
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save content to {Date}", date);
            await _bot.SendTextMessageAsync(
                chatId,
                $"Save failed: {ex.Message}",
                replyMarkup: TelegramKeyboards.BuildMainReplyKeyboard(),
                cancellationToken: ct);
        }
    }

    private async Task PromptDestinationAsync(long chatId, PendingCapture pending, CancellationToken ct)
    {
        if (!pending.HasContent)
        {
            await _bot.SendTextMessageAsync(
                chatId,
                "Nothing to save.",
                replyMarkup: TelegramKeyboards.BuildMainReplyKeyboard(),
                cancellationToken: ct);
            return;
        }

        _pendingByChat[chatId] = pending;
        _awaitingDateByChat.TryRemove(chatId, out _);

        await _bot.SendTextMessageAsync(
            chatId,
            pending.IsTask ? "Where should I add this task?" : "Where should I save it?",
            replyMarkup: pending.IsTask
                ? TelegramKeyboards.BuildTaskDestinationKeyboard()
                : TelegramKeyboards.BuildDestinationKeyboard(),
            cancellationToken: ct);
    }

    private async Task<byte[]> DownloadFileAsync(string fileId, CancellationToken ct)
    {
        var file = await _bot.GetFileAsync(fileId, ct);
        if (file.FilePath is null)
        {
            throw new InvalidOperationException("Telegram file path is empty.");
        }

        await using var stream = new MemoryStream();
        await _bot.DownloadFileAsync(file.FilePath, stream, ct);
        return stream.ToArray();
    }

    private async Task EditOrSendAsync(Message? source, string text, CancellationToken ct)
    {
        if (source is null)
        {
            return;
        }

        try
        {
            await _bot.EditMessageTextAsync(source.Chat.Id, source.MessageId, text, replyMarkup: null, cancellationToken: ct);
        }
        catch
        {
            await _bot.SendTextMessageAsync(
                source.Chat.Id,
                text,
                replyMarkup: TelegramKeyboards.BuildMainReplyKeyboard(),
                cancellationToken: ct);
        }
    }

    private void ClearPendingState(long chatId)
    {
        _pendingByChat.TryRemove(chatId, out _);
        _awaitingDateByChat.TryRemove(chatId, out _);
    }

    private static bool TryParseDate(string text, out DateOnly date)
    {
        return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
            || DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static string BuildSavedMessage(SaveResult result)
    {
        var message = $"Saved to {result.Target}: {result.NotePath}";
        if (!string.IsNullOrWhiteSpace(result.MediaPath))
        {
            message += $"\nMedia: {result.MediaPath}";
        }

        return message;
    }
}