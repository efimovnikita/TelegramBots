using System.Net.Http.Headers;
using System.Text.Json;
using Anthropic.SDK;
using Anthropic.SDK.Constants;
using Anthropic.SDK.Messaging;
using Bot.RecapRobot.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;
using Exception = System.Exception;
using File = System.IO.File;
using Message = Telegram.Bot.Types.Message;

namespace Bot.RecapRobot.Services;

public class UpdateHandler(ITelegramBotClient bot, ILogger<UpdateHandler> logger, IHttpClientFactory clientFactory, IConfiguration configuration) : IUpdateHandler
{
    private const string KeyAnthropicCommand = "/key-anthropic";
    private const string PromptCommand = "/prompt";
    private const string KeyOpenaiCommand = "/key-openai";
    private readonly Dictionary<long, UserSettings> _inMemorySettings = [];
    private static readonly TimeSpan WaitInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxWaitTime = TimeSpan.FromMinutes(10);
    
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
                KeyOpenaiCommand => SetOpenAiKey(msg),
                KeyAnthropicCommand => SetAnthropicKey(msg),
                PromptCommand => SetPrompt(msg),
                _ => DefaultBehaviour(msg)
            });
            
            logger.LogInformation("The message was sent with id: {SentMessageId}", sentMessage.MessageId);
        }
        
        if (messageType == MessageType.Document)
        {
            var msgDocument = msg.Document;
            if (msgDocument == null)
                return;
        
            var sentMessage = await DefaultDocumentBehaviour(msg);
            
            logger.LogInformation("The message was sent with id: {SentMessageId}", sentMessage.MessageId);
        }
    }

    private async Task<Message> SetAnthropicKey(Message msg)
    {
        var getSettingsResult = _inMemorySettings.TryGetValue(msg.Chat.Id, out var settings);
        if (getSettingsResult == false)
        {
            settings = GetDefaultUserSettings();
            _inMemorySettings.Add(msg.Chat.Id, settings);
        }
        
        var msgText = msg.Text;
        if (msgText == null)
        {
            return await bot.SendTextMessageAsync(msg.Chat, "Unable to set the key",
                replyParameters: new ReplyParameters { MessageId = msg.MessageId });
        }

        var key = msgText.Replace(KeyAnthropicCommand, "").Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            return await bot.SendTextMessageAsync(msg.Chat, "Unable to set the key",
                replyParameters: new ReplyParameters { MessageId = msg.MessageId });
        }

        _inMemorySettings[msg.Chat.Id].AnthropicApiKey = key;
        
        return await bot.SendTextMessageAsync(msg.Chat, "The key was set",
            replyParameters: new ReplyParameters { MessageId = msg.MessageId });
    }

    private async Task<Message> DefaultDocumentBehaviour(Message msg)
    {
        var waitingMessage = await bot.SendTextMessageAsync(msg.Chat, "\u23f3");

        try
        {
            // first of all wee need to get settings from memory storage
            var getSettingsResult = _inMemorySettings.TryGetValue(msg.Chat.Id, out var settings);
            if (getSettingsResult == false)
            {
                var userSettings = GetDefaultUserSettings();
                _inMemorySettings.Add(msg.Chat.Id, userSettings);

                return await bot.SendTextMessageAsync(msg.Chat, "You need to setup your OpenAI API key and Anthropic API key",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            var msgDocument = msg.Document;

            if (msgDocument == null)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "The document message is empty",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            Telegram.Bot.Types.File file = await bot.GetFileAsync(msgDocument.FileId);

            if (file.FileSize == null)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Unable to determine the file size",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            var sizeInMegabytes = (double)file.FileSize / (1024 * 1024);
            if (sizeInMegabytes > 19)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "The file is too big",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            if (file.FilePath == null)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Unable to determine the file path",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            var extension = Path.GetExtension(file.FilePath);
            if (extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) == false)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "We are working only with TXT files",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            var filePath = GetFilePath();
            await using (var fileStream = File.OpenWrite(filePath))
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

            var text = await File.ReadAllTextAsync(filePath);

            // now we should make the request to the claude endpoint
            var httpClient = clientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromMinutes(5);

            var summary = await GetSummaryFromClaude(
                httpClient: httpClient,
                text: text,
                settings: settings);
            
            if (summary.Length <= 4000)
            {
                return await bot.SendTextMessageAsync(msg.Chat, summary,
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
                
            return await UploadContentHtmlAndGetLink(httpClient, msg, authData, authServer, clientId, clientSecret, summary);
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
            settings = GetDefaultUserSettings();
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

    private async Task<Message> SetOpenAiKey(Message msg)
    {
        var getSettingsResult = _inMemorySettings.TryGetValue(msg.Chat.Id, out var settings);
        if (getSettingsResult == false)
        {
            settings = GetDefaultUserSettings();
            _inMemorySettings.Add(msg.Chat.Id, settings);
        }
        
        var msgText = msg.Text;
        if (msgText == null)
        {
            return await bot.SendTextMessageAsync(msg.Chat, "Unable to set the key",
                replyParameters: new ReplyParameters { MessageId = msg.MessageId });
        }

        var key = msgText.Replace(KeyOpenaiCommand, "").Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            return await bot.SendTextMessageAsync(msg.Chat, "Unable to set the key",
                replyParameters: new ReplyParameters { MessageId = msg.MessageId });
        }

        _inMemorySettings[msg.Chat.Id].OpenAiApiKey = key;
        
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

    private async Task<Message> CheckStatusAsync(HttpClient httpClient, string audioStatusEndpointUrl,
        string jobId, Message msg, AuthData? authData, string authServer, string clientId, string clientSecret,
        UserSettings? settings)
    {
        var startTime = DateTime.UtcNow;

        while (DateTime.UtcNow - startTime < MaxWaitTime)
        {
            var transcriptionStatusRequest = new HttpRequestMessage
            {
                Method = HttpMethod.Get,
                RequestUri = new Uri($"{audioStatusEndpointUrl}?id={jobId}")
            };

            using var transcriptionStatusResponse = await httpClient.SendAsync(transcriptionStatusRequest);

            if (!transcriptionStatusResponse.IsSuccessStatusCode)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Response from status audio endpoint was unsuccessful",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            var transcriptionStatusResponseStr = await transcriptionStatusResponse.Content.ReadAsStringAsync();
            if (string.IsNullOrEmpty(transcriptionStatusResponseStr))
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Response from status audio endpoint was unsuccessful",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            var transcriptionStatusResponseData =
                JsonSerializer.Deserialize<TranscriptionStatusResponseData>(transcriptionStatusResponseStr);
            if (transcriptionStatusResponseData == null)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Response from status audio endpoint was unsuccessful",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }
            
            if (transcriptionStatusResponseData.Status == "Failed")
            {
                return await bot.SendTextMessageAsync(msg.Chat, $"Something went wrong.\n\nThe error message:\n{transcriptionStatusResponseData.Error}",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            if (transcriptionStatusResponseData.Status == "Succeeded")
            {
                if (string.IsNullOrWhiteSpace(transcriptionStatusResponseData.Result))
                {
                    return await bot.SendTextMessageAsync(msg.Chat, "Result from the status audio endpoint is empty",
                        replyParameters: new ReplyParameters { MessageId = msg.MessageId });
                }
                
                // sent the raw data
                if (transcriptionStatusResponseData.Result.Length > 4000)
                {
                    await UploadContentHtmlAndGetLink(httpClient, msg, authData, authServer, clientId, clientSecret, transcriptionStatusResponseData.Result);
                }
                else
                {
                    await bot.SendTextMessageAsync(msg.Chat, $"Raw data:\n\n{transcriptionStatusResponseData.Result}",
                        replyParameters: new ReplyParameters { MessageId = msg.MessageId });
                }
                
                // now we should make the request to the claude endpoint
                var claudeSummaryResponse = await GetSummaryFromClaude(httpClient, transcriptionStatusResponseData.Result, settings); 

                if (claudeSummaryResponse.Length > 4000)
                {
                    return await UploadContentHtmlAndGetLink(httpClient, msg, authData, authServer, clientId, clientSecret, claudeSummaryResponse);
                }

                return await bot.SendTextMessageAsync(msg.Chat, claudeSummaryResponse,
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            // Wait for 10 seconds before the next request
            await Task.Delay(WaitInterval);
        }

        return await bot.SendTextMessageAsync(msg.Chat, $"Translation job did not succeed within the allowed time. The job id: {jobId}",
            replyParameters: new ReplyParameters { MessageId = msg.MessageId });
    }

    private async Task<Message> UploadContentHtmlAndGetLink(HttpClient httpClient, Message msg, AuthData? authData,
        string authServer, string clientId, string clientSecret, string mainText)
    {
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
                    
        if (authData == null)
        {
            return await bot.SendTextMessageAsync(msg.Chat, "Unable to authorize the bot",
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
            }
        };

        // create a new htm file
        var htmFileContent = $"""
                              <!DOCTYPE html>
                              <html lang="en">
                              <head>
                                  <meta charset="UTF-8">
                                  <meta name="viewport" content="width=device-width, initial-scale=1.0">
                                  <title>Content</title>
                              </head>
                              <body>
                                  <main>
                                     <p>
                                         {mainText}
                                     </p>
                                  </main>
                              </body>
                              </html>
                              """;

        var htmFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.htm");
        await File.WriteAllTextAsync(htmFilePath, htmFileContent);
                    
        var content = new MultipartFormDataContent();
        var fileBytes = await File.ReadAllBytesAsync(htmFilePath);
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data");
        content.Add(fileContent, "file", Path.GetFileName(htmFilePath));
        uploadFileRequest.Content = content;

        using var uploadFileResponse = await httpClient.SendAsync(uploadFileRequest);
        if (uploadFileResponse.IsSuccessStatusCode == false)
        {
            return await bot.SendTextMessageAsync(msg.Chat,
                $"Unable to upload the file to file sharing server. StatusCode: {uploadFileResponse.StatusCode}",
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

        return await bot.SendTextMessageAsync(msg.Chat, $"<a href=\"{uploadData.FileUrl}\">Data</a>",
            replyParameters: new ReplyParameters { MessageId = msg.MessageId },
            parseMode: ParseMode.Html);
    }

    private async Task<string> GetSummaryFromClaude(HttpClient httpClient, string text, UserSettings? settings)
    {
        var client = new AnthropicClient(settings?.AnthropicApiKey ?? "", httpClient);
        var messages = new List<Anthropic.SDK.Messaging.Message>
            {   
                new(RoleType.User, 
                $"""
                    {settings?.Prompt ?? ""}
                    
                    <transcript>     
                    {text}
                    </transcript>
                    """) 
            };
            
        var parameters = new MessageParameters
        {
            Messages = messages,
            MaxTokens = 2500,
            Model = AnthropicModels.Claude35Sonnet,
            Stream = false
        };

        var response = await client.Messages.GetClaudeMessageAsync(parameters);
        return response.Message.ToString();
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
                var userSettings = GetDefaultUserSettings();
                _inMemorySettings.Add(msg.Chat.Id, userSettings);

                return await bot.SendTextMessageAsync(msg.Chat, "You need to setup your OpenAI API key and Anthropic API key",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            var msgText = msg.Text;
            if (msgText == null)
            {
                return await bot.SendTextMessageAsync(msg.Chat, "Message text is null",
                    replyParameters: new ReplyParameters { MessageId = msg.MessageId });
            }

            var allowedServerUrl = configuration["Urls:AllowedFileSharingServer"];
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

            return await SendToCloudAndGetTranscription(msg, httpClient, filePath, settings);
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

    private static UserSettings GetDefaultUserSettings()
    {
        var userSettings = new UserSettings("", "", 
            """
                    You are tasked with summarizing a meeting transcript in Russian. Follow these steps carefully:
                    
                    1. First, you will be provided with a meeting transcript. Read it carefully and analyze its content.
                    
                    2. After reading the transcript, identify the main topics discussed, key decisions made, and any important action items or deadlines mentioned.
                    
                    3. Summarize these key points in Russian. Your summary should be concise but comprehensive, capturing the essence of the meeting.
                    
                    4. Structure your summary as follows:
                       - Start with a brief introduction stating the general purpose or topic of the meeting.
                       - List the main points discussed, using bullet points for clarity.
                       - Mention any decisions made or conclusions reached.
                       - Include any action items or next steps agreed upon, if applicable.
                    
                    5. Write your summary in Russian, using proper grammar and vocabulary. Ensure that the language is formal and appropriate for a business context.
                    
                    Remember, the entire summary should be in Russian, including any introductory phrases or structural elements.
                    """
            );
        return userSettings;
    }

    private async Task<Message> SendToCloudAndGetTranscription(Message msg, HttpClient httpClient, string filePath, UserSettings? settings)
    {
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

        var transcriptionRequest = new HttpRequestMessage
        {
            Method = HttpMethod.Post,
            RequestUri = new Uri(audioEndpointUrl),
            Headers =
            {
                { "Authorization", $"Bearer {authData.AccessToken}" }
            }
        };

        var content = new MultipartFormDataContent();
        var fileBytes = await File.ReadAllBytesAsync(filePath);
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data");
        content.Add(fileContent, "audioFile", Path.GetFileName(filePath));
        content.Add(new StringContent(""), "prompt");
        content.Add(new StringContent(settings?.OpenAiApiKey ?? ""), "openaiApiKey");
        transcriptionRequest.Content = content;

        using var transcriptionResponse = await httpClient.SendAsync(transcriptionRequest);
        if (transcriptionResponse.IsSuccessStatusCode == false)
        {
            return await bot.SendTextMessageAsync(msg.Chat, "Response from audio endpoint was unsuccessful",
                replyParameters: new ReplyParameters { MessageId = msg.MessageId });
        }

        var transcriptionResponseStr = await transcriptionResponse.Content.ReadAsStringAsync();
        if (string.IsNullOrEmpty(transcriptionResponseStr))
        {
            return await bot.SendTextMessageAsync(msg.Chat, "Response from audio endpoint was unsuccessful",
                replyParameters: new ReplyParameters { MessageId = msg.MessageId });
        }

        var transcriptionResponseData = JsonSerializer.Deserialize<TranscriptionResponseData>(transcriptionResponseStr);
        if (transcriptionResponseData == null)
        {
            return await bot.SendTextMessageAsync(msg.Chat, "Response from audio endpoint was unsuccessful",
                replyParameters: new ReplyParameters { MessageId = msg.MessageId });
        }

        var jobId = transcriptionResponseData.JobId;
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return await bot.SendTextMessageAsync(msg.Chat, "Response from audio endpoint was unsuccessful",
                replyParameters: new ReplyParameters { MessageId = msg.MessageId });
        }

        var audioStatusEndpointUrl = configuration["Urls:AudioStatusEndpoint"];
        if (audioStatusEndpointUrl == null)
        {
            return await bot.SendTextMessageAsync(msg.Chat,
                "Unable to send the status request to audio endpoint. Provide url to the endpoint",
                replyParameters: new ReplyParameters { MessageId = msg.MessageId });
        }

        return await CheckStatusAsync(httpClient, audioStatusEndpointUrl, jobId, msg, authData,
            authServer, clientId, clientSecret, settings);
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