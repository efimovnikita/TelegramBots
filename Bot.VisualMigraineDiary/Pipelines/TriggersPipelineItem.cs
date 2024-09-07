using Bot.VisualMigraineDiary.Models;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using System.Text;
using Telegram.Bot.Types.ReplyMarkups;

namespace Bot.VisualMigraineDiary.Pipelines;

public class TriggersPipelineItem : IPipelineItem
{
    public Task<Message> AskAQuestion(Message message, ITelegramBotClient botClient, object? migraineEventObject = null)
    {
        var triggerOptions = string.Join(", ", Enum.GetNames(typeof(TriggerType)).Select(name => $"`{name}`"));
        var sb = new StringBuilder();
        sb.AppendLine("Specify migraine triggers separated by commas. Available options:");
        sb.AppendLine();
        sb.AppendLine(triggerOptions);
        sb.AppendLine();
        sb.AppendLine("If there are no triggers, write `no`.");

        var replyMarkup = new ReplyKeyboardMarkup(true)
            .AddNewRow("no");
        replyMarkup.OneTimeKeyboard = true;
        
        return botClient.SendTextMessageAsync(message.Chat.Id, sb.ToString(), parseMode: ParseMode.Markdown,
            replyMarkup: replyMarkup);
    }

    public (bool, Task<Message>?) ValidateInput(Message message, ITelegramBotClient botClient)
    {
        if (message.Type != MessageType.Text)
        {
            return (false,
                botClient.SendTextMessageAsync(message.Chat.Id, "Expected a text message. Please repeat the input."));
        }

        if (message.Text!.ToLower() == "no")
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
                    $"The following triggers are invalid: {invalidTriggersString}. Please use only the suggested options."));
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

        if (message.Text!.ToLower() == "no")
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