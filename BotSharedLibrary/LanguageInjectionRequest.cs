namespace BotSharedLibrary;

public class LanguageInjectionRequest(long userId, string text)
{
    public long UserId { get; set; } = userId;
    public string Text { get; set; } = text;
}