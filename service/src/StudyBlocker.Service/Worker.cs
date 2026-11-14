using System.Security.Principal;
using System.Text.Json;
using StudyBlocker.Common;
using StudyBlocker.Service.AppBlocking;
using StudyBlocker.Service.Classification;
using StudyBlocker.Service.Ipc;
using StudyBlocker.Service.Policies;

namespace StudyBlocker.Service;

public sealed class Worker : BackgroundService
{
    // Lock time is counted and the browser policies re-checked this often,
    // so a lock (or a break countdown) ends within a few seconds of its time.
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ResyncInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(2);

    // Blocked programs that got running are closed within this long.
    private static readonly TimeSpan AppSweepInterval = TimeSpan.FromSeconds(1);

    private readonly ILogger<Worker> _logger;
    private readonly TrustedClock _clock;
    private readonly NetworkTimeSource _timeSource;
    private readonly LockStateStore _lockStore;
    private readonly PolicyWriter _policies;
    private readonly ClassifierStore _classifier;
    private readonly LinkService _links;
    private readonly AppWatcher _apps;
    private readonly PipeServer _pipeServer;
    private bool _offerFailed;

    public Worker(
        ILogger<Worker> logger,
        ILogger<PipeServer> pipeLogger,
        TrustedClock clock,
        NetworkTimeSource timeSource,
        LockStateStore lockStore,
        PolicyWriter policies,
        ClassifierStore classifier,
        LinkService links,
        AppWatcher apps)
    {
        _logger = logger;
        _clock = clock;
        _timeSource = timeSource;
        _lockStore = lockStore;
        _policies = policies;
        _classifier = classifier;
        _links = links;
        _apps = apps;
        _pipeServer = new PipeServer(HandleRequest, pipeLogger);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Smart Study Distraction Blocker service starting");
        var pipeTask = _pipeServer.RunAsync(stoppingToken);
        var appTask = SweepAppsAsync(stoppingToken);

        var nextSyncMs = _clock.MonotonicMs;
        try
        {
            while (true)
            {
                if (_clock.MonotonicMs >= nextSyncMs)
                {
                    var interval = await SyncClockAsync(stoppingToken) ? ResyncInterval : RetryInterval;
                    nextSyncMs = _clock.MonotonicMs + (long)interval.TotalMilliseconds;
                }

                switch (_lockStore.Advance())
                {
                    case LockEnd.TimeUp:
                        _logger.LogInformation("Lock ended: its full duration has passed on the trusted clock");
                        break;
                    case LockEnd.BrokenEarly:
                        _logger.LogInformation("Lock ended early: the break countdown has passed on the trusted clock");
                        break;
                }

                if (_lockStore.TakeSessionEnd() is { } sessionEnd)
                {
                    _logger.LogInformation(sessionEnd == SessionEnd.LeaseExpired
                        ? "LockIn session ended: LockIn stopped sending heartbeats"
                        : "LockIn session ended: it ran as long as LockIn said it could");
                }

                KeepOfferFresh();
                EnforcePolicies();
                await Task.Delay(TickInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        await pipeTask;
        await appTask;
    }

    // Its own loop, faster than the tick: a blocked program should vanish at
    // once, not after up to five seconds of use.
    private async Task SweepAppsAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                try
                {
                    foreach (var ended in _apps.Sweep(_lockStore.Enforced))
                        _logger.LogInformation("Blocked app: {Ended}", ended);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Closing blocked apps failed");
                }
                await Task.Delay(AppSweepInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> SyncClockAsync(CancellationToken cancellationToken)
    {
        var time = await _timeSource.TryGetTimeAsync(() => _clock.MonotonicMs, cancellationToken);
        if (time is null)
        {
            _logger.LogWarning("Network time sync failed; retrying in {Interval}", RetryInterval);
            return false;
        }

        _clock.ApplyNetworkTime(time.Value.Utc, time.Value.MeasuredAtMs);
        _logger.LogInformation(
            "Trusted clock synced from the network; the system clock is off by {Skew}",
            DateTimeOffset.UtcNow - _clock.UtcNow!.Value);
        return true;
    }

    // Keeps a pairing offer waiting for LockIn while nothing is linked. One
    // file check when there's nothing to do, so it runs every tick, which also
    // puts back an offer that was deleted by hand.
    private void KeepOfferFresh()
    {
        try
        {
            _links.EnsureOffer();
            _offerFailed = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_offerFailed) _logger.LogWarning(ex, "Couldn't leave a pairing offer for LockIn");
            _offerFailed = true;
        }
    }

    // What is enforced, not what is set: on a LockIn session-break the two
    // differ (LockStateStore.Enforced).
    private void EnforcePolicies()
    {
        try
        {
            var (state, sessionLock) = _lockStore.Enforcement;
            var result = _policies.Apply(state, sessionLock);
            foreach (var change in result.Applied)
                _logger.LogInformation("Browser policy: {Change}", change);
            foreach (var change in result.Repaired)
                _logger.LogWarning("Browser policy was changed outside the service; restored {Change}", change);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Applying browser policies failed");
        }
    }

    private IpcResponse HandleRequest(IpcRequest request, WindowsIdentity identity)
    {
        // The browser extension is answered with what is enforced; everyone
        // else (the app, LockIn) with the settings themselves.
        var fromExtension = request.From == IpcProtocol.FromExtension;
        try
        {
            // Who connected, as Windows sees them. The link requests also
            // need the secret, and it has to be this account's.
            var sid = identity.User?.Value;
            switch (request.Type)
            {
                case "ping":
                    return IpcResponse.Success("pong");
                case "get-status":
                    return IpcResponse.Success(Status(fromExtension));
                case "classify-video":
                {
                    var rules = _lockStore.Enforced.Rules;
                    // Switched off in the app, or a LockIn session-break:
                    // nothing is judged (or cached), and every video plays.
                    if (!rules.ExtensionEnabled) return IpcResponse.Success(ClassifyResult.SwitchedOff);
                    return IpcResponse.Success(_classifier.Classify(Payload<VideoMetadata>(request), educationOnly: rules.EducationOnly));
                }
                case "set-channel-rule":
                {
                    var channelRule = Payload<ChannelRuleRequest>(request);
                    var (channel, decision) = ValidChannelRule(channelRule);
                    try
                    {
                        _classifier.SetChannelRule(channel, decision, HoldsRatchet(), channelRule.VideoId);
                    }
                    catch (InvalidOperationException) when (_lockStore.SessionActive)
                    {
                        // Same sentence as every other refusal during a
                        // LockIn session, whatever the list's own wording.
                        throw new InvalidOperationException(LinkProtocol.LockedFromLockIn);
                    }
                    break;
                }
                case "list-channel-rules":
                    return IpcResponse.Success(_classifier.GetChannelRules(HoldsRatchet()));
                case "update-rules":
                    _lockStore.UpdateRules(RuleRequests.Valid(Payload<RuleSet>(request)));
                    break;
                case "start-lock":
                    _lockStore.StartLock(TimeSpan.FromMinutes(Payload<LockRequest>(request).Minutes));
                    break;
                case "extend-lock":
                    _lockStore.ExtendLock(TimeSpan.FromMinutes(Payload<LockRequest>(request).Minutes));
                    break;
                case "break-lock":
                    _lockStore.BreakLock();
                    break;
                case "cancel-break":
                    _lockStore.CancelBreak();
                    break;

                // The link to LockIn (see LinkService for who may ask what).
                case "pair":
                {
                    var secret = _links.Pair(Payload<PairRequest>(request), sid);
                    _logger.LogInformation("LockIn was linked by {User}", identity.Name);
                    return IpcResponse.Success(new PairReply { Secret = secret });
                }
                case "reset-link":
                    _links.ResetLink();
                    break;
                case "unpair":
                    _links.Unpair(request.Secret, sid);
                    break;
                case "session-start":
                    _links.StartSession(request.Secret, sid, Payload<SessionStartRequest>(request).MaxMinutes);
                    break;
                case "session-heartbeat":
                    _links.Heartbeat(request.Secret, sid);
                    break;
                case "session-break":
                    _links.BreakSession(request.Secret, sid);
                    break;
                case "session-end":
                    _links.EndSession(request.Secret, sid);
                    break;
                case "override-rules":
                    _links.OverrideRules(request.Secret, sid, RuleRequests.Valid(RuleRequests.KeepUnsent(request.Payload, Payload<RuleSet>(request), _lockStore.Current.Rules)));
                    break;
                case "override-end-lock":
                    _links.OverrideEndLock(request.Secret, sid);
                    break;
                default:
                    return IpcResponse.Failure($"unknown request type '{request.Type}'");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or JsonException)
        {
            _logger.LogInformation("Refused '{Type}' from {User}: {Reason}", request.Type, identity.Name, ex.Message);
            return IpcResponse.Failure(ex.Message);
        }

        _logger.LogInformation("'{Type}' accepted from {User}", request.Type, identity.Name);
        EnforcePolicies();
        return IpcResponse.Success(Status(fromExtension));
    }

    // `enforced`: the extension's view (see LockStateStore.Enforced). On a
    // LockIn session-break it reads as unlocked and switched off, which every
    // part of the extension already treats as "nothing applies", so a break
    // needs no new extension.
    private ServiceStatus Status(bool enforced) => new()
    {
        Lock = enforced ? _lockStore.Enforced : _lockStore.Current,
        TrustedUtcNow = _clock.UtcNow,
        SystemUtcNow = DateTimeOffset.UtcNow,
        BreakDelayMs = (long)LockStateStore.BreakDelay.TotalMilliseconds,
        Link = _links.Status,
    };

    // The channel lists follow the same ratchet as the rules: held by a lock,
    // and by a LockIn session.
    private bool HoldsRatchet() => _lockStore.Current.IsLocked || _lockStore.SessionActive;

    private static T Payload<T>(IpcRequest request) where T : class =>
        request.Payload?.Deserialize<T>() ?? throw new ArgumentException($"'{request.Type}' needs a payload");

    private static (ChannelRef Channel, ChannelDecision? Decision) ValidChannelRule(ChannelRuleRequest request)
    {
        var id = string.IsNullOrWhiteSpace(request.ChannelId) ? null : request.ChannelId.Trim();
        var name = string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim();
        if (id is null && name is null)
            throw new ArgumentException("A channel rule needs a channel id or name.");
        if (name is { Length: > 200 })
            throw new ArgumentException("That channel name is too long.");
        ChannelDecision? decision = request.Rule.Trim().ToLowerInvariant() switch
        {
            "allow" => ChannelDecision.Allowed,
            "block" => ChannelDecision.Blocked,
            "clear" => null,
            _ => throw new ArgumentException($"Unknown channel rule '{request.Rule}'; use Allow, Block or Clear."),
        };
        return (id is not null ? ChannelRef.ById(id, name) : ChannelRef.ByName(name!), decision);
    }
}
