using System.Text.Json.Serialization;

namespace Bot.ListenYoutube.Models;

public class UploadData
{
    [JsonPropertyName("fileUrl")]
    public string FileUrl { get; set; }
}