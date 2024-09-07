using Bot.VisualMigraineDiary.Models;
using Bot.VisualMigraineDiary.Services;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace Bot.VisualMigraineDiary.Pipelines;

public class MigraineEventAddingPipeline(IPipelineItem[] items, MigraineEventService migraineEventRepository)
    : IPipeline
{
    public Queue<IPipelineItem> PipelineItems { get; set; } = new(items);
    public MigraineEvent CurrentMigraineEvent { get; set; } = new()
    {
        Triggers = [],
        Notes = string.Empty
    };

    public Task<Message> AskAQuestionForNextItem(Message message, ITelegramBotClient botClient)
    {
        var pipelineItem = PipelineItems.Peek();
        return pipelineItem.AskAQuestion(message, botClient, CurrentMigraineEvent);
    }

    public async Task<Message> ProcessCurrentItem(Message message, ITelegramBotClient botClient)
    {
        var pipelineItem = PipelineItems.Peek();
        var (validationResult, validationMsg) = pipelineItem.ValidateInput(message, botClient);
        if (validationResult == false)
        {
            return await validationMsg!;
        }

        var (jobResult, jobMsg) = pipelineItem.DoTheJob(message, CurrentMigraineEvent, botClient);
        if (jobResult == false)
        {
            return await jobMsg!;
        }

        PipelineItems.Dequeue();

        var peekResult = PipelineItems.TryPeek(out var nextItem);
        if (peekResult)
        {
            return await nextItem!.AskAQuestion(message, botClient, CurrentMigraineEvent);
        }

        // Queue is empty
        var sentResult = await SendDataToTheDatabase(CurrentMigraineEvent, botClient, message.Chat.Id);

        return await botClient.SendTextMessageAsync(message.Chat.Id,
            sentResult
                ? "Migraine data has been added to the database."
                : "Failed to add migraine data to the database.", replyMarkup: new ReplyKeyboardRemove());
    }

    public bool IsPipelineQueueEmpty() => PipelineItems.Count == 0;

    private async Task<bool> SendDataToTheDatabase(MigraineEvent migraineEvent, ITelegramBotClient botClient, long chatId)
    {
        try
        {
            await migraineEventRepository.CreateAsync(migraineEvent);
            return true;
        }
        catch (Exception e)
        {
            await botClient.SendTextMessageAsync(chatId, e.Message);
            return false;
        }
    }
}