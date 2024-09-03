using Telegram.Bot;
using Telegram.Bot.Types;

namespace Bot.VisualMigraineDiary.Pipelines;

public interface IPipeline
{
    Task<Message> AskAQuestionForNextItem(Message message, ITelegramBotClient botClient);
    Task<Message> ProcessCurrentItem(Message message, ITelegramBotClient botClient);
    bool IsPipelineQueueEmpty();
}