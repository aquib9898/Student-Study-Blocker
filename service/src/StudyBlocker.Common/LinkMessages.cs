namespace StudyBlocker.Common;

// What LockIn and the service say to each other about the link between them.
// The wire format is frozen: LockIn (an Electron app) reads these names and
// strings exactly as they are, and System.Text.Json matches property names
// case-sensitively, so changing one here means changing it there.
public static class LinkProtocol
{
    // Refusals LockIn matches on. The first five are stable codes; the last
    // is the sentence the desktop app shows in its banner, and LockIn shows it
    // too, so it's worded for a person.
    public const string NotLinked = "link:not-linked";
    public const string BadSecret = "link:bad-secret";
    public const string WrongUser = "link:wrong-user";
    public const string BadOffer = "link:bad-offer";
    public const string AlreadyLinked = "link:already-linked";
    public const string LockedFromLockIn = "Locked from LockIn, end LockIn session to disable";

    // The app's top text instead of LockedFromLockIn while LockIn has its
    // session on a break with every block released (the user's wording,
    // 2026-10-25).
    public const string SessionBreakFromLockIn = "Session-break from LockIn";

    // The top text, and every refusal, while LockIn's Habit Builder scheduled
    // lock is in force (2026-11-18). Worded for a person, like the one above.
    public const string ScheduledLockFromLockIn = "Scheduled lock from LockIn, wait for it to end";

    // The top text, and every refusal, while LockIn's Advanced lock is locked
    // (the user's wording, 2026-11-18: it "simply shows Locked from LockIn").
    public const string AdvancedLockFromLockIn = "Locked from LockIn";

    // The longest a session can be asked to last, in minutes. LockIn asks for
    // its planned length plus three hours.
    public const int MaxSessionMinutes = 720;

    // Habit Builder's scheduled lock: its pieces sit between 7 AM and midnight
    // (minutes after local midnight) and add up to at most 8 hours a day.
    public const int ScheduleBarStartMinute = 7 * 60;
    public const int ScheduleBarEndMinute = 24 * 60;
    public const int ScheduleMaxMinutesPerDay = 8 * 60;

    // Where the service leaves the pairing offer for LockIn to find. Program
    // Files, because every user can read there but only administrators and
    // the service can write, so a Standard account can't plant an offer.
    public static string OfferFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "Smart Study Distraction Blocker", "link", "pairing-offer.json");
}

public sealed record LinkStatus
{
    // A LockIn has been paired with this service.
    public bool IsLinked { get; init; }

    // A pairing offer is waiting for LockIn to pick up (nothing is linked).
    public bool OfferAvailable { get; init; }

    // A LockIn focus session is running: the rules can't loosen meanwhile.
    public bool SessionActive { get; init; }

    // That session is on a break with every block released: nothing is
    // enforced until it ends, though the rules still can't loosen.
    public bool SessionBreak { get; init; }

    // LockIn's Habit Builder scheduled lock is in force right now: the
    // session's minimums and lockdown apply and nothing can loosen, LockIn
    // running or not. A session break doesn't lift it.
    public bool ScheduleInForce { get; init; }

    // LockIn's Advanced lock is locked right now: the top of the ladder. It
    // overrides every other lock and every break until the day's study target
    // is met (or its "Break lock early" countdown runs out).
    public bool AdvancedInForce { get; init; }
}

// Payload of "schedule-set": LockIn's whole plan, replaced each time.
public sealed record ScheduleSetRequest
{
    // False = no scheduled lock at all.
    public bool Active { get; init; }

    // Oldest first; each applies from its local day ("yyyy-MM-dd") on.
    public List<ScheduleWindow> Windows { get; init; } = [];

    // Local days whose lock was lifted early because the study goal was met.
    public List<string> ReleaseDays { get; init; } = [];

    // LockIn's Advanced lock, sent with the same plan; null from an older LockIn.
    public AdvancedLockRequest? Advanced { get; init; }
}

// Locked every day from FromDay until the day is in ReleaseDays (its study
// target was met); off at EndsAtMs (Unix ms, "Break lock early").
public sealed record AdvancedLockRequest
{
    public bool Active { get; init; }
    public string? FromDay { get; init; }
    public List<string> ReleaseDays { get; init; } = [];
    public long? EndsAtMs { get; init; }
}

public sealed record ScheduleWindow
{
    public string FromDay { get; init; } = "";

    // [start, end) pairs, minutes after local midnight.
    public List<int[]> Segments { get; init; } = [];
}

// Payload of "schedule-report".
public sealed record ScheduleReportRequest
{
    public string Day { get; init; } = "";
}

// What "schedule-report" answers with: the day's 288 five-minute slots the
// scheduled lock was enforced in, as 72 hex characters, slot 0 (00:00–00:05)
// being the top bit of the first one. LockIn reads the same format from the
// phones.
public sealed record ScheduleReport
{
    public string Day { get; init; } = "";
    public string Slots { get; init; } = "";
}

// Payload of "pair".
public sealed record PairRequest
{
    public string Offer { get; init; } = "";
}

// What "pair" answers with. The only time the secret is ever sent.
public sealed record PairReply
{
    public string Secret { get; init; } = "";
}

// Payload of "session-start".
public sealed record SessionStartRequest
{
    public int MaxMinutes { get; init; }
}
