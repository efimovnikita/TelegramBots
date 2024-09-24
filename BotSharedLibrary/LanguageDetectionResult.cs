using System.Text.Json.Serialization;

namespace BotSharedLibrary;

public class LanguageDetectionResult
{
    [JsonPropertyName("language")]
    public string Language { get; set; }
}