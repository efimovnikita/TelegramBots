namespace Bot.EngTubeBot.Models;

public class UserSettings(string key, string prompt)
{
    public string Key { get; set; } = key;
    public string Prompt { get; set; } = prompt;
}