using Telegram.Bot.Types.ReplyMarkups;
using Telegram.Bot.Types;

namespace ObsidianBot.Telegram;

public static class TelegramKeyboards
{
    public static IReadOnlyList<BotCommand> GetBotCommands()
    {
        return
        [
            new BotCommand { Command = "start", Description = "Show usage help" },
            new BotCommand { Command = "help", Description = "Show usage help" },
            new BotCommand { Command = "add", Description = "Quick add a text note" },
            new BotCommand { Command = "cancel", Description = "Cancel the current save flow" }
        ];
    }

    public static ReplyKeyboardMarkup BuildMainReplyKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new[] { new KeyboardButton("/add"), new KeyboardButton("/cancel") },
            new[] { new KeyboardButton("/help") }
        })
        {
            ResizeKeyboard = true
        };
    }

    public static InlineKeyboardMarkup BuildDestinationKeyboard()
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("Today's daily note", "obs:today") },
            new[] { InlineKeyboardButton.WithCallbackData("Add to yesterday's note", "obs:yesterday") },
            new[] { InlineKeyboardButton.WithCallbackData("Other date", "obs:date") },
            new[] { InlineKeyboardButton.WithCallbackData("Inbox note", "obs:inbox") },
            new[] { InlineKeyboardButton.WithCallbackData("Cancel", "obs:cancel") }
        });
    }
}