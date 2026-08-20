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
    private readonly VaultSearchService _vaultSearch;
    private readonly ConcurrentDictionary<long, PendingCapture> _pendingByChat = new();
    private readonly ConcurrentDictionary<long, DateTimeOffset> _awaitingDateByChat = new();

    private int _offset;

    public ObsidianBotService(
        ILogger<ObsidianBotService> logger,
        ObsidianBotOptions options,
        ITelegramBotClient bot,
        ObsidianVaultWriter vaultWriter,
        VaultSearchService vaultSearch)
    {
        _logger = logger;
        _options = options;
        _bot = bot;
        _vaultWriter = vaultWriter;
        _vaultSearch = vaultSearch;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initialized = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!initialized)
                {
                    var me = await _bot.GetMeAsync(stoppingToken);
                    await ConfigureCommandsAsync(stoppingToken);
                    _logger.LogInformation("Obsidian bot started as @{Username}", me.Username);
                    initialized = true;
                }

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
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
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

        if (TryGetCommandArgument(text, "/search", out var fullTextQuery))
        {
            await HandleSearchAsync(chatId, fullTextQuery, semantic: false, ct);
            return;
        }

        if (TryGetCommandArgument(text, "/semantic", out var semanticQuery))
        {
            await HandleSearchAsync(chatId, semanticQuery, semantic: true, ct);
            return;
        }

        if (text.StartsWith('/'))
        {
            await _bot.SendTextMessageAsync(
                chatId,
                "Unknown command.",
                replyMarkup: TelegramKeyboards.BuildMainReplyKeyboard(),
                cancellationToken: ct);
            return;
        }

        await PromptDestinationAsync(chatId, new PendingCapture { TextContent = text }, ct);
    }

    private async Task HandleSearchAsync(long chatId, string query, bool semantic, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            var command = semantic ? "/semantic" : "/search";
            await _bot.SendTextMessageAsync(
                chatId,
                $"Usage: {command} your search terms",
                replyMarkup: TelegramKeyboards.BuildMainReplyKeyboard(),
                cancellationToken: ct);
            return;
        }

        try
        {
            var message = semantic
                ? SearchResultMessageFormatter.FormatSemantic(await _vaultSearch.SearchSemanticAsync(query, ct))
                : SearchResultMessageFormatter.FormatCombined(await _vaultSearch.SearchCombinedAsync(query, ct));
            await _bot.SendTextMessageAsync(
                chatId,
                message.Text,
                entities: message.Entities,
                replyMarkup: TelegramKeyboards.BuildMainReplyKeyboard(),
                cancellationToken: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{SearchType} search failed", semantic ? "Semantic" : "Full-text");
            await _bot.SendTextMessageAsync(
                chatId,
                $"Search failed: {ex.Message}",
                replyMarkup: TelegramKeyboards.BuildMainReplyKeyboard(),
                cancellationToken: ct);
        }
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
            var isTaskAction = action.StartsWith("task:", StringComparison.Ordinal);
            var result = pending.IsTask || isTaskAction
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
                : TelegramKeyboards.BuildDestinationKeyboard(ShouldUseTextDestinationKeyboard(pending)),
            cancellationToken: ct);
    }

    private static bool ShouldUseTextDestinationKeyboard(PendingCapture pending)
    {
        return !pending.IsTask
            && pending.MediaBytes is null
            && !string.IsNullOrWhiteSpace(pending.TextContent);
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

    private static bool TryGetCommandArgument(string text, string command, out string argument)
    {
        if (!text.StartsWith(command, StringComparison.OrdinalIgnoreCase) ||
            (text.Length > command.Length && !char.IsWhiteSpace(text[command.Length])))
        {
            argument = string.Empty;
            return false;
        }

        argument = text[command.Length..].Trim();
        return true;
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
