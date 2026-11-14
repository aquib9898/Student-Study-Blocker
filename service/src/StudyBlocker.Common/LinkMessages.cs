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

    // The longest a session can be asked to last, in minutes. LockIn asks for
    // its planned length plus three hours.
    public const int MaxSessionMinutes = 720;

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
