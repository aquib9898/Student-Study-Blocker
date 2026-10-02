using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace StudyBlocker.Service;

// File-backed JSON storage under ProgramData, locked to SYSTEM and
// Administrators only. The daily account runs as Standard (see the plan's
// two-account strategy), so it has no filesystem access to read or edit
// state the lock depends on.
public sealed class ProtectedStore : IStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _root;

    public ProtectedStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
        LockDownAcl(_root);
    }

    public T? Read<T>(string fileName) where T : class
    {
        var path = Path.Combine(_root, fileName);
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream);
    }

    public void Write<T>(string fileName, T value)
    {
        var path = Path.Combine(_root, fileName);
        var tmpPath = path + ".tmp";
        using (var stream = File.Create(tmpPath))
        {
            JsonSerializer.Serialize(stream, value, JsonOptions);
        }
        File.Move(tmpPath, path, overwrite: true);
    }

    private static void LockDownAcl(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        // Fix the owner to Administrators rather than leaving it as whoever
        // happens to create the folder first. An owner always retains
        // WRITE_DAC (can re-grant itself access) regardless of the ACL
        // below, so if a non-admin process ever created this folder first,
        // it could later loosen its own lockout. SYSTEM and elevated admins
        // both have the rights needed to set this.
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));

        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        new DirectoryInfo(path).SetAccessControl(security);
    }
}
