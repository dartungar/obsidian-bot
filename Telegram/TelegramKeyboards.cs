using Telegram.Bot.Types.ReplyMarkups;

namespace ObsidianBot.Telegram;

public static class TelegramKeyboards
{
    public static ReplyKeyboardMarkup BuildMainReplyKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new[] { new KeyboardButton("/add") },
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