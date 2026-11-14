using System.ComponentModel;
using System.Runtime.InteropServices;
using StudyBlocker.Service.Policies;

namespace StudyBlocker.Service.AppBlocking;

// Opens the desktop app's blocked-app notice on the desktop where a program
// was just closed. The service runs in session 0, which has no screen, so it
// starts the notice as the person signed in at that desktop, with their own
// rights, the same way Windows does for a launch AppBlockPolicies stops.
public sealed class UserSessionNotice(string launcher) : IBlockedAppNotice
{
    private const uint CreateUnicodeEnvironment = 0x00000400;

    public void Show(int sessionId, string imagePath)
    {
        if (!WTSQueryUserToken((uint)sessionId, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var environment = IntPtr.Zero;
        try
        {
            if (!CreateEnvironmentBlock(out environment, token, false)) environment = IntPtr.Zero;
            var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>(), lpDesktop = @"winsta0\default" };
            var commandLine = $"\"{launcher}\" {AppBlockPolicies.BlockedSwitch} \"{imagePath}\"";
            if (!CreateProcessAsUser(token, launcher, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    environment == IntPtr.Zero ? 0 : CreateUnicodeEnvironment, environment,
                    Path.GetDirectoryName(launcher), ref startup, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);
        }
        finally
        {
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string? application, string commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags,
        IntPtr environment, string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
