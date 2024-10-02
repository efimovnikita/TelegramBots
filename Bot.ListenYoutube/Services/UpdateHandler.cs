using Bot.ListenYoutube.Models;
using BotSharedLibrary;
using Refit;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;
using Telegram.Bot.Types.ReplyMarkups;
using File = System.IO.File;

namespace Bot.ListenYoutube.Services;

public class UpdateHandler(
    ITelegramBotClient bot,
    ILogger<UpdateHandler> logger,
    IConfiguration configuration,
    IAuthApi authApi,
    IYouTubeApi youTubeApi,
    IFileSharingApi fileSharingApi) : IUpdateHandler
{
    private const string BotconfigurationClientid = "BotConfiguration:ClientId";
    private const string BotconfigurationClientsecret = "BotConfiguration:ClientSecret";
    private static readonly InputPollOption[] PollOptions = ["Hello", "World!"];
    private readonly Dictionary<long, AudioMode> _inMemorySettings = [];
    private TimeSpan _startTime;
    private TimeSpan _endTime;
    private bool _isSplitPresent;

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
            .AddNewRow("Both", "Voice", "Audio", "Link");
        return await bot.SendTextMessageAsync(msg.Chat, "Select the message mode:", replyMarkup: inlineMarkup);
    }

    private async Task<Message> DefaultBehaviour(Message msg)
    {
        var waitingMessage = await bot.SendTextMessageAsync(msg.Chat, "\u23f3");
        var audioFilePath = "";

        try
        {
            // first of all wee need to get settings from memory storage
            var getSettingsResult = _inMemorySettings.TryGetValue(msg.Chat.Id, out var mode);
            if (getSettingsResult == false)
            {
                mode = AudioMode.Link;
                _inMemorySettings.Add(msg.Chat.Id, AudioMode.Link);
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

            var args = msgText.Split(' ');
            var url = args[0];
            if (string.IsNullOrEmpty(url))
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Url is empty",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            if (args.Length == 3)
            {
                var startTimeStr = args[1];
                var endTimeStr = args[2];

                const string format = @"hh\:mm\:ss";
                if (TimeSpan.TryParseExact(startTimeStr, format, null, out var startTime) &&
                    TimeSpan.TryParseExact(endTimeStr, format, null, out var endTime))
                {
                    _startTime = startTime;
                    _endTime = endTime;
                    
                    _isSplitPresent = true;
                }
                else
                {
                    _isSplitPresent = false;
                }
            }
            else
            {
                _isSplitPresent = false;
            }
            
            await youTubeApi.CheckHealth();

            var data = new Dictionary<string, string> {
                { IAuthApi.GrantType, IAuthApi.ClientCredentials },
                { IAuthApi.ClientId, configuration[BotconfigurationClientid] ?? "" },
                { IAuthApi.ClientSecret, configuration[BotconfigurationClientsecret] ?? "" }
            };

            var authData = await authApi.GetAuthData(data);

            Stream audioResponse;
            var authorization = $"Bearer {authData.AccessToken}";
            if (_isSplitPresent == false)
            {
                audioResponse = await youTubeApi.GetAudio(authorization, url);
            }
            else
            {
                audioResponse = await youTubeApi.GetSplitAudio(authorization, url, _startTime.ToString(), _endTime.ToString());
            }
            
            var filename = $"{Guid.NewGuid()}.mp3";

            audioFilePath = Path.Combine(Path.GetTempPath(), filename);
            await using (FileStream fileStream = new(audioFilePath, FileMode.Create, FileAccess.Write))
            {
                await audioResponse.CopyToAsync(fileStream);
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
                    case AudioMode.Link:
                    {
                        return await UploadFileToFileSharingServer(msg, audioFilePath);
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
                return await UploadFileToFileSharingServer(msg, audioFilePath);
            }
        }
        catch (Exception ex)
        {
            return await bot.SendTextMessageAsync(msg.Chat, ex.GetType() + "\n" + ex.Message,
                replyParameters: new ReplyParameters { MessageId = msg.MessageId });
        }
        finally
        {
            await bot.DeleteMessageAsync(msg.Chat, waitingMessage.MessageId);

            if (string.IsNullOrEmpty(audioFilePath) == false && File.Exists(audioFilePath))
            {
                File.Delete(audioFilePath);
                logger.LogInformation("The file '{Path}' was deleted", audioFilePath);
            }

            // Reset the split-related variables
            _startTime = default;
            _endTime = default;
            _isSplitPresent = false;
        }
    }

    private async Task<Message> UploadFileToFileSharingServer(Message msg, string audioFilePath)
    {
        var data = new Dictionary<string, string> {
            { IAuthApi.GrantType, IAuthApi.ClientCredentials },
            { IAuthApi.ClientId, configuration[BotconfigurationClientid] ?? "" },
            { IAuthApi.ClientSecret, configuration[BotconfigurationClientsecret] ?? "" }
        };

        var authData = await authApi.GetAuthData(data);
        
        var fileStream = File.OpenRead(audioFilePath);
        var streamPart = new StreamPart(fileStream, Path.GetFileName(audioFilePath), "multipart/form-data");
                    
        var uploadData = await fileSharingApi.UploadFile($"Bearer {authData.AccessToken}", streamPart);

        return await bot.SendTextMessageAsync(msg.Chat, $"<a href=\"{uploadData.FileUrl}\">Audio link</a>",
            replyParameters: new ReplyParameters { MessageId = msg.MessageId },
            parseMode: ParseMode.Html);
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
            "Link" => AudioMode.Link,
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