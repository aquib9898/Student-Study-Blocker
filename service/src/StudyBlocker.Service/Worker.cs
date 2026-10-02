using System.Security.Principal;
using System.Text.Json;
using StudyBlocker.Common;
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

    private readonly ILogger<Worker> _logger;
    private readonly TrustedClock _clock;
    private readonly NetworkTimeSource _timeSource;
    private readonly LockStateStore _lockStore;
    private readonly PolicyWriter _policies;
    private readonly ClassifierStore _classifier;
    private readonly PipeServer _pipeServer;

    public Worker(
        ILogger<Worker> logger,
        ILogger<PipeServer> pipeLogger,
        TrustedClock clock,
        NetworkTimeSource timeSource,
        LockStateStore lockStore,
        PolicyWriter policies,
        ClassifierStore classifier)
    {
        _logger = logger;
        _clock = clock;
        _timeSource = timeSource;
        _lockStore = lockStore;
        _policies = policies;
        _classifier = classifier;
        _pipeServer = new PipeServer(HandleRequest, pipeLogger);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Smart Study Distraction Blocker service starting");
        var pipeTask = _pipeServer.RunAsync(stoppingToken);

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

                EnforcePolicies();
                await Task.Delay(TickInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        await pipeTask;
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

    private void EnforcePolicies()
    {
        try
        {
            var result = _policies.Apply(_lockStore.Current);
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
        try
        {
            switch (request.Type)
            {
                case "ping":
                    return IpcResponse.Success("pong");
                case "get-status":
                    return IpcResponse.Success(Status());
                case "classify-video":
                {
                    var rules = _lockStore.Current.Rules;
                    // Switched off in the app: nothing is judged (or cached),
                    // and every video plays.
                    if (!rules.ExtensionEnabled) return IpcResponse.Success(ClassifyResult.SwitchedOff);
                    return IpcResponse.Success(_classifier.Classify(Payload<VideoMetadata>(request), educationOnly: rules.EducationOnly));
                }
                case "set-channel-rule":
                {
                    var channelRule = Payload<ChannelRuleRequest>(request);
                    var (channel, decision) = ValidChannelRule(channelRule);
                    _classifier.SetChannelRule(channel, decision, _lockStore.Current.IsLocked, channelRule.VideoId);
                    break;
                }
                case "list-channel-rules":
                    return IpcResponse.Success(_classifier.GetChannelRules(_lockStore.Current.IsLocked));
                case "update-rules":
                    _lockStore.UpdateRules(ValidRules(Payload<RuleSet>(request)));
                    break;
                case "start-lock":
                {
                    var lockRequest = Payload<LockRequest>(request);
                    if (lockRequest.DevKeepDevTools)
                        _logger.LogWarning("Starting a lock with --dev-keep-devtools: DevTools stays enabled. Local testing only — never do this in a real deployment.");
                    _lockStore.StartLock(TimeSpan.FromMinutes(lockRequest.Minutes), includeDevToolsLockdown: !lockRequest.DevKeepDevTools);
                    break;
                }
                case "extend-lock":
                    _lockStore.ExtendLock(TimeSpan.FromMinutes(Payload<LockRequest>(request).Minutes));
                    break;
                case "break-lock":
                    _lockStore.BreakLock();
                    break;
                case "cancel-break":
                    _lockStore.CancelBreak();
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
        return IpcResponse.Success(Status());
    }

    private ServiceStatus Status() => new()
    {
        Lock = _lockStore.Current,
        TrustedUtcNow = _clock.UtcNow,
        SystemUtcNow = DateTimeOffset.UtcNow,
        BreakDelayMs = (long)LockStateStore.BreakDelay.TotalMilliseconds,
    };

    private static T Payload<T>(IpcRequest request) where T : class =>
        request.Payload?.Deserialize<T>() ?? throw new ArgumentException($"'{request.Type}' needs a payload");

    private static RuleSet ValidRules(RuleSet rules)
    {
        var domains = rules.BlockedDomains.Select(d => d.Trim()).ToList();
        if (domains.Any(d => d.Length == 0 || d.Any(char.IsWhiteSpace)))
            throw new ArgumentException("Blocked sites can't be blank or contain spaces.");
        return rules with { BlockedDomains = domains };
    }

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
