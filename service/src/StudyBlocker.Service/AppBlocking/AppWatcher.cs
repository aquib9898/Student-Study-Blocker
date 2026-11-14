using StudyBlocker.Common;
using StudyBlocker.Service.Policies;

namespace StudyBlocker.Service.AppBlocking;

// A running program, as much as the watcher needs of it.
public sealed record RunningProcess(int Id, string Exe, int SessionId);

// Windows' process list, behind an interface so the tests can fake it.
public interface IProcessTable
{
    IReadOnlyList<RunningProcess> List();

    // The program's file on disk; null when it can't be read (it ended, or
    // it's protected).
    string? ImagePath(int processId);

    // The name the program was built under (its version info's
    // OriginalFilename), which renaming the file doesn't change; null if it
    // has none.
    string? OriginalName(string imagePath);

    bool Kill(int processId);
}

// Tells the person at a session's desktop that a program was closed.
public interface IBlockedAppNotice
{
    void Show(int sessionId, string imagePath);
}

// The running half of the app blocker (the user's request, 2026-11-09).
// AppBlockPolicies stops most launches before they happen; this ends what
// that can't catch: a program that was already open when its block began
// (a lock starting, a break ending), a Store app, and a renamed copy, which
// is recognised by the name it was built under.
public sealed class AppWatcher
{
    // One notice per program and desktop this often at most, so a program
    // that keeps relaunching itself doesn't bury the screen in them.
    private static readonly TimeSpan NoticeInterval = TimeSpan.FromSeconds(60);

    private readonly IProcessTable _processes;
    private readonly IBlockedAppNotice? _notice;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<(string Exe, int Session), DateTimeOffset> _lastNotice = new();

    // What each process was found to be, by id and name (so a reused id is a
    // fresh look), and only for processes still running.
    private readonly Dictionary<(int Id, string Exe), (string? Path, string? Original)> _seen = new();

    public AppWatcher(IProcessTable processes, IBlockedAppNotice? notice = null, Func<DateTimeOffset>? now = null)
    {
        _processes = processes;
        _notice = notice;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    // Ends every running copy of a program `enforced` blocks. Returns what
    // it ended, for the log.
    public IReadOnlyList<string> Sweep(LockState enforced)
    {
        var blocked = AppBlockPolicies.Enforced(enforced);
        if (blocked.Count == 0)
        {
            _seen.Clear();
            return [];
        }

        var names = new HashSet<string>(blocked.Select(app => app.Exe), StringComparer.OrdinalIgnoreCase);
        var running = _processes.List();
        var alive = new HashSet<(int, string)>();
        var ended = new List<string>();
        foreach (var process in running)
        {
            // Session 0 is services, never a person's program; a protected
            // name is Windows' or this blocker's own, whatever it was built as.
            if (process.SessionId == 0 || BlockedApp.IsProtected(process.Exe)) continue;
            var key = (process.Id, process.Exe.ToLowerInvariant());
            alive.Add(key);

            if (!_seen.TryGetValue(key, out var found))
            {
                var path = _processes.ImagePath(process.Id);
                found = (path, path is null ? null : _processes.OriginalName(path));
                _seen[key] = found;
            }

            var match = names.Contains(process.Exe) ? process.Exe
                : found.Original is { } original && names.Contains(original) ? original
                : null;
            if (match is null || !_processes.Kill(process.Id)) continue;

            ended.Add($"{process.Exe} (process {process.Id}) ended: {match} is blocked");
            Notify(process, match, found.Path);
        }

        foreach (var gone in _seen.Keys.Where(key => !alive.Contains(key)).ToList()) _seen.Remove(gone);
        return ended;
    }

    private void Notify(RunningProcess process, string blockedExe, string? path)
    {
        if (_notice is null || path is null) return;
        var key = (blockedExe.ToLowerInvariant(), process.SessionId);
        var now = _now();
        if (_lastNotice.TryGetValue(key, out var last) && now - last < NoticeInterval) return;
        _lastNotice[key] = now;
        try
        {
            _notice.Show(process.SessionId, path);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Nobody signed in at that desktop any more, or it can't be
            // reached: the program is closed all the same.
        }
    }
}
