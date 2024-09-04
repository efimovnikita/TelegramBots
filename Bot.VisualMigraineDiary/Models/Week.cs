namespace Bot.VisualMigraineDiary.Models;

public class Week
{
    public Dictionary<string, Day?> Days { get; set; } = 
        new(7)
        {
            { "Sunday", null },
            { "Monday", null },
            { "Tuesday", null },
            { "Wednesday", null },
            { "Thursday", null },
            { "Friday", null },
            { "Saturday", null },
        };
}