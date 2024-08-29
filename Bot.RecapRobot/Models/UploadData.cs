using System.Text.Json.Serialization;

namespace Bot.RecapRobot.Models;

public class UploadData
{
    [JsonPropertyName("fileUrl")]
    public string FileUrl { get; set; }
}