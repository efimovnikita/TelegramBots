using System.Text.Json.Serialization;

namespace BotSharedLibrary;

public class JobStatus
{
    [JsonPropertyName("status")]
    public string Status { get; set; }

    [JsonPropertyName("result")]
    public string Result { get; set; }

    [JsonPropertyName("error")]
    public string Error { get; set; }
}