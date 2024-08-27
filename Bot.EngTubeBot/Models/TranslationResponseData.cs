using System.Text.Json.Serialization;

namespace Bot.EngTubeBot.Models;

public class TranslationResponseData
{
    [JsonPropertyName("jobId")]
    public string JobId { get; set; }
}