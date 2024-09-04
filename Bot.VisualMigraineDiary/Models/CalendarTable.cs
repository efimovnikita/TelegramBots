using System.Globalization;
using Bot.VisualMigraineDiary.Services;

namespace Bot.VisualMigraineDiary.Models;

public class CalendarTable
{
    private static readonly string[] MonthNames =
    [
        "January",
        "February",
        "March",
        "April",
        "May",
        "June",
        "July",
        "August",
        "September",
        "October",
        "November",
        "December"
    ];

    public CalendarTable(DateTime dateTime, MigraineEventService migraineEventService)
    {
        var calendar = CultureInfo.InvariantCulture.Calendar;
        var month = calendar.GetMonth(time: dateTime);
        var year = calendar.GetYear(time: dateTime);

        MonthName = MonthNames[month - 1];

        var daysInMonth = calendar.GetDaysInMonth(year: year, month: month);

        Weeks.Add(item: new Week());
        var secondWeek = new Week();
        Weeks.Add(item: secondWeek);
        var thirdWeek = new Week();
        Weeks.Add(item: thirdWeek);
        var fourthWeek = new Week();
        Weeks.Add(item: fourthWeek);
        Weeks.Add(item: new Week());

        var firstDayOfMonth = new DateTime(year: year, month: month, day: 1);
        var secondDayOfMonth = new DateTime(year: year, month: month, day: 2);
        var firstDayOfMonthName = firstDayOfMonth.DayOfWeek;
        var secondDayOfMonthName = secondDayOfMonth.DayOfWeek;

        var firstWeek = Weeks.First();
        firstWeek.Days[key: firstDayOfMonthName.ToString()] = new Day(numberInMonth: 1,
            isToday: dateTime.Day.Equals(obj: 1),
            isDayWithinCurrentMonth: true,
            month: month,
            year: year,
            migraineEventService: migraineEventService);
        
        var lastWeek = Weeks.Last();

        #region fill current month days

        var i = 2;
        var firstWeekLastDays = firstWeek.Days.Where(predicate: (_, h) => h >= (int)secondDayOfMonthName).ToArray();
        foreach (var (dayName, day) in firstWeekLastDays)
        {
            if (day != null)
            {
                continue;
            }
            
            firstWeek.Days[key: dayName] = new Day(numberInMonth: i,
                isToday: dateTime.Day.Equals(obj: i),
                isDayWithinCurrentMonth: true,
                month: month,
                year: year,
                migraineEventService: migraineEventService);
            i++;
        }
        
        foreach (var week in new[] { secondWeek, thirdWeek, fourthWeek, lastWeek })
        {
            foreach (var (dayName, day) in week.Days)
            {
                if (day != null)
                {
                    continue;
                }
                
                if (i > daysInMonth)
                {
                    break;
                }

                week.Days[key: dayName] = new Day(numberInMonth: i,
                    isToday: dateTime.Day.Equals(obj: i),
                    isDayWithinCurrentMonth: true,
                    month: month,
                    year: year,
                    migraineEventService: migraineEventService);
                i++;
            }
        }

        #endregion
        
        #region fill last week next month days

        var lastWeekEmptyDays = lastWeek.Days.Where(predicate: pair => pair.Value is null).ToArray();
        i = 1;
        foreach (var (dayName, _) in lastWeekEmptyDays)
        {
            lastWeek.Days[key: dayName] = new Day(numberInMonth: i,
                isToday: dateTime.Day.Equals(obj: i),
                isDayWithinCurrentMonth: false,
                month: month + 1,
                year: year,
                migraineEventService: migraineEventService);
            i++;
        }

        #endregion
        
        #region fill first week prev month last days

        var firstWeekEmptyDays = firstWeek.Days.Where(predicate: pair => pair.Value is null).Reverse().ToArray();
        var daysInPrevMonth = calendar.GetDaysInMonth(year: year, month: month - 1);
        foreach (var (dayName, _) in firstWeekEmptyDays)
        {
            firstWeek.Days[key: dayName] = new Day(numberInMonth: daysInPrevMonth,
                isToday: false,
                isDayWithinCurrentMonth: false,
                month: month - 1,
                year: year,
                migraineEventService: migraineEventService);
            daysInPrevMonth--;
        }

        #endregion
    }
    
    public List<Week> Weeks { get; set; } = new List<Week>(5);
    public string MonthName { get; set; }
}