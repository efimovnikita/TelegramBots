using System.Text.Json.Serialization;

namespace Bot.RecapRobot.Models;

public class TranscriptionStatusResponseData
{
    [JsonPropertyName("status")]
    public string Status { get; set; }

    [JsonPropertyName("result")]
    public string Result { get; set; }
    
    [JsonPropertyName("error")]
    public string Error { get; set; }
}