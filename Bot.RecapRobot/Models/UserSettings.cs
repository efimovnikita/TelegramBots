namespace Bot.RecapRobot.Models;

public class UserSettings(string openAiApiKey, string anthropicApiKey, string prompt)
{
    public string OpenAiApiKey { get; set; } = openAiApiKey;
    public string AnthropicApiKey { get; set; } = anthropicApiKey;
    public string Prompt { get; set; } = prompt;
}