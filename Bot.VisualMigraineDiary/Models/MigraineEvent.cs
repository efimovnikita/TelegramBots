using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System.Text;

namespace Bot.VisualMigraineDiary.Models;

public class MigraineEvent
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; }

    [BsonElement("startTime")]
    public DateTime StartTime { get; set; } = DateTime.Now;

    [BsonElement("scotoma")]
    [BsonRepresentation(BsonType.String)]
    public ScotomaSeverity ScotomaSeverity { get; set; }

    [BsonElement("triggers")]
    [BsonRepresentation(BsonType.String)]
    public List<TriggerType> Triggers { get; set; }

    [BsonElement("notes")]
    public string Notes { get; set; }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Date: {StartTime}");
        sb.AppendLine($"Scotoma severity: {ScotomaSeverity}");
        if (Triggers.Any())
        {
            sb.AppendLine($"Triggers: {string.Join(", ", Triggers)}");
        }
        if (!string.IsNullOrEmpty(Notes))
        {
            sb.AppendLine($"Notes: {Notes}");
        }
        return sb.ToString().TrimEnd();
    }
}