using System.Text;
using Bot.YTMusicFetcher.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;
using YoutubeExplode;
using YoutubeExplode.Common;
using Exception = System.Exception;
using Message = Telegram.Bot.Types.Message;

namespace Bot.YTMusicFetcher.Services;

public class UpdateHandler(
    ITelegramBotClient bot,
    ILogger<UpdateHandler> logger,
    IConfiguration configuration,
    IYtMusicApi ytMusicApi,
    IAuthApi authApi) : IUpdateHandler
{
    private readonly List<UserJob> _failedJobsRepository = [];
    private const int MaxChunkSize = 30;
    private const string PlaylistStartUrl = "https://music.youtube.com/playlist";

    private const string RestartCommand = "/restart";
    private static readonly TimeSpan WaitInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MaxWaitTime = TimeSpan.FromMinutes(15);

    public async Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, HandleErrorSource source, CancellationToken cancellationToken)
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
                RestartCommand => RestartJob(msg),
                _ => DefaultBehaviour(msg)
            });
            
            logger.LogInformation("The message was sent with id: {SentMessageId}", sentMessage.MessageId);
        }
    }

    private async Task<Message> RestartJob(Message msg)
    {
        var waitingMessage = await bot.SendTextMessageAsync(msg.Chat, "\u23f3");

        UserJob? userJob = null;
        try
        {
            var text = msg.Text;
            if (text == null)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Message text is null",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            var id = text.Split(' ')[1];
            if (string.IsNullOrEmpty(id))
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Job id is empty",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            var jobs = _failedJobsRepository
                .Where(job => job.UserId.Equals(msg.Chat.Id)).ToArray();
            
            if (jobs.Length == 0)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "You do not have failed jobs.",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            userJob = jobs.FirstOrDefault(job => job.Job.JobId.Equals(id));
            if (userJob == null)
            {
                return await bot.SendTextMessageAsync(msg.Chat, $"You do not have a failed job with id '{id}'.",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            var jb = userJob.Job;
            
            await bot.SendTextMessageAsync(msg.Chat, $"The job with info will be restarted:\n\n{await GetJobTextDescription(jb)}",
                replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            
            await jb.IgniteRemoteJob();
            
            var startTime = DateTime.UtcNow;
            
            while (DateTime.UtcNow - startTime < MaxWaitTime)
            {
                await jb.UpdateStatus();
                
                if (jb.Status is Statuses.SuccessStatus or Statuses.FailedStatus)
                {
                    break;
                }
                
                await Task.Delay(WaitInterval);
            }
            
            if (jb.Status is not Statuses.SuccessStatus)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Unable to do the job.",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }
            
            return await bot.SendTextMessageAsync(msg.Chat, $"This is your direct link: {jb.Result}",
                replyParameters: new ReplyParameters { MessageId = msg.MessageId });
        }
        catch (Exception ex)
        {
            return await bot.SendTextMessageAsync(msg.Chat, ex.GetType() + "\n" + ex.Message,
                replyParameters: new ReplyParameters { MessageId = msg.MessageId });
        }
        finally
        {
            if (userJob != null)
            {
                _failedJobsRepository.Remove(userJob);    
            }
            
            await bot.DeleteMessageAsync(msg.Chat, waitingMessage.MessageId);
        }
    }
    
    private async Task<Message> DefaultBehaviour(Message msg)
    {
        var waitingMessage = await bot.SendTextMessageAsync(msg.Chat, "\u23f3");

        try
        {
            var msgText = msg.Text;
            if (msgText == null)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Message text is null",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            if (msgText.StartsWith(PlaylistStartUrl) == false)
            {
                return await bot.SendTextMessageAsync(msg.Chat,
                    "Only the direct links on youtube playlist are supported.",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            var playlistUrl = msgText.Split(' ')[0];
            if (string.IsNullOrEmpty(playlistUrl))
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Playlist url is empty",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            var youtube = new YoutubeClient();
            var tracks = await youtube.Playlists.GetVideosAsync(playlistUrl);

            var allChunks = tracks.Chunk(MaxChunkSize).ToArray();
            if (allChunks.Length == 0)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Playlist is empty",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            var jobs = new List<Job>();
            foreach (var chunk in allChunks)
            {
                var job = new Job(chunk.Select(video => video.Url).ToArray(), ytMusicApi, authApi, configuration);
                jobs.Add(job);
            }

            var sb = new StringBuilder();
            
            foreach (var job in jobs)
            {
                sb.AppendLine(await GetJobTextDescription(job));
            }
            
            await bot.SendTextMessageAsync(
                chatId: msg.Chat,
                text: $"""
                       You will receive next chunks:
                       
                       {sb}
                       """,
                replyParameters: new ReplyParameters { MessageId = msg.MessageId }
            );
            
            foreach (var job in jobs)
            {
                await job.IgniteRemoteJob();
                await Task.Delay(WaitInterval);
            }
            
            logger.LogInformation("All jobs started...");
            
            foreach (var job in jobs)
            {
                logger.LogInformation("Job ID: {JobId}", job.JobId);
            }
            
            var startTime = DateTime.UtcNow;

            while (DateTime.UtcNow - startTime < MaxWaitTime)
            {
                foreach (var job in jobs)
                {
                    await job.UpdateStatus();
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }

                if (jobs.All(job => job.Status is Statuses.SuccessStatus or Statuses.FailedStatus))
                {
                    break;
                }

                await Task.Delay(WaitInterval);
            }

            var successJobs = jobs.Where(job =>
                job.Status == Statuses.SuccessStatus && string.IsNullOrWhiteSpace(job.Result) == false).ToArray();
                
            await SentHtmlMsg(msg, successJobs);
            
            if (jobs.Any(js => js.Status is not Statuses.SuccessStatus))
            {
                var failedJobs = jobs.Where(js => js.Status is Statuses.FailedStatus or Statuses.UnknownStatus).ToArray();
                foreach (var job in failedJobs)
                {
                    _failedJobsRepository.Add(new UserJob(msg.Chat.Id, job));
                }

                var builder = new StringBuilder();
                foreach (var job in failedJobs)
                {
                    builder.AppendLine($"`/restart {job.JobId}`");
                }

                return await bot.SendTextMessageAsync(msg.Chat, $"Some jobs did not succeed. You can restart failed jobs:\n{builder}",
                    parseMode: ParseMode.Markdown,
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }
            
            return await bot.SendTextMessageAsync(msg.Chat, "These are your direct links \ud83d\udc46",
                replyParameters: new ReplyParameters { MessageId = msg.MessageId });
        }
        catch (Exception ex)
        {
            return await bot.SendTextMessageAsync(msg.Chat, ex.GetType() + "\n" + ex.Message,
                replyParameters: new ReplyParameters { MessageId = msg.MessageId });
        }
        finally
        {
            await bot.DeleteMessageAsync(msg.Chat, waitingMessage.MessageId);
        }
    }

    private static async Task<string> GetJobTextDescription(Job job)
    {
        return $"""
                1. {await job.GetFirstTrackName()}
                ...
                n. {await job.GetLastTrackName()}
                _____________________
                """;
    }

    private async Task SentHtmlMsg(Message msg, Job[] jobs)
    {
        for (var i = 0; i < jobs.Length; i++)
        {
            await bot.SendTextMessageAsync(msg.Chat, $"<a href=\"{jobs[i].Result}\">Part {i + 1}</a>",
                parseMode: ParseMode.Html,
                replyParameters: new ReplyParameters { MessageId = msg.MessageId });

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    #region Inline Mode

    private async Task OnInlineQuery(InlineQuery inlineQuery)
    {
        logger.LogInformation("Received inline query from: {InlineQueryFromId}", inlineQuery.From.Id);

        InlineQueryResult[] results = [ // displayed result
            new InlineQueryResultArticle("1", "Telegram.Bot", new InputTextMessageContent("hello")),
            new InlineQueryResultArticle("2", "is the best", new InputTextMessageContent("world"))
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
}