using Bot.VisualMigraineDiary.Models;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Bot.VisualMigraineDiary.Pipelines;

public class ScotomaIntensityPipelineItem : IPipelineItem
{
    public Task<Message> AskAQuestion(Message message, ITelegramBotClient botClient, object? migraineEventObject = null)
    {
        return botClient.SendTextMessageAsync(message.Chat.Id,
            "Оцените интенсивность боли по шкале от 1 до 5, где 1 - легкая скотома, а 5 - интенсивная скотома.");
    }

    public (bool, Task<Message>?) ValidateInput(Message message, ITelegramBotClient botClient)
    {
        if (message.Type != MessageType.Text)
        {
            return (false,
                botClient.SendTextMessageAsync(message.Chat.Id, "Ожидалось текстовое сообщение. Повторите ввод."));
        }

        if (!int.TryParse(message.Text, out var intensity) || intensity < 1 || intensity > 5)
        {
            return (false,
                botClient.SendTextMessageAsync(message.Chat.Id, "Пожалуйста, введите число от 1 до 5."));
        }

        return (true, null);
    }

    public (bool, Task<Message>?) DoTheJob(Message message, object affectedObject, ITelegramBotClient botClient)
    {
        if (affectedObject is not MigraineEvent migraineEvent)
        {
            return (false,
                botClient.SendTextMessageAsync(message.Chat.Id, "Редактируемый объект неверного типа. Повторите ввод."));
        }

        migraineEvent.ScotomaSeverity = (ScotomaSeverity)int.Parse(message.Text!);
        return (true, null);
    }
}