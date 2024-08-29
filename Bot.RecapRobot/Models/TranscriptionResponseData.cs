using System.Text.Json.Serialization;

namespace Bot.RecapRobot.Models;

public class TranscriptionResponseData
{
    [JsonPropertyName("jobId")]
    public string JobId { get; set; }
}