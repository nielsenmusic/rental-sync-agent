using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

/// <summary>
/// Rental Calendar Sync Agent
/// Fetches iCal feeds from Airbnb and Booking.com, compares bookings,
/// generates a merged master iCal file, and logs any discrepancies.
///
/// iCal URLs are read from environment variables (set as GitHub Secrets):
///   AIRBNB_ICAL_URL
///   BOOKING_ICAL_URL
///
/// Output files (committed back to repo by GitHub Actions):
///   docs/master.ics   — served via GitHub Pages at yourdomain.com/master.ics
///   sync-log.md       — human-readable log of all sync checks
/// </summary>
class CalendarSyncAgent
{
    // Paths are relative to the repository root
    const string MasterICalPath = "docs/master.ics";
    const string LogPath        = "sync-log.md";

    static async Task Main(string[] args)
    {
        Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm}] Calendar sync agent starting...");

        // Read iCal URLs from environment variables (set as GitHub Secrets)
        var airbnbUrl  = Environment.GetEnvironmentVariable("AIRBNB_ICAL_URL");
        var bookingUrl = Environment.GetEnvironmentVariable("BOOKING_ICAL_URL");

        if (string.IsNullOrEmpty(airbnbUrl) || string.IsNullOrEmpty(bookingUrl))
        {
            await LogEntry("❌ ERROR: AIRBNB_ICAL_URL or BOOKING_ICAL_URL environment variable is missing.");
            Environment.Exit(1);
        }

        var airbnbEvents  = await FetchAndParseICal(airbnbUrl,  "Airbnb");
        var bookingEvents = await FetchAndParseICal(bookingUrl, "Booking.com");

        if (airbnbEvents == null || bookingEvents == null)
        {
            await LogEntry("❌ ERROR: Could not fetch one or both calendars. Sync aborted.");
            Environment.Exit(1);
        }

        var discrepancies = FindDiscrepancies(airbnbEvents, bookingEvents);
        var allEvents     = airbnbEvents.Concat(bookingEvents).ToList();

        WriteMasterICal(allEvents);

        if (discrepancies.Count == 0)
        {
            Console.WriteLine("✅ Calendars are in sync. No discrepancies found.");
            await LogEntry("✅ Sync check passed — calendars are in sync.");
        }
        else
        {
            Console.WriteLine($"⚠️  Found {discrepancies.Count} discrepancy(s). Logged and master calendar updated.");
            var sb = new StringBuilder();
            sb.AppendLine($"⚠️ **Sync discrepancy detected** — {discrepancies.Count} issue(s) found:");
            foreach (var d in discrepancies)
                sb.AppendLine($"   - {d}");
            sb.AppendLine("   Master calendar updated with all blocked dates.");
            await LogEntry(sb.ToString());
        }
    }

    // ─── ICAL FETCHING & PARSING ──────────────────────────────────────────────

    static async Task<List<CalendarEvent>?> FetchAndParseICal(string url, string source)
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            var ical = await client.GetStringAsync(url);
            return ParseICal(ical, source);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Failed to fetch {source} calendar: {ex.Message}");
            return null;
        }
    }

    static List<CalendarEvent> ParseICal(string ical, string source)
    {
        var events = new List<CalendarEvent>();
        var lines  = ical.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

        CalendarEvent? current = null;

        foreach (var line in lines)
        {
            if (line == "BEGIN:VEVENT")
            {
                current = new CalendarEvent { Source = source };
            }
            else if (line == "END:VEVENT" && current != null)
            {
                if (current.Start != default && current.End != default)
                    events.Add(current);
                current = null;
            }
            else if (current != null)
            {
                if (line.StartsWith("DTSTART"))
                    current.Start = ParseICalDate(line);
                else if (line.StartsWith("DTEND"))
                    current.End = ParseICalDate(line);
                else if (line.StartsWith("SUMMARY:"))
                    current.Summary = line[8..].Trim();
                else if (line.StartsWith("UID:"))
                    current.Uid = line[4..].Trim();
            }
        }

        Console.WriteLine($"  Parsed {events.Count} events from {source}");
        return events;
    }

    static DateTime ParseICalDate(string line)
    {
        var value = Regex.Match(line, @"(\d{8})(T\d{6}Z?)?$").Value;
        if (value.Length >= 8)
        {
            int y = int.Parse(value[..4]);
            int m = int.Parse(value[4..6]);
            int d = int.Parse(value[6..8]);
            return new DateTime(y, m, d);
        }
        return default;
    }

    // ─── DISCREPANCY DETECTION ────────────────────────────────────────────────

    static List<string> FindDiscrepancies(List<CalendarEvent> airbnb, List<CalendarEvent> booking)
    {
        var issues = new List<string>();

        var airbnbDates  = GetBookedDates(airbnb);
        var bookingDates = GetBookedDates(booking);

        var onlyOnAirbnb = airbnbDates.Except(bookingDates).OrderBy(d => d).ToList();
        if (onlyOnAirbnb.Any())
            issues.Add($"Booked on **Airbnb** but NOT blocked on Booking.com: {DatesToConciseRanges(onlyOnAirbnb)}");

        var onlyOnBooking = bookingDates.Except(airbnbDates).OrderBy(d => d).ToList();
        if (onlyOnBooking.Any())
            issues.Add($"Booked on **Booking.com** but NOT blocked on Airbnb: {DatesToConciseRanges(onlyOnBooking)}");

        return issues;
    }

    static HashSet<DateTime> GetBookedDates(List<CalendarEvent> events)
    {
        var dates = new HashSet<DateTime>();
        foreach (var e in events)
        {
            var day = e.Start.Date;
            while (day < e.End.Date)
            {
                dates.Add(day);
                day = day.AddDays(1);
            }
        }
        return dates;
    }

    static string DatesToConciseRanges(List<DateTime> dates)
    {
        if (!dates.Any()) return "none";
        var ranges = new List<string>();
        var start  = dates[0];
        var prev   = dates[0];

        for (int i = 1; i < dates.Count; i++)
        {
            if ((dates[i] - prev).Days > 1)
            {
                ranges.Add(start == prev ? start.ToString("yyyy-MM-dd") : $"{start:yyyy-MM-dd} → {prev:yyyy-MM-dd}");
                start = dates[i];
            }
            prev = dates[i];
        }
        ranges.Add(start == prev ? start.ToString("yyyy-MM-dd") : $"{start:yyyy-MM-dd} → {prev:yyyy-MM-dd}");
        return string.Join(", ", ranges);
    }

    // ─── MASTER ICAL GENERATION ───────────────────────────────────────────────

    static void WriteMasterICal(List<CalendarEvent> events)
    {
        var sb = new StringBuilder();
        sb.AppendLine("BEGIN:VCALENDAR");
        sb.AppendLine("VERSION:2.0");
        sb.AppendLine("PRODID:-//RentalSyncAgent//EN");
        sb.AppendLine("CALSCALE:GREGORIAN");
        sb.AppendLine("METHOD:PUBLISH");
        sb.AppendLine("X-WR-CALNAME:Master Rental Calendar");
        sb.AppendLine($"X-WR-CALDESC:Auto-generated merged calendar. Last updated: {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}");

        foreach (var e in events)
        {
            sb.AppendLine("BEGIN:VEVENT");
            sb.AppendLine($"UID:{e.Uid ?? Guid.NewGuid().ToString()}");
            sb.AppendLine($"DTSTART;VALUE=DATE:{e.Start:yyyyMMdd}");
            sb.AppendLine($"DTEND;VALUE=DATE:{e.End:yyyyMMdd}");
            sb.AppendLine($"SUMMARY:{e.Summary ?? $"Blocked ({e.Source})"}");
            sb.AppendLine($"DESCRIPTION:Synced from {e.Source} by RentalSyncAgent");
            sb.AppendLine("END:VEVENT");
        }

        sb.AppendLine("END:VCALENDAR");

        Directory.CreateDirectory(Path.GetDirectoryName(MasterICalPath)!);
        File.WriteAllText(MasterICalPath, sb.ToString());
        Console.WriteLine($"  Master iCal written ({events.Count} events)");
    }

    // ─── LOGGING ──────────────────────────────────────────────────────────────

    static async Task LogEntry(string message)
    {
        var entry = $"\n## {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC\n{message}\n";
        await File.AppendAllTextAsync(LogPath, entry);
        Console.WriteLine($"  Logged: {message}");
    }
}

// ─── DATA MODEL ───────────────────────────────────────────────────────────────

class CalendarEvent
{
    public string Source   { get; set; } = "";
    public string? Uid     { get; set; }
    public string? Summary { get; set; }
    public DateTime Start  { get; set; }
    public DateTime End    { get; set; }
}
