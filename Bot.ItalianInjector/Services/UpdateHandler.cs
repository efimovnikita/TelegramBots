using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anthropic.SDK;
using Anthropic.SDK.Constants;
using Anthropic.SDK.Messaging;
using Bot.ItalianInjector.Models;
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
using Message = Telegram.Bot.Types.Message;

namespace Bot.ItalianInjector.Services;

public class UpdateHandler : IUpdateHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly ILogger<UpdateHandler> _logger;
    private readonly IConfiguration _configuration;
    private readonly IFileSharingApi _fileSharingApi;
    private readonly IAuthApi _authApi;
    private readonly IOptions<LlmSettings> _llmSettings;
    private readonly IOptions<AllowedUsers> _allowedUsers;

    public UpdateHandler(ITelegramBotClient bot,
        ILogger<UpdateHandler> logger,
        IConfiguration configuration, 
        IFileSharingApi fileSharingApi,
        IAuthApi authApi,
        IOptions<RedisSettings> redisSettings,
        IOptions<LlmSettings> llmSettings,
        IOptions<AllowedUsers> allowedUsers)
    {
        _bot = bot;
        _logger = logger;
        _configuration = configuration;
        _fileSharingApi = fileSharingApi;
        _authApi = authApi;
        _llmSettings = llmSettings;
        _allowedUsers = allowedUsers;

        var redisConnection = ConnectionMultiplexer.Connect(redisSettings.Value.ConnectionString);
        var subscriber = redisConnection.GetSubscriber();
        subscriber.SubscribeAsync(redisSettings.Value.ChannelName, OnReceiveMsgFromRedis).ConfigureAwait(false);
    }

    public async Task HandleErrorAsync(
        ITelegramBotClient botClient,
        Exception exception,
        HandleErrorSource source,
        CancellationToken cancellationToken
        )
    {
        _logger.LogInformation("HandleError: {Exception}", exception);
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

    private void OnReceiveMsgFromRedis(RedisChannel channel, RedisValue value)
    {
        LanguageInjectionRequest? request = null;
        try
        {
            _logger.LogInformation("Starting OnReceiveMsgFromRedis process");

            var key = _llmSettings.Value.Key;
            if (string.IsNullOrWhiteSpace(key))
            {
                _logger.LogWarning("LLM key is null or empty");

                return;
            }
        
            _logger.LogInformation("Checking file sharing API health");

            _fileSharingApi.CheckHealth().ConfigureAwait(false);
            
            _logger.LogInformation("Deserializing request");
            request = JsonSerializer.Deserialize<LanguageInjectionRequest>(value!);
        
            if (request == null)
            {
                _logger.LogWarning("Deserialized request is null");
                return;
            }

            var allowedIds = _allowedUsers.Value.Ids.Split(',',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (allowedIds.Contains(request.UserId.ToString()) == false)
            {
                _logger.LogError("User {UserId} is not allowed", request.UserId);
                return;
            }

            var initialText = request.Text;
        
            if (string.IsNullOrWhiteSpace(initialText))
            {
                return;
            }

            var sentences = Regex.Split(initialText, @"(?<=[.!?])\s+")
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();

            var client = new AnthropicClient(key);

            var builder = new StringBuilder();
            foreach (var engSentence in sentences)
            {
                builder.AppendLine($"<p lang=\"en\"><span class=\"english-text\">{engSentence}</span></p>");

                try
                {
                    var messages = new List<Anthropic.SDK.Messaging.Message>
                    {   
                        new(RoleType.User, 
                            $"""
                             You are tasked with translating an English sentence into Italian. Your goal is to create a simple, basic Italian translation that a beginner-level student can understand.

                             Here is the English sentence to translate:
                             <english_sentence>
                             {engSentence}
                             </english_sentence>

                             Follow these guidelines when translating:
                             1. Use basic vocabulary and simple sentence structures.
                             2. Avoid idiomatic expressions or complex grammar.
                             3. If there are multiple ways to express the same idea, choose the simplest one.
                             4. Ensure that the core meaning of the original sentence is preserved.

                             Provide only the Italian translation of the sentence. Do not include any explanations, notes, or the original English sentence in your response.
                             """) 
                    };
    
                    var parameters = new MessageParameters
                    {
                        Messages = messages,
                        Model = AnthropicModels.Claude3Haiku,
                        Stream = false,
                        MaxTokens = 2500
                    };

                    var response = client.Messages.GetClaudeMessageAsync(parameters).ConfigureAwait(false).GetAwaiter().GetResult();
                    var italianSentence = response.Message.ToString();
    
                    builder.AppendLine($"<p lang=\"it\"><span class=\"italian-text\">{italianSentence}</span></p>");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred while translating sentence: {Sentence}", engSentence);
                }
            }
        
            // create a new htm file
            var htmFileContent = $$"""
                                   <!DOCTYPE html>
                                   <html>
                                   <head>
                                       <meta charset="UTF-8">
                                       <meta name="viewport" content="width=device-width, initial-scale=1.0">
                                       <title>Translation (English and Italian)</title>
                                       <style type="text/css">
                                            .english-text {
                                                   font-style: italic;
                                                   color: #0000FF;
                                               }
                                           .italian-text {
                                               font-weight: bold;
                                               color: #008000;
                                           }
                                       </style>
                                   </head>
                                   <body>
                                       <main>
                                          {{builder}}
                                       </main>
                                   </body>
                                   </html>
                                   """;

            _logger.LogInformation("Creating HTML file");
            var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.htm");
            File.WriteAllText(path, htmFileContent);
        
            if (!File.Exists(path))
            {
                _logger.LogError("Failed to create HTML file at {Path}", path);

                _bot.SendTextMessageAsync(
                    chatId: request.UserId,
                    text: "Unable to print the output").ConfigureAwait(false);
                return;
            }

            var fileInfo = new FileInfo(path);
            if (fileInfo.Length <= 0)
            {
                _bot.SendTextMessageAsync(
                    chatId: request.UserId,
                    text: "Unable to print the output").ConfigureAwait(false);
                return;
            }

            var fileStream = File.OpenRead(path);
            var streamPart = new StreamPart(fileStream, Path.GetFileName(path), "multipart/form-data");

            try
            {
                var data = new Dictionary<string, string> {
                    { IAuthApi.GrantType, IAuthApi.ClientCredentials },
                    { IAuthApi.ClientId, _configuration["BotConfiguration:ClientId"] ?? "" },
                    { IAuthApi.ClientSecret, _configuration["BotConfiguration:ClientSecret"] ?? "" }
                };

                var authData = _authApi.GetAuthData(data).ConfigureAwait(false).GetAwaiter().GetResult();
                UploadData uploadData = _fileSharingApi.UploadFile($"Bearer {authData.AccessToken}", streamPart).ConfigureAwait(false).GetAwaiter().GetResult();

                _logger.LogInformation("File uploaded successfully. Sending URL to user");

                _bot.SendTextMessageAsync(
                    chatId: request.UserId,
                    text: uploadData.FileUrl).ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during file upload or authentication");

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
                _bot.SendDocumentAsync(
                    chatId: request.UserId,
                    document: new InputFileStream(fs, Path.GetFileName(path)),
                    caption: "Here's the translation file:").ConfigureAwait(false).GetAwaiter().GetResult();
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "An error occurred in OnReceiveMsgFromRedis");
            if (request != null)
                _bot.SendTextMessageAsync(
                    chatId: request.UserId,
                    text: e.Message).ConfigureAwait(false);
        }
    }

    private async Task OnMessage(Message msg)
    {
        var messageType = msg.Type;
        _logger.LogInformation("Receive message type: {MessageType}", messageType);
        if (messageType == MessageType.Text)
        {
            if (msg.Text is not { } messageText)
                return;

            var sentMessage = await (messageText.Split(' ')[0] switch
            {
                _ => Usage()
            });
            
            _logger.LogInformation("The message was sent with id: {SentMessageId}", sentMessage.MessageId);
        }
    }

    private Task<Message> Usage()
    {
        throw new NotImplementedException();
    }

    #region Inline Mode

    private async Task OnInlineQuery(InlineQuery inlineQuery)
    {
        _logger.LogInformation("Received inline query from: {InlineQueryFromId}", inlineQuery.From.Id);

        InlineQueryResult[] results = [ // displayed result
            new InlineQueryResultArticle("1", "Telegram.Bot", new InputTextMessageContent("hello")),
            new InlineQueryResultArticle("2", "is the best", new InputTextMessageContent("world")),
        ];
        await _bot.AnswerInlineQueryAsync(inlineQuery.Id, results, cacheTime: 0, isPersonal: true);
    }

    private async Task OnChosenInlineResult(ChosenInlineResult chosenInlineResult)
    {
        _logger.LogInformation("Received inline result: {ChosenInlineResultId}", chosenInlineResult.ResultId);
        await _bot.SendTextMessageAsync(chosenInlineResult.From.Id, $"You chose result with Id: {chosenInlineResult.ResultId}");
    }

    #endregion

    private Task OnPoll(Poll poll)
    {
        _logger.LogInformation("Received Poll info: {Question}", poll.Question);
        return Task.CompletedTask;
    }

    private Task UnknownUpdateHandlerAsync(Update update)
    {
        _logger.LogInformation("Unknown update type: {UpdateType}", update.Type);
        return Task.CompletedTask;
    }
}