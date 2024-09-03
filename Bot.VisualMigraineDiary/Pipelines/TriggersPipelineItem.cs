using Bot.VisualMigraineDiary.Models;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using System.Text;

namespace Bot.VisualMigraineDiary.Pipelines;

public class TriggersPipelineItem : IPipelineItem
{
    public Task<Message> AskAQuestion(Message message, ITelegramBotClient botClient, object? migraineEventObject = null)
    {
        var triggerOptions = string.Join(", ", Enum.GetNames(typeof(TriggerType)).Select(name => $"`{name}`"));
        var sb = new StringBuilder();
        sb.AppendLine("Укажите триггеры мигрени через запятую. Доступные варианты:");
        sb.AppendLine();
        sb.AppendLine(triggerOptions);
        sb.AppendLine();
        sb.AppendLine("Если триггеров нет, напишите `нет`.");

        return botClient.SendTextMessageAsync(message.Chat.Id, sb.ToString(), parseMode: ParseMode.Markdown);
    }

    public (bool, Task<Message>?) ValidateInput(Message message, ITelegramBotClient botClient)
    {
        if (message.Type != MessageType.Text)
        {
            return (false,
                botClient.SendTextMessageAsync(message.Chat.Id, "Ожидалось текстовое сообщение. Повторите ввод."));
        }

        if (message.Text!.ToLower() == "нет")
        {
            return (true, null);
        }

        var triggerStrings = message.Text!.Split(',').Select(t => t.Trim());
        var invalidTriggers = triggerStrings.Where(t => !Enum.TryParse<TriggerType>(t, true, out _)).ToList();

        if (invalidTriggers.Any())
        {
            var invalidTriggersString = string.Join(", ", invalidTriggers);
            return (false,
                botClient.SendTextMessageAsync(message.Chat.Id, 
                    $"Следующие триггеры недопустимы: {invalidTriggersString}. Пожалуйста, используйте только предложенные варианты."));
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

        if (message.Text!.ToLower() == "нет")
        {
            migraineEvent.Triggers = new List<TriggerType>();
        }
        else
        {
            var triggerStrings = message.Text!.Split(',').Select(t => t.Trim());
            migraineEvent.Triggers = triggerStrings
                .Select(t => Enum.Parse<TriggerType>(t, true))
                .ToList();
        }

        return (true, null);
    }
}