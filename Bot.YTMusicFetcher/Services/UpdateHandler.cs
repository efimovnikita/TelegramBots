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

public class UserJob(long userId, UpdateHandler.Job job)
{
    public long UserId { get; set; } = userId;
    public UpdateHandler.Job Job { get; set; } = job;
}

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
    private const string SuccessStatus = "Succeeded";
    private const string FailedStatus = "Failed";
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

        try
        {
            var msgText = msg.Text;
            if (msgText == null)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Message text is null",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            var jobId = msgText.Split(' ')[1];
            if (string.IsNullOrEmpty(jobId))
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Job id is empty",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            var userFailedJobs = _failedJobsRepository
                .Where(userJob => userJob.UserId.Equals(msg.Chat.Id)).ToArray();
            
            if (userFailedJobs.Length == 0)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "You do not have failed jobs.",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            var userFailedJob = userFailedJobs.FirstOrDefault(userJob => userJob.Job.JobId.Equals(jobId));
            if (userFailedJob == null)
            {
                return await bot.SendTextMessageAsync(msg.Chat, $"You do not have a failed job with id '{jobId}'.",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }
            
            await ytMusicApi.CheckHealth();
            logger.LogInformation("The music service is ready.");
            
            logger.LogInformation("Attempting to retrieve auth data");

            var data = new Dictionary<string, string> {
                { IAuthApi.GrantType, IAuthApi.ClientCredentials },
                { IAuthApi.ClientId, configuration["BotConfiguration:ClientId"] ?? "" },
                { IAuthApi.ClientSecret, configuration["BotConfiguration:ClientSecret"] ?? "" }
            };

            var authData = await authApi.GetAuthData(data);
            logger.LogInformation("Auth data retrieved successfully");

            var makingArchiveJob = await ytMusicApi.StartMakingArchiveJob($"Bearer {authData.AccessToken}",
                new UrlsRequest { Urls = userFailedJob.Job.Urls });
            
            var startTime = DateTime.UtcNow;

            YtMusicStatusResponseData? statusResponseData = null;
            while (DateTime.UtcNow - startTime < MaxWaitTime)
            {
                statusResponseData = await ytMusicApi.CheckStatus(makingArchiveJob.JobId);
                
                if (statusResponseData.Status is SuccessStatus or FailedStatus)
                {
                    break;
                }

                await Task.Delay(WaitInterval);
            }
            
            if (statusResponseData == null)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Unable to do the job.",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }
            
            if (statusResponseData.Status != SuccessStatus)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Unable to do the job.",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }
            
            _failedJobsRepository.Remove(userFailedJob);
            
            return await bot.SendTextMessageAsync(msg.Chat, $"This is your direct link: {statusResponseData.Result}",
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

    public record Job(string JobId, string[] Urls);

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
            
            await bot.SendTextMessageAsync(msg.Chat, $"The whole playlist contains {tracks.Count} tracks. I will split the playlist into multiple ({allChunks.Length}) chunks. Each chunk will contain maximum {MaxChunkSize} tracks.",
                replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            
            await ytMusicApi.CheckHealth();
            logger.LogInformation("The music service is ready.");
            
            logger.LogInformation("Attempting to retrieve auth data");

            var data = new Dictionary<string, string> {
                { IAuthApi.GrantType, IAuthApi.ClientCredentials },
                { IAuthApi.ClientId, configuration["BotConfiguration:ClientId"] ?? "" },
                { IAuthApi.ClientSecret, configuration["BotConfiguration:ClientSecret"] ?? "" }
            };

            var authData = await authApi.GetAuthData(data);
            logger.LogInformation("Auth data retrieved successfully");

            var jobs = new List<Job>();
            
            foreach (var chunk in allChunks)
            {
                if (authData.ShouldRefreshToken(refreshThresholdSeconds: 180))
                {
                    authData = await authApi.GetAuthData(data);
                }

                var urls = chunk.Select(video => video.Url).ToArray();
                var job = await ytMusicApi.StartMakingArchiveJob($"Bearer {authData.AccessToken}",
                    new UrlsRequest { Urls = urls});

                jobs.Add(new Job(job.JobId, urls));

                await Task.Delay(TimeSpan.FromMinutes(1));
            }
            
            foreach (var job in jobs)
            {
                logger.LogInformation("Job ID: {JobId}", job.JobId);
            }
            
            var startTime = DateTime.UtcNow;
            (string id, string Status, string Result)[] jobStatuses = [];

            while (DateTime.UtcNow - startTime < MaxWaitTime)
            {
                jobStatuses = await Task.WhenAll(jobs.Select(async job =>
                {
                    var status = await ytMusicApi.CheckStatus(job.JobId);
                    return (id: job.JobId, status.Status, status.Result);
                }));

                if (jobStatuses.All(js => js.Status is SuccessStatus or FailedStatus))
                {
                    break;
                }

                await Task.Delay(WaitInterval);
            }

            if (jobStatuses.Any(js => js.Status != SuccessStatus))
            {
                var successJobs = jobStatuses.Where(js => js.Status == SuccessStatus).ToArray();
                
                for (var i = 0; i < successJobs.Length; i++)
                {
                    await SendHtmlResult(msg, successJobs, i);
                }

                var failedJobs = jobStatuses.Where(js => js.Status == FailedStatus).ToArray();
                foreach ((string id, string Status, string Result) tuple in failedJobs)
                {
                    var foundFailedJob = jobs.FirstOrDefault(job => job.JobId.Equals(tuple.id));
                    if (foundFailedJob != null)
                        _failedJobsRepository.Add(new UserJob(msg.Chat.Id, foundFailedJob));
                }

                var builder = new StringBuilder();
                foreach (var (id, _, _) in failedJobs)
                {
                    builder.AppendLine($"`/restart {id}`");
                }

                return await bot.SendTextMessageAsync(msg.Chat, $"Some jobs did not succeed. You can restart failed jobs:\n{builder}",
                    parseMode: ParseMode.Markdown,
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }
            
            for (var i = 0; i < jobStatuses.Length; i++)
            {
                await SendHtmlResult(msg, jobStatuses, i);
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

    private async Task SendHtmlResult(Message msg, (string id, string Status, string Result)[] jobStatuses, int i)
    {
        var (_, _, result) = jobStatuses[i];
        logger.LogInformation("Job result: {Result}", result);
        await bot.SendTextMessageAsync(msg.Chat, $"<a href=\"{result}\">Part {i + 1}</a>",
            parseMode: ParseMode.Html,
            replyParameters: new ReplyParameters { MessageId = msg.MessageId });

        await Task.Delay(TimeSpan.FromSeconds(1));
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