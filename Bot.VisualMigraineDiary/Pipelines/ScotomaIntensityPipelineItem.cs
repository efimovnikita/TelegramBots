using Bot.VisualMigraineDiary.Models;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Bot.VisualMigraineDiary.Pipelines;

public class ScotomaIntensityPipelineItem : IPipelineItem
{
    public Task<Message> AskAQuestion(Message message, ITelegramBotClient botClient, object? migraineEventObject = null)
    {
        var replyMarkup = new ReplyKeyboardMarkup(true)
            .AddNewRow("1", "2", "3", "4", "5");
        replyMarkup.OneTimeKeyboard = true;
        
        return botClient.SendTextMessageAsync(message.Chat.Id,
            "Evaluate the intensity of the pain on a scale of 1 to 5, where 1 is a mild scotoma, and 5 is an intense scotoma.",
            replyMarkup: replyMarkup);
    }

    public (bool, Task<Message>?) ValidateInput(Message message, ITelegramBotClient botClient)
    {
        if (message.Type != MessageType.Text)
        {
            return (false,
                botClient.SendTextMessageAsync(message.Chat.Id, "Expected a text message. Repeat the input."));
        }

        if (!int.TryParse(message.Text, out var intensity) || intensity < 1 || intensity > 5)
        {
            return (false,
                botClient.SendTextMessageAsync(message.Chat.Id, "Please enter a number from 1 to 5."));
        }

        return (true, null);
    }

    public (bool, Task<Message>?) DoTheJob(Message message, object affectedObject, ITelegramBotClient botClient)
    {
        if (affectedObject is not MigraineEvent migraineEvent)
        {
            return (false,
                botClient.SendTextMessageAsync(message.Chat.Id, "The object to edit has the wrong type. Please repeat the input."));
        }

        migraineEvent.ScotomaSeverity = (ScotomaSeverity)int.Parse(message.Text!);
        return (true, null);
    }
}