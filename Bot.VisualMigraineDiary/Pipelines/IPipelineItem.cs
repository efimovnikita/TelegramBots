using Telegram.Bot;
using Telegram.Bot.Types;

namespace Bot.VisualMigraineDiary.Pipelines;

public interface IPipelineItem
{
    Task<Message> AskAQuestion(Message message, ITelegramBotClient botClient, object? violetObject = null);
    (bool, Task<Message>?) ValidateInput(Message message, ITelegramBotClient botClient);
    (bool, Task<Message>?) DoTheJob(Message message, object affectedObject, ITelegramBotClient botClient);
}