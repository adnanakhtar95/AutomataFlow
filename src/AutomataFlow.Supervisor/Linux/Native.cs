using System.Runtime.InteropServices;

namespace AutomataFlow.Supervisor.Linux;

/// <summary>The few libc calls the supervisor needs. Linux only; every caller checks the result and fails closed.</summary>
internal static class Native
{
    public const int SIGKILL = 9;
    public const int SIGTERM = 15;
    public const int WNOHANG = 1;
    public const int ECHILD = 10;
    public const int ESRCH = 3;
    public const int PR_SET_DUMPABLE = 4;

    [DllImport("libc", SetLastError = true)]
    public static extern int socket(int domain, int type, int protocol);

    [DllImport("libc", SetLastError = true)]
    public static extern nint sendto(int fd, byte[] buffer, nint length, int flags, byte[] address, int addressLength);

    [DllImport("libc", SetLastError = true)]
    public static extern nint recv(int fd, byte[] buffer, nint length, int flags);

    [DllImport("libc", SetLastError = true)]
    public static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    public static extern int kill(int pid, int signal);

    [DllImport("libc", SetLastError = true)]
    public static extern int waitpid(int pid, out int status, int options);

    [DllImport("libc", SetLastError = true)]
    public static extern int prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);

    [DllImport("libc", SetLastError = true)]
    public static extern int getpid();

    [DllImport("libc", SetLastError = true)]
    public static extern nint syscall(nint number, nint arg1, nint arg2, nint arg3);
}
