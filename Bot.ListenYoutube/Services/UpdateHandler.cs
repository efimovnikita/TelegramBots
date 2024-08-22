using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bot.ListenYoutube.Models;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;
using Telegram.Bot.Types.ReplyMarkups;
using File = System.IO.File;

namespace Bot.ListenYoutube.Services;

public class UpdateHandler(ITelegramBotClient bot, ILogger<UpdateHandler> logger, IHttpClientFactory clientFactory, IConfiguration configuration) : IUpdateHandler
{
    private static readonly InputPollOption[] PollOptions = ["Hello", "World!"];
    private readonly Dictionary<long, AudioMode> _inMemorySettings = [];

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
            { CallbackQuery: { } callbackQuery }            => OnCallbackQuery(callbackQuery),
            { InlineQuery: { } inlineQuery }                => OnInlineQuery(inlineQuery),
            { ChosenInlineResult: { } chosenInlineResult }  => OnChosenInlineResult(chosenInlineResult),
            { Poll: { } poll }                              => OnPoll(poll),
            { PollAnswer: { } pollAnswer }                  => OnPollAnswer(pollAnswer),
            _                                               => UnknownUpdateHandlerAsync(update)
        });
    }

    private async Task OnMessage(Message msg)
    {
        logger.LogInformation("Receive message type: {MessageType}", msg.Type);
        if (msg.Text is not { } messageText)
            return;

        var sentMessage = await (messageText.Split(' ')[0] switch
        {
            "/mode" => SendInlineModeKeyboard(msg),
            _ => DefaultBehaviour(msg)
        });
        logger.LogInformation("The message was sent with id: {SentMessageId}", sentMessage.MessageId);
    }

    private async Task<Message> SendInlineModeKeyboard(Message msg)
    {
        var inlineMarkup = new InlineKeyboardMarkup()
            .AddNewRow("Both", "Voice", "Audio");
        return await bot.SendTextMessageAsync(msg.Chat, "Inline buttons:", replyMarkup: inlineMarkup);
    }

    private async Task<Message> DefaultBehaviour(Message msg)
    {
        var audioFilePath = "";

        try
        {
            // first of all wee need to get settings from memory storage
            var getSettingsResult = _inMemorySettings.TryGetValue(msg.Chat.Id, out var mode);
            if (getSettingsResult == false)
            {
                mode = AudioMode.Both;
                _inMemorySettings.Add(msg.Chat.Id, AudioMode.Both);
            }
            
            logger.LogInformation("Selected message mode: {Mode}", mode);

            var msgText = msg.Text;
            if (msgText == null)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Message text is null",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            if ((msgText.StartsWith("https://youtube.com") || msgText.StartsWith("https://www.youtube.com") ||
                 msgText.StartsWith("https://youtu.be")) == false)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Only the YouTube links are allowed",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            var url = msgText.Split(' ')[0];
            if (string.IsNullOrEmpty(url))
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Url is empty",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            var httpClient = clientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromMinutes(5);

            var healthUrl = configuration["Urls:AudioEndpointHealth"];
            if (healthUrl == null)
            {
                return await bot.SendTextMessageAsync(msg.Chat,
                    "Unable to check the fact that the main endpoint is healthy",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            var healthRequest = new HttpRequestMessage
            {
                Method = HttpMethod.Get,
                RequestUri = new Uri(healthUrl),
            };
            using var healthResponse = await httpClient.SendAsync(healthRequest);
            if (healthResponse.IsSuccessStatusCode == false)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Audio microservice is unhealthy",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            var authServer = configuration["Urls:AuthServer"];
            var clientId = configuration["BotConfiguration:ClientId"];
            var clientSecret = configuration["BotConfiguration:ClientSecret"];

            if ((authServer != null && clientId != null && clientSecret != null) == false)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Unable to authorize the bot",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            var authData = await GetAuthData(httpClient, authServer, clientId, clientSecret);
            if (authData == null)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Unable to authorize the bot",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }
            
            var audioEndpointUrl = configuration["Urls:AudioEndpoint"];
            if (audioEndpointUrl == null)
            {
                return await bot.SendTextMessageAsync(msg.Chat,
                    "Unable to send the request to audio endpoint. Provide url to the endpoint",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            var audioRequest = new HttpRequestMessage
            {
                Method = HttpMethod.Get,
                RequestUri = new Uri($"{audioEndpointUrl}?videoUrl={url}"),
                Headers =
                {
                    { "Authorization", $"Bearer {authData.AccessToken}" },
                },
            };

            using var audioResponse = await httpClient.SendAsync(audioRequest);
            if (audioResponse.IsSuccessStatusCode == false)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Response from audio endpoint was unsuccessful",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            var (_, lastHeaderValue) = audioResponse.Content.Headers.LastOrDefault();
            var attachmentInfo = lastHeaderValue?.LastOrDefault();
            if (string.IsNullOrEmpty(attachmentInfo))
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Unable to get the attachment string",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            const string pattern = """filename="?(.+?)"?;""";
            var match = Regex.Match(attachmentInfo, pattern);

            string filename;
            if (match.Success)
            {
                filename = match.Groups[1].Value;
            }
            else
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Filename not found",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }
            
            if (IsValidFilename(filename) == false)
            {
                // invalid name
                var extension = Path.GetExtension(filename);
                filename = $"{Guid.NewGuid()}{extension}";
            }

            audioFilePath = Path.Combine(Path.GetTempPath(), filename);
            await using (FileStream fileStream = new(audioFilePath, FileMode.Create, FileAccess.Write))
            {
                await audioResponse.Content.CopyToAsync(fileStream);
            }
            
            var fileInfo = new FileInfo(audioFilePath);
            var sizeInBytes = fileInfo.Length;
            var sizeInMegabytes = (double) sizeInBytes / (1024 * 1024);
            
            if (sizeInMegabytes < 49)
            {
                switch (mode)
                {
                    case AudioMode.Voice:
                    {
                        await using var voiceStream = File.OpenRead(audioFilePath);
                        return await bot.SendVoiceAsync(msg.Chat,
                            InputFile.FromStream(voiceStream, Path.GetFileName(audioFilePath)),
                            replyParameters: new ReplyParameters { MessageId = msg.MessageId });
                    }
                    case AudioMode.Audio:
                    {
                        await using var audioStream = File.OpenRead(audioFilePath);
                        return await bot.SendAudioAsync(msg.Chat,
                            InputFile.FromStream(audioStream, Path.GetFileName(audioFilePath)),
                            replyParameters: new ReplyParameters { MessageId = msg.MessageId });
                    }
                    case AudioMode.Both:
                    default:
                    {
                        await using var voiceStream = File.OpenRead(audioFilePath);
                        await bot.SendVoiceAsync(msg.Chat,
                            InputFile.FromStream(voiceStream, Path.GetFileName(audioFilePath)),
                            replyParameters: new ReplyParameters { MessageId = msg.MessageId });
                        await using var audioStream = File.OpenRead(audioFilePath);
                        return await bot.SendAudioAsync(msg.Chat,
                            InputFile.FromStream(audioStream, Path.GetFileName(audioFilePath)),
                            replyParameters: new ReplyParameters { MessageId = msg.MessageId });
                    }
                }
            }
            else
            {
                // we need to upload the huge file to remote server and get the download link, but before doing that we need to check the endpoint health and to check our auth token
                var fileSharingEndpointHealthUrl = configuration["Urls:FileSharingEndpointHealth"];
                if (fileSharingEndpointHealthUrl == null)
                {
                    return await bot.SendTextMessageAsync(msg.Chat,
                        "Unable to determine the health status of the file sharing endpoint",
                        replyParameters: new ReplyParameters { MessageId = msg.MessageId });
                }
                
                var uploadEndHealthRequest = new HttpRequestMessage
                {
                    Method = HttpMethod.Get,
                    RequestUri = new Uri(fileSharingEndpointHealthUrl),
                };
                
                using var uploadEndHealthResponse = await httpClient.SendAsync(uploadEndHealthRequest);
                if (uploadEndHealthResponse.IsSuccessStatusCode == false)
                {
                    return await bot.SendTextMessageAsync(msg.Chat, "File sharing endpoint is down",
                        replyParameters: new ReplyParameters { MessageId = msg.MessageId });
                }
                
                if (authData.ShouldRefreshToken())
                {
                    authData = await GetAuthData(httpClient, authServer, clientId, clientSecret);
                }
                
                if (authData == null)
                {
                    return await bot.SendTextMessageAsync(msg.Chat, "Unable to authorize the bot",
                        replyParameters: new ReplyParameters { MessageId = msg.MessageId });
                }

                var fileSharingEndpointUrl = configuration["Urls:FileSharingEndpoint"];
                if (fileSharingEndpointUrl == null)
                {
                    return await bot.SendTextMessageAsync(msg.Chat, "Unable to get the file sharing endpoint url",
                        replyParameters: new ReplyParameters { MessageId = msg.MessageId });
                }
                
                var uploadFileRequest = new HttpRequestMessage
                {
                    Method = HttpMethod.Post,
                    RequestUri = new Uri(fileSharingEndpointUrl),
                    Headers =
                    {
                        { "Authorization", $"Bearer {authData.AccessToken}" },
                    },
                };

                var content = new MultipartFormDataContent();
                var fileBytes = await File.ReadAllBytesAsync(audioFilePath);
                var fileContent = new ByteArrayContent(fileBytes);
                fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data");
                content.Add(fileContent, "file", Path.GetFileName(audioFilePath));
                uploadFileRequest.Content = content;
                
                using var uploadFileResponse = await httpClient.SendAsync(uploadFileRequest);
                if (uploadFileResponse.IsSuccessStatusCode == false)
                {
                    return await bot.SendTextMessageAsync(msg.Chat,
                        $"Unable to upload the audio file to file sharing server. StatusCode: {uploadFileResponse.StatusCode}",
                        replyParameters: new ReplyParameters { MessageId = msg.MessageId });
                }
                
                var uploadResultStr = await uploadFileResponse.Content.ReadAsStringAsync();
                if (string.IsNullOrEmpty(uploadResultStr))
                {
                    return await bot.SendTextMessageAsync(msg.Chat,
                        "Unable to get upload data from file sharing server",
                        replyParameters: new ReplyParameters { MessageId = msg.MessageId });
                }
                
                var uploadData = JsonSerializer.Deserialize<UploadData>(uploadResultStr);
                if (uploadData == null)
                {
                    return await bot.SendTextMessageAsync(msg.Chat,
                        "Unable to get upload data from file sharing server",
                        replyParameters: new ReplyParameters { MessageId = msg.MessageId });
                }

                return await bot.SendTextMessageAsync(msg.Chat, $"<a href=\"{uploadData.FileUrl}\">Audio link</a>",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId },
                    parseMode: ParseMode.Html);
            }
        }
        finally
        {
            if (string.IsNullOrEmpty(audioFilePath) == false && File.Exists(audioFilePath))
            {
                File.Delete(audioFilePath);
                logger.LogInformation("The file '{Path}' was deleted", audioFilePath);
            }
        }
    }

    private async Task<AuthData?> GetAuthData(HttpClient httpClient, string authServer, string clientId,
        string clientSecret)
    {
        try
        {
            var authRequest = new HttpRequestMessage
            {
                Method = HttpMethod.Post,
                RequestUri = new Uri(authServer),
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    { "grant_type", "client_credentials" },
                    { "client_id", clientId },
                    { "client_secret", clientSecret },
                })
            };
            using var authResponse = await httpClient.SendAsync(authRequest);
            if (authResponse.IsSuccessStatusCode == false)
            {
                return null;
            }

            var authStr = await authResponse.Content.ReadAsStringAsync();

            if (string.IsNullOrEmpty(authStr))
            {
                return null;
            }

            return JsonSerializer.Deserialize<AuthData>(authStr);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error while getting the auth data");
            return null;
        }
    }

    private bool IsValidFilename(string testName)
    {
        var strTheseAreInvalidFileNameChars = new string(Path.GetInvalidFileNameChars()); 
        var regInvalidFileName = new Regex("[" + Regex.Escape(strTheseAreInvalidFileNameChars) + "]");
 
        if (regInvalidFileName.IsMatch(testName)) { return false; }

        return true;
    }

    // Process Inline Keyboard callback data
    private async Task OnCallbackQuery(CallbackQuery callbackQuery)
    {
        logger.LogInformation("Received inline keyboard callback from: {CallbackQueryId}", callbackQuery.Id);

        var data = callbackQuery.Data;
        var message = callbackQuery.Message;
        if (message == null)
        {
            return;
        }

        var chat = message.Chat;
        if (data == null)
        {
            return;
        }

        _inMemorySettings[chat.Id] = data switch
        {
            "Voice" => AudioMode.Voice,
            "Audio" => AudioMode.Audio,
            "Both" => AudioMode.Both,
            _ => AudioMode.Both
        };

        await bot.SendTextMessageAsync(message.Chat, $"Selected message mode: {data}");
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

    private async Task OnPollAnswer(PollAnswer pollAnswer)
    {
        var answer = pollAnswer.OptionIds.FirstOrDefault();
        var selectedOption = PollOptions[answer];
        if (pollAnswer.User != null)
            await bot.SendTextMessageAsync(pollAnswer.User.Id, $"You've chosen: {selectedOption.Text} in poll");
    }

    private Task UnknownUpdateHandlerAsync(Update update)
    {
        logger.LogInformation("Unknown update type: {UpdateType}", update.Type);
        return Task.CompletedTask;
    }
}