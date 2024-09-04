using Bot.VisualMigraineDiary.Models;
using Bot.VisualMigraineDiary.Pipelines;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Refit;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;
using Exception = System.Exception;
using File = System.IO.File;
using Message = Telegram.Bot.Types.Message;

namespace Bot.VisualMigraineDiary.Services;

public class UpdateHandler(
    ITelegramBotClient bot,
    ILogger<UpdateHandler> logger,
    IMemoryStateProvider memoryStateProvider,
    MigraineEventService migraineEventService, 
    IConfiguration configuration, 
    IFileSharingApi fileSharingApi,
    IAuthApi authApi) : IUpdateHandler
{
    public async Task HandleErrorAsync(
        ITelegramBotClient botClient,
        Exception exception,
        HandleErrorSource source,
        CancellationToken cancellationToken
        )
    {
        logger.LogInformation("HandleError: {Exception}", exception);
        // Cooldown in case of network connection error
        if (exception is RequestException)
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
    }

    public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await (update switch
        {
            { Message: { } message }                        => OnMessage(message),
            { EditedMessage: { } message }                  => OnMessage(message),
            { InlineQuery: { } inlineQuery }                => OnInlineQuery(inlineQuery),
            { ChosenInlineResult: { } chosenInlineResult }  => OnChosenInlineResult(chosenInlineResult),
            { Poll: { } poll }                              => OnPoll(poll),
            _                                               => UnknownUpdateHandlerAsync(update)
        });
    }

    private async Task OnMessage(Message msg)
    {
        var messageType = msg.Type;
        logger.LogInformation("Receive message type: {MessageType}", messageType);
        if (messageType == MessageType.Text)
        {
            if (msg.Text is not { } messageText)
                return;

            var sentMessage = await (messageText.Split(' ')[0] switch
            {
                "/start" => StartCommand(msg),
                "/list" => ListEventsCommand(msg),
                "/print" => PrintCommand(msg),
                _ => ProcessCommand(msg)
            });
            
            logger.LogInformation("The message was sent with id: {SentMessageId}", sentMessage.MessageId);
        }
    }

    private async Task<Message> PrintCommand(Message msg)
    {
        await fileSharingApi.CheckHealth();
        
        var data = new Dictionary<string, string> {
            { IAuthApi.GrantType, IAuthApi.ClientCredentials },
            { IAuthApi.ClientId, configuration["BotConfiguration:ClientId"] ?? "" },
            { IAuthApi.ClientSecret, configuration["BotConfiguration:ClientSecret"] ?? "" }
        };

        var authData = await authApi.GetAuthData(data);
        
        var table = new CalendarTable(DateTime.Now, migraineEventService);
        var html = table.PrintTableAsHtml();

        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.html");
        await File.WriteAllTextAsync(path, html);
        
        if (File.Exists(path))
        {
            var fileInfo = new FileInfo(path);
            if (fileInfo.Length > 0)
            {
                var fileStream = System.IO.File.OpenRead(path);
                var streamPart = new StreamPart(fileStream, Path.GetFileName(path), "multipart/form-data");

                var uploadData = await fileSharingApi.UploadFile($"Bearer {authData.AccessToken}", streamPart);
                
                return await bot.SendTextMessageAsync(
                    chatId: msg.Chat.Id,
                    text: uploadData.FileUrl);        
            }
        }

        return await bot.SendTextMessageAsync(
            chatId: msg.Chat.Id,
            text: "Unable to print the output"); 
    }

    private async Task<Message> ProcessCommand(Message msg)
    {
        var pipeline = memoryStateProvider.GetCurrentPipeline(msg.Chat.Id);
        if (pipeline == null)
        {
            return await bot.SendTextMessageAsync(
                chatId: msg.Chat.Id,
                text: "usage");
        }

        await pipeline.ProcessCurrentItem(msg, bot);

        if (pipeline.IsPipelineQueueEmpty())
        {
            memoryStateProvider.ResetCurrentPipeline(msg.Chat.Id);
        }

        return msg;
    }

    private Task<Message> StartCommand(Message msg)
    {
        try
        {
            var pipeline = new MigraineEventAddingPipeline(
                [
                    new ScotomaIntensityPipelineItem(),
                    new TriggersPipelineItem(),
                    new NotesPipelineItem()
                ],
                migraineEventService);
            
            memoryStateProvider.SetCurrentPipeline(pipeline, msg.Chat.Id);
            return pipeline.AskAQuestionForNextItem(msg, bot);
        }
        catch (Exception ex)
        {
            return bot.SendTextMessageAsync(msg.Chat, ex.GetType() + "\n" + ex.Message,
                replyParameters: new ReplyParameters { MessageId = msg.MessageId });
        }
    }

    #region Inline Mode

    private async Task OnInlineQuery(InlineQuery inlineQuery)
    {
        logger.LogInformation("Received inline query from: {InlineQueryFromId}", inlineQuery.From.Id);

        InlineQueryResult[] results = [ // displayed result
            new InlineQueryResultArticle("1", "Telegram.Bot", new InputTextMessageContent("hello")),
            new InlineQueryResultArticle("2", "is the best", new InputTextMessageContent("world")),
        ];
        await bot.AnswerInlineQueryAsync(inlineQuery.Id, results, cacheTime: 0, isPersonal: true);
    }

    private async Task OnChosenInlineResult(ChosenInlineResult chosenInlineResult)
    {
        logger.LogInformation("Received inline result: {ChosenInlineResultId}", chosenInlineResult.ResultId);
        await bot.SendTextMessageAsync(chosenInlineResult.From.Id, $"You chose result with Id: {chosenInlineResult.ResultId}");
    }

    #endregion

    private Task OnPoll(Poll poll)
    {
        logger.LogInformation("Received Poll info: {Question}", poll.Question);
        return Task.CompletedTask;
    }

    private Task UnknownUpdateHandlerAsync(Update update)
    {
        logger.LogInformation("Unknown update type: {UpdateType}", update.Type);
        return Task.CompletedTask;
    }

    private async Task<Message> ListEventsCommand(Message msg)
    {
        try
        {
            var events = await migraineEventService.GetAsync();
            if (events.Count == 0)
            {
                return await bot.SendTextMessageAsync(msg.Chat.Id, "No migraine events found.");
            }

            var eventList = events.Select(e => e.ToString()).ToList();
            var messageText = $"Here are your recorded migraine events:\n\n";
            messageText += string.Join("\n\n", eventList);

            if (messageText.Length <= 4000)
            {
                return await bot.SendTextMessageAsync(msg.Chat.Id, messageText);
            }

            var lastTenEvents = eventList.TakeLast(10).ToList();
            messageText = "Here are your last 10 recorded migraine events:\n\n";
            messageText += string.Join("\n\n", lastTenEvents);

            return await bot.SendTextMessageAsync(msg.Chat.Id, messageText);
        }
        catch (Exception ex)
        {
            return await bot.SendTextMessageAsync(msg.Chat.Id, 
                $"An error occurred while fetching migraine events: {ex.Message}");
        }
    }
}