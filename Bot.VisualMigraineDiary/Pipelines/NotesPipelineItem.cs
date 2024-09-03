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
            "Введите заметки о мигрени или напишите `нет`, если заметок нет.", 
            parseMode: ParseMode.Markdown);
    }

    public (bool, Task<Message>?) ValidateInput(Message message, ITelegramBotClient botClient)
    {
        if (message.Type != MessageType.Text)
        {
            return (false,
                botClient.SendTextMessageAsync(message.Chat.Id, "Ожидалось текстовое сообщение. Повторите ввод."));
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

        migraineEvent.Notes = message.Text!.ToLower() == "нет" ? string.Empty : message.Text!;

        return (true, null);
    }
}