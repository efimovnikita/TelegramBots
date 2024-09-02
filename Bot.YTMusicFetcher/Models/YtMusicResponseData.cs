using System.Text.Json.Serialization;

namespace Bot.YTMusicFetcher.Models;

public class YtMusicResponseData
{
    [JsonPropertyName("jobId")]
    public string JobId { get; set; }
}