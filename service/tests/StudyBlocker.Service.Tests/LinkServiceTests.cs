using System.Text.Json;
using StudyBlocker.Common;

namespace StudyBlocker.Service.Tests;

// What a linked LockIn may ask for, and the checks in front of each request.
public sealed class LinkServiceTests : IDisposable
{
    private const string Me = "S-1-5-21-1-2-3-1001";
    private const string SomeoneElse = "S-1-5-21-1-2-3-1002";

    private readonly InMemoryStateStore _disk = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ssb-linkservice-tests-" + Guid.NewGuid().ToString("N"));
    private readonly LockStateStore _locks;
    private readonly LinkService _service;
    private long _monotonicMs = 1_000;

    public LinkServiceTests()
    {
        Directory.CreateDirectory(_folder);
        _locks = new LockStateStore(_disk, new TrustedClock(() => _monotonicMs));
        _service = new LinkService(new LinkStore(_disk, Path.Combine(_folder, "pairing-offer.json")), _locks);
        _service.EnsureOffer();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Link()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_folder, "pairing-offer.json")));
        var offer = document.RootElement.GetProperty("Offer").GetString()!;
        return _service.Pair(new PairRequest { Offer = offer }, Me);
    }

    // Every request that needs the secret, as a call taking the secret and account.
    private IEnumerable<(string Name, Action<string?, string?> Call)> SecretRequests()
    {
        yield return ("unpair", (secret, sid) => _service.Unpair(secret, sid));
        yield return ("session-start", (secret, sid) => _service.StartSession(secret, sid, 60));
        yield return ("session-heartbeat", (secret, sid) => _service.Heartbeat(secret, sid));
        yield return ("session-break", (secret, sid) => _service.BreakSession(secret, sid));
        yield return ("session-end", (secret, sid) => _service.EndSession(secret, sid));
        yield return ("override-rules", (secret, sid) => _service.OverrideRules(secret, sid, new RuleSet()));
        yield return ("override-end-lock", (secret, sid) => _service.OverrideEndLock(secret, sid));
    }

    private static void AssertRefused(string message, Action action) =>
        Assert.Equal(message, Assert.Throws<InvalidOperationException>(action).Message);

    [Fact]
    public void Nothing_but_pairing_works_before_a_link()
    {
        foreach (var (_, call) in SecretRequests())
            AssertRefused(LinkProtocol.NotLinked, () => call("anything", Me));
    }

    [Fact]
    public void Every_request_needs_the_right_secret_and_the_right_account()
    {
        var secret = Link();
        foreach (var (_, call) in SecretRequests())
        {
            AssertRefused(LinkProtocol.BadSecret, () => call("wrong", Me));
            AssertRefused(LinkProtocol.BadSecret, () => call(null, Me));
            AssertRefused(LinkProtocol.WrongUser, () => call(secret, SomeoneElse));
        }

        // Nothing above got through.
        Assert.False(_locks.SessionActive);
    }

    [Fact]
    public void The_status_reports_the_offer_the_link_and_the_session()
    {
        Assert.Equal(new LinkStatus { IsLinked = false, OfferAvailable = true, SessionActive = false }, _service.Status);

        var secret = Link();
        Assert.Equal(new LinkStatus { IsLinked = true, OfferAvailable = false, SessionActive = false }, _service.Status);

        _service.StartSession(secret, Me, 60);
        Assert.Equal(new LinkStatus { IsLinked = true, OfferAvailable = false, SessionActive = true }, _service.Status);
    }

    [Fact]
    public void A_session_switches_the_extension_on_and_off_again_around_LockIn()
    {
        var secret = Link();
        _locks.UpdateRules(new RuleSet { ExtensionEnabled = false });

        _service.StartSession(secret, Me, 120);
        _service.Heartbeat(secret, Me);
        Assert.True(_locks.Current.Rules.ExtensionEnabled);

        _service.EndSession(secret, Me);
        Assert.False(_locks.SessionActive);
        Assert.False(_locks.Current.Rules.ExtensionEnabled);
    }

    [Fact]
    public void A_break_releases_everything_and_the_status_says_so_until_the_session_starts_again()
    {
        var secret = Link();
        _locks.StartLock(TimeSpan.FromHours(1));
        _service.StartSession(secret, Me, 120);

        _service.BreakSession(secret, Me);
        Assert.Equal(new LinkStatus { IsLinked = true, OfferAvailable = false, SessionActive = true, SessionBreak = true }, _service.Status);
        Assert.True(_locks.Current.IsLocked);
        Assert.False(_locks.Enforced.IsLocked);

        _service.StartSession(secret, Me, 120); // the break is over
        Assert.False(_service.Status.SessionBreak);
        Assert.True(_locks.Enforced.IsLocked);
    }

    [Fact]
    public void A_session_length_must_be_between_one_minute_and_twelve_hours()
    {
        var secret = Link();

        Assert.Throws<ArgumentOutOfRangeException>(() => _service.StartSession(secret, Me, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => _service.StartSession(secret, Me, LinkProtocol.MaxSessionMinutes + 1));
        Assert.False(_locks.SessionActive);

        _service.StartSession(secret, Me, LinkProtocol.MaxSessionMinutes);
        Assert.True(_locks.SessionActive);
    }

    [Fact]
    public void Unlinking_is_refused_during_a_session_and_works_after()
    {
        var secret = Link();
        _service.StartSession(secret, Me, 60);

        AssertRefused(LinkProtocol.LockedFromLockIn, () => _service.Unpair(secret, Me));
        Assert.True(_service.Status.IsLinked);

        _service.EndSession(secret, Me);
        _service.Unpair(secret, Me);
        Assert.False(_service.Status.IsLinked);
        Assert.True(_service.Status.OfferAvailable);
        AssertRefused(LinkProtocol.NotLinked, () => _service.StartSession(secret, Me, 60));
    }

    [Fact]
    public void The_reload_arrow_forgets_the_link_without_a_secret_except_during_a_session()
    {
        var secret = Link();
        _service.StartSession(secret, Me, 60);
        AssertRefused(LinkProtocol.LockedFromLockIn, () => _service.ResetLink());
        Assert.True(_service.Status.IsLinked);

        _service.EndSession(secret, Me);
        _service.ResetLink();

        Assert.Equal(new LinkStatus { IsLinked = false, OfferAvailable = true, SessionActive = false }, _service.Status);
        AssertRefused(LinkProtocol.NotLinked, () => _service.StartSession(secret, Me, 60));
        _service.ResetLink(); // harmless when nothing is linked
    }

    [Fact]
    public void The_overrides_work_through_a_lock_but_not_during_a_session()
    {
        var secret = Link();
        _locks.StartLock(TimeSpan.FromDays(2));

        _service.OverrideRules(secret, Me, new RuleSet { ExtensionEnabled = false });
        Assert.False(_locks.Current.Rules.ExtensionEnabled);
        Assert.True(_locks.Current.IsLocked);

        _service.StartSession(secret, Me, 60);
        AssertRefused(LinkProtocol.LockedFromLockIn, () => _service.OverrideRules(secret, Me, new RuleSet()));
        AssertRefused(LinkProtocol.LockedFromLockIn, () => _service.OverrideEndLock(secret, Me));
        Assert.True(_locks.Current.IsLocked);

        _service.EndSession(secret, Me);
        _service.OverrideEndLock(secret, Me);
        Assert.False(_locks.Current.IsLocked);
    }
}
