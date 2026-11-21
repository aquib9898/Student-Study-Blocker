using System.Globalization;
using StudyBlocker.Common;

namespace StudyBlocker.Service;

// LockIn's Habit Builder scheduled lock (2026-11-18). From Habit Builder's
// Level 3 the student's distracting sites are locked every day inside a window
// they chose (pieces between 7 AM and midnight, at most 8 hours). From Level 6,
// reaching the day's study goal lifts it for the rest of the day. LockIn
// decides all of it and hands the whole plan over ("schedule-set"); the service
// holds it on its own clock from then on, LockIn running or not, and records
// the five-minute slots it actually enforced, which LockIn reads back
// ("schedule-report") to verify the day.
public sealed record ScheduledLockPlan
{
    public bool Active { get; init; }
    public List<ScheduledWindow> Windows { get; init; } = [];
    public List<DateOnly> ReleaseDays { get; init; } = [];

    // The Advanced lock (2026-11-18): all day, every day from AdvancedFromDay,
    // except days whose target was met; off at AdvancedEndsAtMs.
    public bool AdvancedActive { get; init; }
    public DateOnly? AdvancedFromDay { get; init; }
    public List<DateOnly> AdvancedReleaseDays { get; init; } = [];
    public long? AdvancedEndsAtMs { get; init; }

    public bool AdvancedInForceAt(DateTime local, long utcNowMs)
    {
        if (!AdvancedActive || AdvancedFromDay is not { } from) return false;
        var day = DateOnly.FromDateTime(local);
        if (day < from || AdvancedReleaseDays.Contains(day)) return false;
        return AdvancedEndsAtMs is not { } ends || utcNowMs < ends;
    }

    // The pieces in force on `day`: the latest window starting on or before it.
    public IReadOnlyList<(int Start, int End)> SegmentsOn(DateOnly day) =>
        Windows.Where(w => w.FromDay <= day).MaxBy(w => w.FromDay)?.Segments.Select(s => (s[0], s[1])).ToList()
        ?? [];

    public bool InForceAt(DateTime local)
    {
        if (!Active) return false;
        var day = DateOnly.FromDateTime(local);
        if (ReleaseDays.Contains(day)) return false;
        var minute = local.Hour * 60 + local.Minute;
        return SegmentsOn(day).Any(s => minute >= s.Start && minute < s.End);
    }

    // Checks LockIn's request and turns it into a plan. Every refusal is an
    // ArgumentException, which the worker answers with.
    public static ScheduledLockPlan From(ScheduleSetRequest request)
    {
        if (request.Windows.Count > 20) throw new ArgumentException("A schedule keeps at most 20 windows.");
        if (request.ReleaseDays.Count > 31) throw new ArgumentException("A schedule keeps at most 31 released days.");
        var windows = request.Windows.Select(w =>
        {
            var day = ParseDay(w.FromDay);
            if (w.Segments.Count > 24) throw new ArgumentException("A window has at most 24 pieces.");
            var segments = new List<int[]>();
            foreach (var s in w.Segments)
            {
                if (s is not { Length: 2 } || s[0] < LinkProtocol.ScheduleBarStartMinute || s[1] > LinkProtocol.ScheduleBarEndMinute || s[0] >= s[1])
                    throw new ArgumentException("Each piece of a scheduled lock lies between 7 AM and midnight.");
                segments.Add([s[0], s[1]]);
            }
            if (segments.Sum(s => s[1] - s[0]) > LinkProtocol.ScheduleMaxMinutesPerDay)
                throw new ArgumentException("A scheduled lock is at most 8 hours a day.");
            return new ScheduledWindow { FromDay = day, Segments = segments };
        }).OrderBy(w => w.FromDay).ToList();
        var advanced = request.Advanced;
        if (advanced is { ReleaseDays.Count: > 31 }) throw new ArgumentException("The Advanced lock keeps at most 31 released days.");
        return new ScheduledLockPlan
        {
            Active = request.Active,
            Windows = windows,
            ReleaseDays = request.ReleaseDays.Select(ParseDay).Distinct().ToList(),
            AdvancedActive = advanced?.Active == true && advanced.FromDay is not null,
            AdvancedFromDay = advanced?.FromDay is { } from ? ParseDay(from) : null,
            AdvancedReleaseDays = advanced?.ReleaseDays.Select(ParseDay).Distinct().ToList() ?? [],
            AdvancedEndsAtMs = advanced?.EndsAtMs,
        };
    }

    public static DateOnly ParseDay(string text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : throw new ArgumentException($"'{text}' is not a day (yyyy-MM-dd).");
}

public sealed record ScheduledWindow
{
    public DateOnly FromDay { get; init; }
    public List<int[]> Segments { get; init; } = [];
}

// A day's enforced slots: 288 bits, slot 0 = 00:00–00:05 as the top bit of the
// first hex character (the same format LockIn and its phones use).
public static class ScheduleSlots
{
    public const int SlotMinutes = 5;
    public const int PerDay = 24 * 60 / SlotMinutes;
    public const int HexLength = PerDay / 4;

    public static string Empty => new('0', HexLength);

    public static int IndexOf(DateTime local) => (local.Hour * 60 + local.Minute) / SlotMinutes;

    // `hex` with slot `index` set.
    public static string Set(string hex, int index)
    {
        var chars = (hex.Length == HexLength ? hex : Empty).ToCharArray();
        var nibble = Convert.ToInt32(chars[index / 4].ToString(), 16) | (8 >> (index % 4));
        chars[index / 4] = nibble.ToString("x")[0];
        return new string(chars);
    }

    public static bool Has(string hex, int index) =>
        hex.Length == HexLength && (Convert.ToInt32(hex[index / 4].ToString(), 16) & (8 >> (index % 4))) != 0;
}
