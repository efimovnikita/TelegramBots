using System.Text.Json.Serialization;

namespace Bot.EngTubeBot.Models;

public class UploadData
{
    [JsonPropertyName("fileUrl")]
    public string FileUrl { get; set; }
}