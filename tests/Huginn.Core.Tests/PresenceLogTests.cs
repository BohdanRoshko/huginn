using Xunit;

namespace Huginn.Tests;

/// <summary>The Kumospace work timer: totals per state, the 6-minute cap, the midnight split, the week view.</summary>
public class PresenceLogTests
{
    private static string Day(string dir, string date, params string[] rows)
    {
        File.WriteAllLines(Path.Combine(dir, date + ".tsv"), rows);
        return dir;
    }

    [Fact]
    public void AddsUpEachStateUntilTheNextLine()
    {
        var dir = Day(TestData.NewDir(), "2026-11-17",
            "09:00:00\tavailable", "09:04:00\tavailable", "09:08:00\tfocusing", "09:10:00\taway", "09:12:00\tclosed");
        var t = PresenceLog.Totals(dir, new DateTime(2026, 11, 17), new DateTime(2026, 11, 18, 12, 0, 0));
        Assert.Equal(TimeSpan.FromMinutes(8), t["available"]);
        Assert.Equal(TimeSpan.FromMinutes(2), t["focusing"]);
        Assert.Equal(TimeSpan.FromMinutes(2), t["away"]);
    }

    [Fact]
    public void AStateLastsAtMostSixMinutes()
    {
        // The app writes a heartbeat every 5 min; a gap past 6 min means it (or the PC) was down.
        var dir = Day(TestData.NewDir(), "2026-11-17", "09:00:00\tavailable", "11:00:00\tavailable");
        var t = PresenceLog.Totals(dir, new DateTime(2026, 11, 17), new DateTime(2026, 11, 17, 11, 3, 0));
        Assert.Equal(TimeSpan.FromMinutes(6 + 3), t["available"]);
    }

    [Fact]
    public void TheLastStateOfAPastDayEndsAtMidnight()
    {
        var dir = Day(TestData.NewDir(), "2026-11-17", "23:58:00\tavailable");
        var t = PresenceLog.Totals(dir, new DateTime(2026, 11, 17), new DateTime(2026, 11, 18, 9, 0, 0));
        Assert.Equal(TimeSpan.FromMinutes(2), t["available"]);
    }

    [Fact]
    public void ObserveWritesOnChangeOnly()
    {
        var dir = TestData.NewDir();
        var log = new PresenceLog(dir);
        var at = new DateTime(2026, 11, 18, 10, 0, 0);
        log.Observe(at, "available");
        log.Observe(at.AddSeconds(1), "available");
        log.Observe(at.AddSeconds(2), "away");
        var lines = File.ReadAllLines(Path.Combine(dir, "2026-11-18.tsv"));
        Assert.Equal(new[] { "10:00:00\tavailable", "10:00:02\taway" }, lines);
    }

    [Fact]
    public void DescribesTheDay()
    {
        var totals = new Dictionary<string, TimeSpan>
        {
            ["available"] = new(5, 12, 0), ["focusing"] = TimeSpan.FromMinutes(40), ["away"] = TimeSpan.FromMinutes(20),
        };
        Assert.Equal("5h 12m available · 40m focusing", PresenceLog.Describe(totals));
        Assert.Equal("5h 12m available · 40m focusing · 20m away", PresenceLog.Describe(totals, withAway: true));
        Assert.Equal("nothing yet", PresenceLog.Describe(new()));
    }

    [Fact]
    public void TheWeekRunsMondayToSunday()
    {
        var dir = Day(TestData.NewDir(), "2026-11-16", "09:00:00\tavailable", "09:05:00\tclosed");
        var week = PresenceLog.Week(dir, new DateTime(2026, 11, 18, 12, 0, 0)); // a Wednesday
        Assert.Equal(7, week.Count);
        Assert.Equal("Mon 16 Nov:  5m available", week[0]);
        Assert.Equal("Tue 17 Nov:  —", week[1]);
        Assert.Equal("Today:  nothing yet", week[2]);
        Assert.Equal("Sun 22 Nov:  —", week[6]);
    }

    [Fact]
    public void TheWeekTotalAddsTheDaysFromMondayUpToNow()
    {
        var dir = TestData.NewDir();
        Day(dir, "2026-11-15", "09:00:00\tavailable", "09:05:00\tclosed");                     // the Sunday before: not this week
        Day(dir, "2026-11-16", "09:00:00\tavailable", "09:05:00\tfocusing", "09:10:00\taway", "09:15:00\tclosed");
        Day(dir, "2026-11-17", "10:00:00\tavailable", "10:05:00\tavailable", "10:10:00\tclosed");
        Day(dir, "2026-11-18", "11:58:00\tavailable");                                         // today, still open
        var now = new DateTime(2026, 11, 18, 12, 0, 0); // a Wednesday

        var week = PresenceLog.WeekTotals(dir, now);
        Assert.Equal(TimeSpan.FromMinutes(5 + 10 + 2), week["available"]); // Monday 5, Tuesday 10, today 2 up to now
        Assert.Equal(TimeSpan.FromMinutes(5), week["focusing"]);
        Assert.Equal(TimeSpan.FromMinutes(5), week["away"]);
        Assert.Equal("This week:  17m available · 5m focusing · 5m away", PresenceLog.WeekTotalLine(dir, now));

        // The same days as the day lines: their available minutes add up to the week's.
        Assert.Equal(new[] { "Mon 16 Nov:  5m available · 5m focusing · 5m away", "Tue 17 Nov:  10m available", "Today:  2m available" },
            PresenceLog.Week(dir, now).Take(3));
    }

    [Fact]
    public void TheWeekTotalReadsInHoursAndIsEmptyOnAMondayMorning()
    {
        var dir = TestData.NewDir();
        var rows = new List<string>();
        for (var t = new TimeSpan(8, 0, 0); t < new TimeSpan(17, 30, 0); t += TimeSpan.FromMinutes(5)) rows.Add($"{t:hh\\:mm\\:ss}\tavailable");
        rows.Add("17:30:00\tclosed");
        foreach (var day in new[] { "2026-11-16", "2026-11-17" }) Day(dir, day, rows.ToArray());
        Assert.Equal("This week:  19h 00m available", PresenceLog.WeekTotalLine(dir, new DateTime(2026, 11, 18, 7, 0, 0)));
        Assert.Equal("This week:  nothing yet", PresenceLog.WeekTotalLine(TestData.NewDir(), new DateTime(2026, 11, 16, 7, 0, 0)));
    }

    [Fact]
    public void WithoutAnOdinNoHoursAreWritten()
    {
        // The timer is for the one user who has an Odin; for anyone else it leaves nothing on disk.
        var dir = Path.Combine(TestData.NewDir(), "Presence");
        var log = new PresenceLog(dir, enabled: false);
        var at = new DateTime(2026, 11, 18, 10, 0, 0);
        log.Observe(at, "available");
        log.Observe(at.AddMinutes(10), "away");
        log.Observe(at.AddDays(1), "off");
        Assert.False(Directory.Exists(dir));
    }
}
