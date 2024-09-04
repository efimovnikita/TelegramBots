using System.Text.Json.Serialization;

namespace Bot.VisualMigraineDiary.Models;

public class UploadData
{
    [JsonPropertyName("fileUrl")]
    public string FileUrl { get; set; }
}