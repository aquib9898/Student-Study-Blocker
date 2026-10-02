using Microsoft.Extensions.Logging.EventLog;
using StudyBlocker.Common;
using StudyBlocker.Service;
using StudyBlocker.Service.Classification;
using StudyBlocker.Service.Policies;

const string ServiceName = "SmartStudyBlockerService";

var storeRoot = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "SmartStudyBlocker");

// One copy at a time: two would each write browser policy from their own
// idea of the lock, and only one can own the pipe. This also stops the old
// `dotnet run` command from starting a second copy next to the installed
// service.
using var singleInstance = TryClaimSingleInstance();
if (singleInstance is null)
{
    Console.Error.WriteLine("Another copy of the Smart Study Distraction Blocker service is already running " +
        "(the installed Windows service, or one started in another window), or this one wasn't started as an administrator.");
    return 2;
}

ProtectedStore store;
try
{
    store = new ProtectedStore(storeRoot);
}
catch (UnauthorizedAccessException)
{
    Console.Error.WriteLine("The Smart Study Distraction Blocker service needs administrator rights. " +
        "It normally runs as the installed Windows service (see installer\\README.md).");
    return 3;
}

// The uninstaller's last step before removing the service: puts every
// browser policy the service took over back the way it was, then exits.
// Refused during a lock, like every other way of loosening one.
if (args.Contains("--restore-browser-policies"))
{
    if (new LockStateStore(store, new TrustedClock()).Current.IsLocked)
    {
        Console.Error.WriteLine("A lock is active, so the browser policies stay until it ends.");
        return 1;
    }
    foreach (var change in new PolicyWriter(new WindowsPolicyRegistry(), store).Apply(new LockState()).Applied)
        Console.WriteLine(change);
    return 0;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = ServiceName);
// Installed, the service has no console window: its log goes to the Windows
// Application event log, which on its own would keep only warnings.
builder.Services.Configure<EventLogSettings>(settings => settings.SourceName = ServiceName);
builder.Logging.AddFilter<EventLogLoggerProvider>(level => level >= LogLevel.Information);

builder.Services.AddSingleton<IStateStore>(store);
builder.Services.AddSingleton(new TrustedClock());
builder.Services.AddSingleton(new NetworkTimeSource());
builder.Services.AddSingleton<LockStateStore>();
builder.Services.AddSingleton<IPolicyRegistry, WindowsPolicyRegistry>();
builder.Services.AddSingleton<PolicyWriter>();
builder.Services.AddSingleton<ClassifierStore>();
builder.Services.AddHostedService<Worker>();

builder.Build().Run();
return 0;

static Mutex? TryClaimSingleInstance()
{
    try
    {
        var mutex = new Mutex(initiallyOwned: true, @"Global\SmartStudyBlockerService", out var createdNew);
        if (createdNew) return mutex;
        mutex.Dispose();
        return null;
    }
    catch (UnauthorizedAccessException)
    {
        // It exists but belongs to an account this copy can't open it as
        // (the installed service runs as SYSTEM), or this copy isn't allowed
        // to create one at all (not started as an administrator).
        return null;
    }
}
