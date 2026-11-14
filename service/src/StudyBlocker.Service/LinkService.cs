using StudyBlocker.Common;

namespace StudyBlocker.Service;

// What a linked LockIn may ask for, and the checks in front of each. The pipe
// calls this with the request's secret and the Windows account (SID) that
// connected; every refusal is an InvalidOperationException or
// ArgumentException, which the worker turns into an `Ok:false` reply.
//
// `pair` needs only the offer. Everything else needs the secret, from the
// account that paired, and while a LockIn session is running the ones that
// would loosen something (unpair, the overrides) are refused outright.
public sealed class LinkService
{
    private readonly LinkStore _link;
    private readonly LockStateStore _locks;

    public LinkService(LinkStore link, LockStateStore locks)
    {
        _link = link;
        _locks = locks;
    }

    public LinkStatus Status => new()
    {
        IsLinked = _link.IsLinked,
        OfferAvailable = _link.OfferAvailable,
        SessionActive = _locks.SessionActive,
        SessionBreak = _locks.SessionOnBreak,
    };

    public void EnsureOffer() => _link.EnsureOffer();

    public string Pair(PairRequest request, string? userSid) => _link.Pair(request.Offer, userSid);

    public void Unpair(string? secret, string? userSid)
    {
        _link.Require(secret, userSid);
        RefuseDuringSession();
        _link.Unpair();
    }

    // The app's reload arrow: forgets the link, with no secret, so LockIn can
    // link again after it lost its own. Only the session holds it back.
    public void ResetLink()
    {
        RefuseDuringSession();
        _link.Unpair();
    }

    public void StartSession(string? secret, string? userSid, int maxMinutes)
    {
        _link.Require(secret, userSid);
        if (maxMinutes < 1 || maxMinutes > LinkProtocol.MaxSessionMinutes)
            throw new ArgumentOutOfRangeException(nameof(maxMinutes), $"A session can last 1 to {LinkProtocol.MaxSessionMinutes} minutes.");
        _locks.StartSession(TimeSpan.FromMinutes(maxMinutes));
    }

    public void Heartbeat(string? secret, string? userSid)
    {
        _link.Require(secret, userSid);
        _locks.Heartbeat();
    }

    public void BreakSession(string? secret, string? userSid)
    {
        _link.Require(secret, userSid);
        _locks.BreakSession();
    }

    public void EndSession(string? secret, string? userSid)
    {
        _link.Require(secret, userSid);
        _locks.EndSession();
    }

    public void OverrideRules(string? secret, string? userSid, RuleSet rules)
    {
        _link.Require(secret, userSid);
        _locks.OverrideRules(rules);
    }

    public void OverrideEndLock(string? secret, string? userSid)
    {
        _link.Require(secret, userSid);
        _locks.OverrideEndLock();
    }

    private void RefuseDuringSession()
    {
        if (_locks.SessionActive) throw new InvalidOperationException(LinkProtocol.LockedFromLockIn);
    }
}
