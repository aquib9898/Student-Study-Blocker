namespace StudyBlocker.Service.Packaging;

// Runs PackagedInstall.Prepare once the service has started (so a slow
// sc.exe or schtasks never holds up Windows' start timeout). Registered only
// when the service runs from the Microsoft Store package.
public sealed class PackagedInstallService(PackagedInstall install, IStateStore store, ILogger<PackagedInstallService> log)
    : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Run(() => install.Prepare(store, log), stoppingToken);
}
