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
}
