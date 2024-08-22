using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
            "/photo" => SendPhoto(msg),
            "/inline_buttons" => SendInlineKeyboard(msg),
            "/keyboard" => SendReplyKeyboard(msg),
            "/remove" => RemoveKeyboard(msg),
            "/request" => RequestContactAndLocation(msg),
            "/inline_mode" => StartInlineQuery(msg),
            "/poll" => SendPoll(msg),
            "/poll_anonymous" => SendAnonymousPoll(msg),
            "/throw" => FailingHandler(msg),
            "/usage" => Usage(msg),
            _ => DefaultBehaviour(msg)
        });
        logger.LogInformation("The message was sent with id: {SentMessageId}", sentMessage.MessageId);
    }

    private async Task<Message> DefaultBehaviour(Message msg)
    {
        var audioFilePath = "";

        try
        {
            var msgText = msg.Text;
            if (msgText == null)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Message text is null");
            }

            if ((msgText.StartsWith("https://youtube.com") || msgText.StartsWith("https://www.youtube.com") ||
                 msgText.StartsWith("https://youtu.be")) == false)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Only the YouTube links are allowed");
            }

            var url = msgText.Split(' ')[0];
            if (string.IsNullOrEmpty(url))
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Url is empty");
            }

            var httpClient = clientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromMinutes(5);

            var healthUrl = configuration["Urls:AudioEndpointHealth"];
            if (healthUrl == null)
            {
                return await bot.SendTextMessageAsync(msg.Chat,
                    "Unable to check the fact that the main endpoint is healthy");
            }

            var healthRequest = new HttpRequestMessage
            {
                Method = HttpMethod.Get,
                RequestUri = new Uri(healthUrl),
            };
            using var healthResponse = await httpClient.SendAsync(healthRequest);
            if (healthResponse.IsSuccessStatusCode == false)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Audio microservice is unhealthy");
            }

            var authServer = configuration["Urls:AuthServer"];
            var clientId = configuration["BotConfiguration:ClientId"];
            var clientSecret = configuration["BotConfiguration:ClientSecret"];

            if ((authServer != null && clientId != null && clientSecret != null) == false)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Unable to authorize the bot");
            }

            var authData = await GetAuthData(httpClient, authServer, clientId, clientSecret);
            if (authData == null)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Unable to authorize the bot");
            }
            
            var audioEndpointUrl = configuration["Urls:AudioEndpoint"];
            if (audioEndpointUrl == null)
            {
                return await bot.SendTextMessageAsync(msg.Chat,
                    "Unable to send the request to audio endpoint. Provide url to the endpoint");
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
                return await bot.SendTextMessageAsync(msg.Chat, "Response from audio endpoint was unsuccessful");
            }

            var (_, lastHeaderValue) = audioResponse.Content.Headers.LastOrDefault();
            var attachmentInfo = lastHeaderValue?.LastOrDefault();
            if (string.IsNullOrEmpty(attachmentInfo))
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Unable to get the attachment string");
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
                return await bot.SendTextMessageAsync(msg.Chat, "Filename not found");
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
                await using (var voiceStream = File.OpenRead(audioFilePath))
                {
                    await bot.SendVoiceAsync(msg.Chat,
                        InputFile.FromStream(voiceStream, Path.GetFileName(audioFilePath)),
                        replyParameters: new ReplyParameters { MessageId = msg.MessageId });
                }

                await using (var audioStream = File.OpenRead(audioFilePath))
                {
                    return await bot.SendAudioAsync(msg.Chat,
                        InputFile.FromStream(audioStream, Path.GetFileName(audioFilePath)),
                        replyParameters: new ReplyParameters { MessageId = msg.MessageId });
                }
            }
            else
            {
                // we need to upload the huge file to remote server and get the download link, but before doing that we need to check the endpoint health and to check our auth token
                var fileSharingEndpointHealthUrl = configuration["Urls:FileSharingEndpointHealth"];
                if (fileSharingEndpointHealthUrl == null)
                {
                    return await bot.SendTextMessageAsync(msg.Chat,
                        "Unable to determine the health status of the file sharing endpoint");
                }
                
                var uploadEndHealthRequest = new HttpRequestMessage
                {
                    Method = HttpMethod.Get,
                    RequestUri = new Uri(fileSharingEndpointHealthUrl),
                };
                
                using var uploadEndHealthResponse = await httpClient.SendAsync(uploadEndHealthRequest);
                if (uploadEndHealthResponse.IsSuccessStatusCode == false)
                {
                    return await bot.SendTextMessageAsync(msg.Chat, "File sharing endpoint is down");
                }
                
                if (authData.ShouldRefreshToken())
                {
                    authData = await GetAuthData(httpClient, authServer, clientId, clientSecret);
                }
                
                if (authData == null)
                {
                    return await bot.SendTextMessageAsync(msg.Chat, "Unable to authorize the bot");
                }

                var fileSharingEndpointUrl = configuration["Urls:FileSharingEndpoint"];
                if (fileSharingEndpointUrl == null)
                {
                    return await bot.SendTextMessageAsync(msg.Chat, "Unable to get the file sharing endpoint url");
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
                fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
                content.Add(fileContent, "file", Path.GetFileName(audioFilePath));
                uploadFileRequest.Content = content;
                
                using var uploadFileResponse = await httpClient.SendAsync(uploadFileRequest);
                if (uploadFileResponse.IsSuccessStatusCode == false)
                {
                    return await bot.SendTextMessageAsync(msg.Chat,
                        $"Unable to upload the audio file to file sharing server. StatusCode: {uploadFileResponse.StatusCode}");
                }
                
                var uploadResultStr = await uploadFileResponse.Content.ReadAsStringAsync();
                if (string.IsNullOrEmpty(uploadResultStr))
                {
                    return await bot.SendTextMessageAsync(msg.Chat,
                        "Unable to get upload data from file sharing server");
                }
                
                var uploadData = JsonSerializer.Deserialize<UploadData>(uploadResultStr);
                if (uploadData == null)
                {
                    return await bot.SendTextMessageAsync(msg.Chat,
                        "Unable to get upload data from file sharing server");
                }

                return await bot.SendTextMessageAsync(msg.Chat, uploadData.FileUrl);
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
        var strTheseAreInvalidFileNameChars = new string(System.IO.Path.GetInvalidFileNameChars()); 
        var regInvalidFileName = new Regex("[" + Regex.Escape(strTheseAreInvalidFileNameChars) + "]");
 
        if (regInvalidFileName.IsMatch(testName)) { return false; };
 
        return true;
    }

    private async Task<Message> Usage(Message msg)
    {
        const string usage = """
                <b><u>Bot menu</u></b>:
                /photo          - send a photo
                /inline_buttons - send inline buttons
                /keyboard       - send keyboard buttons
                /remove         - remove keyboard buttons
                /request        - request location or contact
                /inline_mode    - send inline-mode results list
                /poll           - send a poll
                /poll_anonymous - send an anonymous poll
                /throw          - what happens if handler fails
            """;
        return await bot.SendTextMessageAsync(msg.Chat, usage, parseMode: ParseMode.Html, replyMarkup: new ReplyKeyboardRemove());
    }

    private async Task<Message> SendPhoto(Message msg)
    {
        await bot.SendChatActionAsync(msg.Chat, ChatAction.UploadPhoto);
        await Task.Delay(2000); // simulate a long task
        await using var fileStream = new FileStream("Files/bot.gif", FileMode.Open, FileAccess.Read);
        return await bot.SendPhotoAsync(msg.Chat, fileStream, caption: "Read https://telegrambots.github.io/book/");
    }

    // Send inline keyboard. You can process responses in OnCallbackQuery handler
    private async Task<Message> SendInlineKeyboard(Message msg)
    {
        var inlineMarkup = new InlineKeyboardMarkup()
            .AddNewRow("1.1", "1.2", "1.3")
            .AddNewRow()
                .AddButton("WithCallbackData", "CallbackData")
                .AddButton(InlineKeyboardButton.WithUrl("WithUrl", "https://github.com/TelegramBots/Telegram.Bot"));
        return await bot.SendTextMessageAsync(msg.Chat, "Inline buttons:", replyMarkup: inlineMarkup);
    }

    private async Task<Message> SendReplyKeyboard(Message msg)
    {
        var replyMarkup = new ReplyKeyboardMarkup(true)
            .AddNewRow("1.1", "1.2", "1.3")
            .AddNewRow().AddButton("2.1").AddButton("2.2");
        return await bot.SendTextMessageAsync(msg.Chat, "Keyboard buttons:", replyMarkup: replyMarkup);
    }

    private async Task<Message> RemoveKeyboard(Message msg)
    {
        return await bot.SendTextMessageAsync(msg.Chat, "Removing keyboard", replyMarkup: new ReplyKeyboardRemove());
    }

    private async Task<Message> RequestContactAndLocation(Message msg)
    {
        var replyMarkup = new ReplyKeyboardMarkup(true)
            .AddButton(KeyboardButton.WithRequestLocation("Location"))
            .AddButton(KeyboardButton.WithRequestContact("Contact"));
        return await bot.SendTextMessageAsync(msg.Chat, "Who or Where are you?", replyMarkup: replyMarkup);
    }

    private async Task<Message> StartInlineQuery(Message msg)
    {
        var button = InlineKeyboardButton.WithSwitchInlineQueryCurrentChat("Inline Mode");
        return await bot.SendTextMessageAsync(msg.Chat, "Press the button to start Inline Query\n\n" +
            "(Make sure you enabled Inline Mode in @BotFather)", replyMarkup: new InlineKeyboardMarkup(button));
    }

    private async Task<Message> SendPoll(Message msg)
    {
        return await bot.SendPollAsync(msg.Chat, "Question", PollOptions, isAnonymous: false);
    }

    private async Task<Message> SendAnonymousPoll(Message msg)
    {
        return await bot.SendPollAsync(chatId: msg.Chat, "Question", PollOptions);
    }

    private static Task<Message> FailingHandler(Message msg)
    {
        throw new NotImplementedException("FailingHandler");
    }

    // Process Inline Keyboard callback data
    private async Task OnCallbackQuery(CallbackQuery callbackQuery)
    {
        logger.LogInformation("Received inline keyboard callback from: {CallbackQueryId}", callbackQuery.Id);
        await bot.AnswerCallbackQueryAsync(callbackQuery.Id, $"Received {callbackQuery.Data}");
        await bot.SendTextMessageAsync(callbackQuery.Message!.Chat, $"Received {callbackQuery.Data}");
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

public class AuthData
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("refresh_expires_in")]
    public int RefreshExpiresIn { get; set; }

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; }

    [JsonPropertyName("not-before-policy")]
    public int NotBeforePolicy { get; set; }

    [JsonPropertyName("scope")]
    public string Scope { get; set; }
    
    [JsonIgnore]
    public DateTime ExpirationTime { get; private set; }

    public void SetExpirationTime()
    {
        ExpirationTime = DateTime.UtcNow.AddSeconds(ExpiresIn);
    }

    public bool IsTokenValid()
    {
        return DateTime.UtcNow < ExpirationTime;
    }

    public bool ShouldRefreshToken(int refreshThresholdSeconds = 30)
    {
        return DateTime.UtcNow.AddSeconds(refreshThresholdSeconds) >= ExpirationTime;
    }
}

public class UploadData
{
    [JsonPropertyName("fileUrl")]
    public string FileUrl { get; set; }
}