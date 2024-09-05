using System.Globalization;
using System.Text;
using Bot.VisualMigraineDiary.Services;

namespace Bot.VisualMigraineDiary.Models;

public class CalendarTable
{
  private readonly DateTime _dateTime;

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
        _dateTime = dateTime;
      
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
            isToday: dateTime.Day.Equals(obj: 1) && dateTime.Month.Equals(DateTime.Now.Month),
            isDayWithinCurrentMonth: true,
            month: month,
            year: year,
            migraineEventService: migraineEventService);
        
        var lastWeek = Weeks.Last();

        #region fill current month days

        var i = 2;
        KeyValuePair<string, Day?>[] firstWeekLastDays;
        if (secondDayOfMonthName.Equals(DayOfWeek.Sunday) == false)
        {
            firstWeekLastDays = firstWeek.Days.Where(predicate: (_, h) => h >= (int)secondDayOfMonthName).ToArray();
        }
        else
        {
            firstWeekLastDays = Array.Empty<KeyValuePair<string, Day?>>();
        }
        
        foreach (var (dayName, day) in firstWeekLastDays)
        {
            if (day != null)
            {
                continue;
            }

            firstWeek.Days[key: dayName] = new Day(numberInMonth: i,
                isToday: dateTime.Day.Equals(obj: i) && dateTime.Month.Equals(DateTime.Now.Month),
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
                    isToday: dateTime.Day.Equals(obj: i) && dateTime.Month.Equals(DateTime.Now.Month),
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
                isToday: false,
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
    
    public List<Week> Weeks { get; set; } = new(5);
    public string MonthName { get; set; }

    public string PrintTableAsHtml()
    {
        var style = """
                    <style>
                      @charset "UTF-8";
                      * {
                        margin: 0;
                        padding: 0;
                      }
                      
                      html {
                        background: #249991;
                      }
                      
                      body {
                        margin: 5% auto 0;
                        width: 280px;
                      }
                      
                      time {
                        color: white;
                        text-transform: uppercase;
                        font-weight: 300;
                        font-size: 38px;
                      }
                      time em {
                        display: block;
                        font-weight: 300;
                        font-style: normal;
                        font-size: 16px;
                      }
                      
                      header {
                        padding: 30px 0;
                        background: #4ecdc4;
                        text-align: center;
                        font-family: "Roboto Condensed", sans-serif;
                      }
                      header a {
                        display: inline-block;
                        padding: 5px 20px;
                        border-radius: 20px;
                        background: #44b3ab;
                        color: white;
                        text-decoration: none;
                        text-transform: uppercase;
                        font-weight: 300;
                        font-size: 12px;
                        transition: all 0.3s ease-in-out;
                      }
                      header a:hover {
                        background: #249991;
                        color: #ccc;
                      }
                      
                      [role="main"] {
                        overflow: hidden;
                        padding: 15px;
                        background: white;
                        font-family: "Helvetica";
                      }
                      
                      section ul {
                        list-style-type: none;
                      }
                      section li {
                        position: relative;
                        display: inline-block;
                        float: left;
                        width: 35px;
                        height: 35px;
                        text-align: center;
                        line-height: 35px;
                        zoom: 1;
                        *display: inline;
                      }
                      
                      .l-date--event {
                        cursor: pointer;
                        transition: background 0.3s ease-in-out;
                      }
                      .l-date--event:hover {
                        background: #efefef;
                      }
                      
                      .m-bullet--event {
                        position: absolute;
                        top: 5px;
                        right: 5px;
                        display: block;
                        width: 5px;
                        height: 5px;
                        border-radius: 50%;
                        background: #ff6b6b;
                      }
                      
                      .m-box--weeks {
                        color: #e66b6b;
                        text-transform: uppercase;
                        font-weight: bold;
                        font-size: 10px;
                      }
                      
                      .m-box--date {
                        color: #555;
                        font-size: 14px;
                      }
                      
                      .l-date--passed {
                        color: #bababa;
                      }
                      
                      .eventTip {
                        position: absolute;
                        width: 150px;
                        left: 50%;
                        top: -125%;
                        margin-left: -75px;
                        color: white;
                      }
                      .eventTip:before {
                        content: "▾";
                        position: absolute;
                        font-size: 25px;
                        bottom: -19px;
                        left: 46%;
                      }
                    
                      html.spring .eventTip { background: #7ab892; }
                      html.summer .eventTip { background: #45a049; }
                      html.autumn .eventTip { background: #bf5f1a; }
                      html.winter .eventTip { background: #79bcd6; }
                    
                      html.spring .eventTip:before { color: #7ab892; }
                      html.summer .eventTip:before { color: #45a049; }
                      html.autumn .eventTip:before { color: #bf5f1a; }
                      html.winter .eventTip:before { color: #79bcd6; }
                      
                      html.spring { background: #88c9a1; }
                      html.summer { background: #4CAF50; } 
                      html.autumn { background: #d2691e; }
                      html.winter { background: #87ceeb; }
                    
                      html.spring header { background: #7ab892; }
                      html.summer header { background: #45a049; }
                      html.autumn header { background: #bf5f1a; }
                      html.winter header { background: #79bcd6; }
                    </style>
                    """;
        
        var script = """
                     <script>
                       $('.l-date--event').on('click', function(){
                         var EventDescribe = $(this).attr('data-event');
                         $('#eventBox').html(EventDescribe).show();
                       });
                     
                       $('section[role="main"]').on('mouseleave', function(){
                         $('#eventBox').hide();
                       });
                     </script>
                     """;
        
        var formattedWeeks = Weeks.Select(GetFormattedWeek).ToArray();

        var seasonClass = "winter";
        var month = _dateTime.Month;
        seasonClass = month switch
        {
          >= 3 and <= 5 => "spring",
          >= 6 and <= 8 => "summer",
          >= 9 and <= 11 => "autumn",
          _ => seasonClass
        };

        return $"""
                <!DOCTYPE html>
                <html lang="en" class="{seasonClass}">
                <head>
                  <meta charset="UTF-8">
                  <meta name="viewport" content="width=device-width, initial-scale=1.0">
                  <title>{MonthName} calendar</title>
                  <link href="https://fonts.googleapis.com/css?family=Roboto+Condensed:400,300,700" rel="stylesheet" type="text/css" />
                  {style}
                </head>
                <body>
                  <header role="banner">
                    <time>{MonthName}<em>{_dateTime.Year}</em></time>
                  </header>
                  <section role="main">
                    <ul class="m-box--weeks">
                      <li>Sun</li>
                      <li>Mon</li>
                      <li>Tue</li>
                      <li>Wed</li>
                      <li>Thu</li>
                      <li>Fri</li>
                      <li>Sat</li>
                    </ul>
                    {formattedWeeks.First()}
                    {formattedWeeks[1]}
                    {formattedWeeks[2]}
                    {formattedWeeks[3]}
                    {formattedWeeks.Last()}
                  </section>
                  
                  <div id="eventBox" style="display: none; margin-top: 10px; padding: 10px; background-color: #f0f0f0; font-family: Helvetica;"></div>
                
                  <script src='https://cdnjs.cloudflare.com/ajax/libs/jquery/2.1.3/jquery.min.js'></script>
                  {script}
                </body>
                </html>
                """;
    }

    private static string GetFormattedWeek(Week firstWeek)
    {
      var builder = new StringBuilder();
      builder.AppendLine("<ul class=\"m-box--date\">");
      foreach (var day in firstWeek.Days.Select(pair => pair.Value).ToArray())
      {
        var styleClass = GetStyleForLiElement(day);

        var iElement = "";
        var dataEventAttribute = "";
        if (day!.MigraineEvents.Count != 0)
        {
            iElement = "<i class=\"m-bullet--event\"></i>";
            dataEventAttribute = GetDataEventAttributeWithContent(day);
        }
        
        var number = $"{day.NumberInMonth}";
        var boldContainer = $"<b><u>{number}</u></b>";
        builder.AppendLine($"<li class=\"{styleClass}\" {dataEventAttribute}>{iElement}{(day.IsToday ? boldContainer : number)}</li>");
      }
      builder.AppendLine("</ul>");

      var formattedWeek = builder.ToString();
      return formattedWeek;
    }

    private static string GetDataEventAttributeWithContent(Day day)
    {
      var builder = new StringBuilder();
      builder.Append("data-event=\"");
      var mEvents = day.MigraineEvents.Select(me => $"{me.StartTime:HH:mm} - {me.ScotomaSeverity.GetDescription()}").ToArray();
      foreach (var mEvent in mEvents)
      {
        builder.Append(mEvent);
        builder.Append("<br>");
      }
      builder.Append('"');
      
      return builder.ToString();
    }

    private static string GetStyleForLiElement(Day? day)
    {
      string style;
      
      if (day!.IsDayWithinCurrentMonth == false)
      {
        style = "l-date--passed";
      }
      else
      {
        style = day.MigraineEvents.Count != 0 ? "l-date--event" : "";
      }

      return style;
    }
}