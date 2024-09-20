using System.Text.Json.Serialization;

namespace BotSharedLibrary;

public class JobInfo
{
    [JsonPropertyName("jobId")]
    public string JobId { get; set; }
}