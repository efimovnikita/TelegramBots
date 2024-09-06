using Bot.VisualMigraineDiary.Models;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Bot.VisualMigraineDiary.Pipelines;

public class NotesPipelineItem : IPipelineItem
{
    public Task<Message> AskAQuestion(Message message, ITelegramBotClient botClient, object? migraineEventObject = null)
    {
        return botClient.SendTextMessageAsync(message.Chat.Id, 
            "Enter notes about the migraine or write `no`, if there are no notes.", 
            parseMode: ParseMode.Markdown);
    }

    public (bool, Task<Message>?) ValidateInput(Message message, ITelegramBotClient botClient)
    {
        if (message.Type != MessageType.Text)
        {
            return (false,
                botClient.SendTextMessageAsync(message.Chat.Id, "Expected a text message. Repeat the input."));
        }

        return (true, null);
    }

    public (bool, Task<Message>?) DoTheJob(Message message, object affectedObject, ITelegramBotClient botClient)
    {
        if (affectedObject is not MigraineEvent migraineEvent)
        {
            return (false,
                botClient.SendTextMessageAsync(message.Chat.Id, "The object to edit has the wrong type. Repeat the input."));
        }

        migraineEvent.Notes = message.Text!.ToLower() == "no" ? string.Empty : message.Text!;

        return (true, null);
    }
}