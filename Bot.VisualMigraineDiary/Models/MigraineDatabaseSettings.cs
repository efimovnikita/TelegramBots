namespace Bot.VisualMigraineDiary.Models;

public class MigraineDatabaseSettings
{
    public string ConnectionString { get; set; } = null!;
    public string DatabaseName { get; set; } = null!;
    public string MigraineEventsCollectionName { get; set; } = null!;
}