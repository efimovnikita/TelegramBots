using System.Text.Json.Serialization;

namespace BotSharedLibrary;

public class UploadData
{
    [JsonPropertyName("fileUrl")]
    public string FileUrl { get; set; }
}