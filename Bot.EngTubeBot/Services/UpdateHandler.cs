using System.Text.Json;
using Bot.EngTubeBot.Models;
using BotSharedLibrary;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Refit;
using StackExchange.Redis;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;
using Exception = System.Exception;
using File = System.IO.File;

namespace Bot.EngTubeBot.Services;

public class UpdateHandler(
    ITelegramBotClient bot,
    ILogger<UpdateHandler> logger,
    IHttpClientFactory clientFactory,
    IConfiguration configuration,
    IOptions<RedisSettings> redisSettings,
    IFileSharingApi fileSharingApi,
    IAuthApi authApi,
    IAudioApi audioApi)
    : IUpdateHandler
{
    private readonly Dictionary<long, UserSettings> _inMemorySettings = [];
    private readonly ConnectionMultiplexer _redisConnection = ConnectionMultiplexer.Connect(redisSettings.Value.ConnectionString);

    private static readonly TimeSpan WaitInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxWaitTime = TimeSpan.FromMinutes(10);
    
    private const string KeyCommand = "/key";
    private const string PromptCommand = "/prompt";
    private const string InjectCommand = "/inject";
    private const string BotconfigurationClientid = "BotConfiguration:ClientId";
    private const string BotconfigurationClientsecret = "BotConfiguration:ClientSecret";
    private const string? MultipartFormData = "multipart/form-data";

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
                KeyCommand => SetKey(msg),
                PromptCommand => SetPrompt(msg),
                InjectCommand => SetInjection(msg),
                _ => DefaultBehaviour(msg)
            });
            
            logger.LogInformation("The message was sent with id: {SentMessageId}", sentMessage.MessageId);
        }
        
        if (messageType == MessageType.Audio)
        {
            var messageAudio = msg.Audio;
            if (messageAudio == null)
                return;

            var sentMessage = await DefaultAudioBehaviour(msg);
            
            logger.LogInformation("The message was sent with id: {SentMessageId}", sentMessage.MessageId);
        }
    }

    private async Task<Message> SetInjection(Message msg)
    {
        var getSettingsResult = _inMemorySettings.TryGetValue(msg.Chat.Id, out var settings);
        if (getSettingsResult == false)
        {
            settings = new UserSettings("", "");
            _inMemorySettings.Add(msg.Chat.Id, settings);
        }
        
        _inMemorySettings[msg.Chat.Id].InjectLanguage = !_inMemorySettings[msg.Chat.Id].InjectLanguage;
        
        return await bot.SendTextMessageAsync(msg.Chat, $"The language injection was set to: {_inMemorySettings[msg.Chat.Id].InjectLanguage}",
            replyParameters: new ReplyParameters { MessageId = msg.MessageId });
    }

    private async Task<Message> DefaultAudioBehaviour(Message msg)
    {
        var waitingMessage = await bot.SendTextMessageAsync(msg.Chat, "\u23f3");

        try
        {
            // first of all wee need to get settings from memory storage
            var getSettingsResult = _inMemorySettings.TryGetValue(msg.Chat.Id, out var settings);
            if (getSettingsResult == false)
            {
                var userSettings = new UserSettings("", "");
                _inMemorySettings.Add(msg.Chat.Id, userSettings);

                return await bot.SendTextMessageAsync(msg.Chat, "You need to setup your OpenAI API key",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            var msgAudio = msg.Audio;

            if (msgAudio == null)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "The audio message is empty",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            Telegram.Bot.Types.File file = await bot.GetFileAsync(msgAudio.FileId);

            if (file.FileSize == null)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Unable to determine the file size",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            var sizeInMegabytes = (double)file.FileSize / (1024 * 1024);
            if (sizeInMegabytes > 19)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "The audio file is too big",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            if (file.FilePath == null)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Unable to determine the file path",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            var filePath = GetFilePath();
            await using (FileStream fileStream = File.OpenWrite(filePath))
            {
                await bot.DownloadFileAsync(file.FilePath, fileStream);
            }

            if (File.Exists(filePath) == false)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Error while saving the file",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length == 0)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Error while saving the file",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            return await SendToCloudAndGetTranslation(msg, filePath, settings);
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

    private async Task<Message> SetPrompt(Message msg)
    {
        var getSettingsResult = _inMemorySettings.TryGetValue(msg.Chat.Id, out var settings);
        if (getSettingsResult == false)
        {
            settings = new UserSettings("", "");
            _inMemorySettings.Add(msg.Chat.Id, settings);
        }
        
        var msgText = msg.Text;
        if (msgText == null)
        {
            return await bot.SendTextMessageAsync(msg.Chat, "Unable to set the prompt",
                replyParameters: new ReplyParameters { MessageId = msg.MessageId });
        }
        
        var prompt = msgText.Replace(PromptCommand, "").Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return await bot.SendTextMessageAsync(msg.Chat, "Unable to set the prompt",
                replyParameters: new ReplyParameters { MessageId = msg.MessageId });
        }
        
        _inMemorySettings[msg.Chat.Id].Prompt = prompt;
        
        return await bot.SendTextMessageAsync(msg.Chat, "The prompt was set",
            replyParameters: new ReplyParameters { MessageId = msg.MessageId });
    }

    private async Task<Message> SetKey(Message msg)
    {
        var getSettingsResult = _inMemorySettings.TryGetValue(msg.Chat.Id, out var settings);
        if (getSettingsResult == false)
        {
            settings = new UserSettings("", "");
            _inMemorySettings.Add(msg.Chat.Id, settings);
        }
        
        var msgText = msg.Text;
        if (msgText == null)
        {
            return await bot.SendTextMessageAsync(msg.Chat, "Unable to set the key",
                replyParameters: new ReplyParameters { MessageId = msg.MessageId });
        }

        var key = msgText.Replace(KeyCommand, "").Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            return await bot.SendTextMessageAsync(msg.Chat, "Unable to set the key",
                replyParameters: new ReplyParameters { MessageId = msg.MessageId });
        }

        _inMemorySettings[msg.Chat.Id].Key = key;
        
        return await bot.SendTextMessageAsync(msg.Chat, "The key was set",
            replyParameters: new ReplyParameters { MessageId = msg.MessageId });
    }

    private static async Task<(bool, byte[]? data)> DownloadMp3FileAsync(HttpClient httpClient, string url)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            var response = await httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                if (response.Content.Headers.ContentType is { MediaType: not null } &&
                    response.Content.Headers.ContentType.MediaType.Equals("audio/mpeg", StringComparison.OrdinalIgnoreCase))
                {
                    // Read the content as byte array
                    var data = await response.Content.ReadAsByteArrayAsync();
                    return (true, data);
                }
            }
        }
        catch
        {
            // Handle exceptions if needed
        }

        return (false, null);
    }

    private async Task<Message> CheckTranslationStatusAsync(string jobId, Message msg,
        UserSettings? settings)
    {
        var startTime = DateTime.UtcNow;

        while (DateTime.UtcNow - startTime < MaxWaitTime)
        {
            var jobStatus = await audioApi.CheckTranslationStatus(jobId);

            if (jobStatus.Status == "Failed")
            {
                return await bot.SendTextMessageAsync(msg.Chat, $"Something went wrong.\n\nThe error message:\n{jobStatus.Error}",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            if (jobStatus.Status == "Succeeded")
            {
                if (string.IsNullOrWhiteSpace(jobStatus.Result))
                {
                    return await bot.SendTextMessageAsync(msg.Chat, "Result from the status audio endpoint is empty",
                        replyParameters: new ReplyParameters { MessageId = msg.MessageId });
                }

                if (settings is { InjectLanguage: true })
                {
                    var requestForItalianInject = new LanguageInjectionRequest(msg.Chat.Id, jobStatus.Result);
                    var json = JsonSerializer.Serialize(requestForItalianInject);
                    var subscriber = _redisConnection.GetSubscriber();
                    await subscriber.PublishAsync(redisSettings.Value.ChannelName, json);
                }

                if (jobStatus.Result.Length > 4000)
                {
                    var data = new Dictionary<string, string> {
                        { IAuthApi.GrantType, IAuthApi.ClientCredentials },
                        { IAuthApi.ClientId, configuration[BotconfigurationClientid] ?? "" },
                        { IAuthApi.ClientSecret, configuration[BotconfigurationClientsecret] ?? "" }
                    };

                    var authData = await authApi.GetAuthData(data);

                    // create a new htm file
                    var htmFileContent = $"""
                                         <!DOCTYPE html>
                                         <html lang="en">
                                         <head>
                                             <meta charset="UTF-8">
                                             <meta name="viewport" content="width=device-width, initial-scale=1.0">
                                             <title>Translation</title>
                                         </head>
                                         <body>
                                             <main>
                                                <p>
                                                    {jobStatus.Result}
                                                </p>
                                             </main>
                                         </body>
                                         </html>
                                         """;

                    var htmFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.htm");
                    await File.WriteAllTextAsync(htmFilePath, htmFileContent);
                    
                    var fileStream = File.OpenRead(htmFilePath);
                    var streamPart = new StreamPart(fileStream, Path.GetFileName(htmFilePath), MultipartFormData);
                    
                    var uploadData = await fileSharingApi.UploadFile($"Bearer {authData.AccessToken}", streamPart);

                    return await bot.SendTextMessageAsync(msg.Chat, $"<a href=\"{uploadData.FileUrl}\">Translation</a>",
                        replyParameters: new ReplyParameters { MessageId = msg.MessageId },
                        parseMode: ParseMode.Html);
                }

                return await bot.SendTextMessageAsync(msg.Chat, jobStatus.Result,
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            await Task.Delay(WaitInterval);
        }

        return await bot.SendTextMessageAsync(msg.Chat, $"Translation job did not succeed within the allowed time. The job id: {jobId}",
            replyParameters: new ReplyParameters { MessageId = msg.MessageId });
    }

    private async Task<Message> DefaultBehaviour(Message msg)
    {
        var waitingMessage = await bot.SendTextMessageAsync(msg.Chat, "\u23f3");
        
        try
        {
            // first of all wee need to get settings from memory storage
            var getSettingsResult = _inMemorySettings.TryGetValue(msg.Chat.Id, out var settings);
            if (getSettingsResult == false)
            {
                var userSettings = new UserSettings("", "");
                _inMemorySettings.Add(msg.Chat.Id, userSettings);

                return await bot.SendTextMessageAsync(msg.Chat, "You need to setup your OpenAI API key",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            var msgText = msg.Text;
            if (msgText == null)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Message text is null",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            var allowedServerUrl = configuration["Urls:GatewayBaseAddress"];
            if (string.IsNullOrWhiteSpace(allowedServerUrl))
            {
                return await bot.SendTextMessageAsync(msg.Chat,
                    "Unable to get the allowed server url",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            if (msgText.StartsWith(allowedServerUrl) == false)
            {
                return await bot.SendTextMessageAsync(msg.Chat,
                    "Only the direct links to audio files are allowed. Or your file sharing server is not allowed.",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            var urlToMp3File = msgText.Split(' ')[0];
            if (string.IsNullOrEmpty(urlToMp3File))
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Url is empty",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            var httpClient = clientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromMinutes(5);

            var downloadResult = await DownloadMp3FileAsync(httpClient, urlToMp3File);
            if (downloadResult.Item1 == false)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Only the direct mp3 file links are allowed",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            var bytes = downloadResult.data;
            
            if (bytes == null)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "File is empty",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            if (bytes is { Length: 0 })
            {
                return await bot.SendTextMessageAsync(msg.Chat, "File is empty",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            // first of all - save the file
            var filePath = await SaveBytesToFile(bytes);

            return await SendToCloudAndGetTranslation(msg, filePath, settings);
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

    private async Task<Message> SendToCloudAndGetTranslation(Message msg, string filePath, UserSettings? settings)
    {
        var languageFromAudio = await GetLanguageDetectionResult(filePath);
        if (languageFromAudio.Language.Equals("en", StringComparison.InvariantCultureIgnoreCase))
        {
            return await bot.SendTextMessageAsync(msg.Chat, "The detected language is English. We are working only with other languages.",
                replyParameters: new ReplyParameters { MessageId = msg.MessageId });
        }
        
        await audioApi.CheckHealth();

        var data = new Dictionary<string, string> {
            { IAuthApi.GrantType, IAuthApi.ClientCredentials },
            { IAuthApi.ClientId, configuration[BotconfigurationClientid] ?? "" },
            { IAuthApi.ClientSecret, configuration[BotconfigurationClientsecret] ?? "" }
        };

        var authData = authApi.GetAuthData(data).ConfigureAwait(false).GetAwaiter().GetResult();
        
        var fileStream = File.OpenRead(filePath);
        var streamPart = new StreamPart(fileStream, Path.GetFileName(filePath), MultipartFormData);
        
        var jobInfo = await audioApi.MakeTranslationFromAudio($"Bearer {authData.AccessToken}",
            streamPart,
            settings?.Prompt ?? "",
            settings?.Key ?? "");

        if (string.IsNullOrWhiteSpace(jobInfo.JobId))
        {
            return await bot.SendTextMessageAsync(msg.Chat, "Response from audio endpoint was unsuccessful",
                replyParameters: new ReplyParameters { MessageId = msg.MessageId });
        }

        return await CheckTranslationStatusAsync(jobInfo.JobId, msg, settings);
    }

    private async Task<LanguageDetectionResult> GetLanguageDetectionResult(string filePath)
    {
        var data = new Dictionary<string, string> {
            { IAuthApi.GrantType, IAuthApi.ClientCredentials },
            { IAuthApi.ClientId, configuration[BotconfigurationClientid] ?? "" },
            { IAuthApi.ClientSecret, configuration[BotconfigurationClientsecret] ?? "" }
        };

        var authData = authApi.GetAuthData(data).ConfigureAwait(false).GetAwaiter().GetResult();
            
        var fs = File.OpenRead(filePath);
        var streamPart = new StreamPart(fs, Path.GetFileName(filePath), MultipartFormData);

        var languageFromAudio =
            await audioApi.DetectLanguageFromAudio($"Bearer {authData.AccessToken}", streamPart);
        return languageFromAudio;
    }

    private async Task<string> SaveBytesToFile(byte[] bytes)
    {
        var filePath = GetFilePath();
        logger.LogInformation("File will be saved as: {filePath}", filePath);

        await using (var fileStream = new FileStream(filePath, FileMode.Create))
        {
            await fileStream.WriteAsync(bytes, 0, bytes.Length);
            logger.LogInformation("File copy completed");
        }

        return filePath;
    }

    private static string GetFilePath()
    {
        var uniqueFileName = Guid.NewGuid() + ".mp3";
        var filePath = Path.Combine(Path.GetTempPath(), uniqueFileName);
        return filePath;
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