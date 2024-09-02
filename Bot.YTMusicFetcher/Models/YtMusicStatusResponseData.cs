using System.Text.Json.Serialization;

namespace Bot.YTMusicFetcher.Models;

public class YtMusicStatusResponseData
{
    [JsonPropertyName("status")]
    public string Status { get; set; }

    [JsonPropertyName("result")]
    public string Result { get; set; }
    
    [JsonPropertyName("error")]
    public string Error { get; set; }
}