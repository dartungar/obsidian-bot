using Telegram.Bot.Types.ReplyMarkups;
using Telegram.Bot.Types;

namespace ObsidianBot.Telegram;

public static class TelegramKeyboards
{
    public static IReadOnlyList<BotCommand> GetBotCommands()
    {
        return
        [
            new BotCommand { Command = "add", Description = "Quick add a text note" },
            new BotCommand { Command = "cancel", Description = "Cancel the current save flow" }
        ];
    }

    public static ReplyKeyboardMarkup BuildMainReplyKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new[] { new KeyboardButton("/add"), new KeyboardButton("/cancel") }
        })
        {
            ResizeKeyboard = true
        };
    }

    public static InlineKeyboardMarkup BuildTaskDestinationKeyboard()
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("Today's daily note", "obs:task:today") },
            new[] { InlineKeyboardButton.WithCallbackData("Tomorrow's daily note", "obs:task:tomorrow") },
            new[] { InlineKeyboardButton.WithCallbackData("Inbox note", "obs:task:inbox") },
            new[] { InlineKeyboardButton.WithCallbackData("Cancel", "obs:cancel") }
        });
    }

    public static InlineKeyboardMarkup BuildDestinationKeyboard(bool includeTaskActions = false)
    {
        var buttons = new List<InlineKeyboardButton[]>
        {
            new[] { InlineKeyboardButton.WithCallbackData("Today's daily note", "obs:today") }
        };

        if (includeTaskActions)
        {
            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("Add task to today", "obs:task:today") });
            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("Add task to tomorrow", "obs:task:tomorrow") });
        }

        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("Add to yesterday's note", "obs:yesterday") });
        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("Other date", "obs:date") });
        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("Inbox note", "obs:inbox") });
        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("Cancel", "obs:cancel") });

        return new InlineKeyboardMarkup(buttons);
    }
}