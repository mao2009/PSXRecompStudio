using System.Globalization;
using System.Runtime.InteropServices;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core;

/// <summary>
/// Reads the parent of every process (Issue #827), so a stop can list a child's descendants before it kills the tree. Windows reads
/// the toolhelp process snapshot; Linux reads /proc; macOS lists the processes with libproc. This is the interop boundary for that query.
/// Other platforms report that it cannot be read.
/// </summary>
[Domain]
public static class ProcessTreeInterop
{
    /// <summary>The (process id, parent process id) of every process, or null when the platform cannot list them.</summary>
    public static IReadOnlyList<(int Pid, int Ppid)>? TryReadParentIds()
    {
        if (OperatingSystem.IsWindows()) return WindowsParentIds();
        if (OperatingSystem.IsMacOS()) return MacParentIds();
        return OperatingSystem.IsLinux() ? LinuxParentIds() : null;
    }

    private const string LibProc = "/usr/lib/libproc.dylib";
    private const uint PROC_ALL_PIDS = 1;
    private const int PROC_PIDTBSDINFO = 3;

    // struct proc_bsdinfo (sys/proc_info.h): pbi_flags, pbi_status, pbi_xstatus, pbi_pid, pbi_ppid, ... ; 136 bytes.
    private const int ProcBsdInfoSize = 136;
    private const int PbiPpidOffset = 16;

    [DllImport(LibProc)]
    private static extern int proc_listpids(uint type, uint typeInfo, nint buffer, int bufferSize);

    [DllImport(LibProc)]
    private static extern int proc_pidinfo(int pid, int flavor, ulong arg, nint buffer, int bufferSize);

    /// <summary>
    /// Lists the processes with libproc (Issue #820 regression: macOS used to report "cannot be listed", refusing every stop). A process that
    /// cannot be read (another user's, or one that ended while listed) is left out; its children are then not attributable, which the stop
    /// reports as an uncertainty rather than a refusal.
    /// </summary>
    private static List<(int Pid, int Ppid)>? MacParentIds()
    {
        var needed = proc_listpids(PROC_ALL_PIDS, 0, 0, 0);
        if (needed <= 0)
        {
            return null;
        }

        // A process may start between the two calls: room for a few more ids.
        var capacity = needed + 64 * sizeof(int);
        var pids = Marshal.AllocHGlobal(capacity);
        var info = Marshal.AllocHGlobal(ProcBsdInfoSize);
        try
        {
            var filled = proc_listpids(PROC_ALL_PIDS, 0, pids, capacity);
            if (filled <= 0)
            {
                return null;
            }

            var map = new List<(int Pid, int Ppid)>();
            for (var index = 0; index < filled / sizeof(int); index++)
            {
                var pid = Marshal.ReadInt32(pids, index * sizeof(int));
                if (pid <= 0)
                {
                    continue;
                }

                if (proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, info, ProcBsdInfoSize) < ProcBsdInfoSize)
                {
                    continue;
                }

                map.Add((pid, Marshal.ReadInt32(info, PbiPpidOffset)));
            }

            return map;
        }
        finally
        {
            Marshal.FreeHGlobal(info);
            Marshal.FreeHGlobal(pids);
        }
    }

    private static List<(int Pid, int Ppid)> LinuxParentIds()
    {
        var map = new List<(int Pid, int Ppid)>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var pid))
            {
                continue;
            }

            try
            {
                // The command name is in parentheses and may contain spaces; the fields after the last ')' are: state, parent id.
                var stat = File.ReadAllText(Path.Combine(directory, "stat"));
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                map.Add((pid, int.Parse(fields[1], CultureInfo.InvariantCulture)));
            }
            catch (Exception exception) when (exception is IOException or FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException)
            {
                // The process ended while it was being read.
            }
        }

        return map;
    }

    private const uint TH32CS_SNAPPROCESS = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    private static List<(int Pid, int Ppid)>? WindowsParentIds()
    {
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == -1)
        {
            return null;
        }

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(snapshot, ref entry))
            {
                return null;
            }

            var map = new List<(int Pid, int Ppid)>();
            do
            {
                map.Add(((int)entry.ProcessId, (int)entry.ParentProcessId));
            }
            while (Process32NextW(snapshot, ref entry));
            return map;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }
}
