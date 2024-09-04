using Bot.VisualMigraineDiary.Services;

namespace Bot.VisualMigraineDiary.Models;

public class Day
{
    private readonly MigraineEventService _migraineEventService;

    public Day(int numberInMonth,
        bool isToday,
        bool isDayWithinCurrentMonth,
        int month,
        int year,
        MigraineEventService migraineEventService)
    {
        _migraineEventService = migraineEventService;
        IsToday = isToday;
        IsDayWithinCurrentMonth = isDayWithinCurrentMonth;
        NumberInMonth = numberInMonth;
        Month = month;
        Year = year;

        MigraineEvents = GetMigraineEventsAsync().GetAwaiter().GetResult();
    }

    public bool IsToday { get; set; }
    public bool IsDayWithinCurrentMonth { get; set; }
    public int NumberInMonth { get; }
    private int Month { get; }
    private int Year { get; }

    public List<MigraineEvent> MigraineEvents { get; set; }

    private async Task<List<MigraineEvent>> GetMigraineEventsAsync()
    {
        var date = new DateTime(Year, Month, NumberInMonth);
        var nextDay = date.AddDays(1);
        return await _migraineEventService.GetEventsForDateRangeAsync(date, nextDay);
    }
}