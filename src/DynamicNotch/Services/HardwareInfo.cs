using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DynamicNotch.Services;

/// <summary>
/// The Hardware page's extra readings, beside SystemMonitor and StrainService: the processor's and the graphics card's
/// names, cores and threads (read once), and each second the load of every core, the clock, the memory held as cache
/// and the commit, the local address and the drives. Everything here is cheap Windows calls (no WMI, no driver), meant
/// to run off the UI thread.
/// </summary>
public sealed class HardwareInfo
{
    public sealed record Chip(string Cpu, string CpuShort, int Cores, int Threads, string? Gpu, string? GpuShort);
    public sealed record Live(double[] Cores, double? Ghz, double CacheGb, double CommitGb, double CommitLimitGb, string? LocalIp, IReadOnlyList<Drive> Drives);
    public sealed record Drive(string Name, string Label, double UsedGb, double TotalGb);

    Chip? _chip;
    long[] _idle = Array.Empty<long>(), _total = Array.Empty<long>();
    string? _ip;
    DateTime _ipAt, _drivesAt;
    IReadOnlyList<Drive> _drives = Array.Empty<Drive>();

    public Chip Names => _chip ??= ReadChip();

    // ───────────── once: the names ─────────────

    static Chip ReadChip()
    {
        string cpu = "";
        try { cpu = (Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", "") as string ?? "").Trim(); } catch { }
        int threads = Environment.ProcessorCount, cores = PhysicalCores() is > 0 and var c ? c : threads;
        var (gpu, _) = BiggestGpu();
        return new Chip(cpu, ShortCpu(cpu), cores, threads, gpu, gpu == null ? null : ShortGpu(gpu));
    }

    /// <summary>"AMD Ryzen 7 7735HS with Radeon Graphics" → "Ryzen 7 7735HS"; "12th Gen Intel(R) Core(TM) i7-12700H" → "Core i7-12700H".</summary>
    public static string ShortCpu(string name)
    {
        var s = name.Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", "");
        int cut = s.IndexOf(" with ", StringComparison.OrdinalIgnoreCase);
        if (cut > 0) s = s[..cut];
        cut = s.IndexOf(" CPU", StringComparison.OrdinalIgnoreCase);
        if (cut > 0) s = s[..cut];
        cut = s.IndexOf('@');
        if (cut > 0) s = s[..cut];
        s = System.Text.RegularExpressions.Regex.Replace(s, @"^\d+(st|nd|rd|th) Gen\s+", "");
        foreach (var brand in new[] { "AMD ", "Intel " }) if (s.StartsWith(brand, StringComparison.OrdinalIgnoreCase)) s = s[brand.Length..];
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+(\d+-Core Processor|Processor)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return System.Text.RegularExpressions.Regex.Replace(s, @"\s{2,}", " ").Trim();
    }

    /// <summary>"NVIDIA GeForce RTX 4060 Laptop GPU" → "RTX 4060 Laptop"; "AMD Radeon(TM) Graphics" → "Radeon Graphics".</summary>
    public static string ShortGpu(string name)
    {
        var s = name.Replace("(R)", "").Replace("(TM)", "");
        foreach (var brand in new[] { "NVIDIA GeForce ", "NVIDIA ", "AMD ", "Intel " }) if (s.StartsWith(brand, StringComparison.OrdinalIgnoreCase)) { s = s[brand.Length..]; break; }
        if (s.EndsWith(" GPU", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        return System.Text.RegularExpressions.Regex.Replace(s, @"\s{2,}", " ").Trim();
    }

    /// <summary>The graphics card with the most memory of its own (the one games use), from the display adapters' registry entries.</summary>
    static (string? Name, long Bytes) BiggestGpu()
    {
        string? best = null;
        long most = -1;
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return (null, 0);
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
            {
                using var k = cls.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") is not string desc || desc.Contains("Basic", StringComparison.OrdinalIgnoreCase) || desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || desc.Contains("Remote", StringComparison.OrdinalIgnoreCase)) continue;
                long mem = k.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l,
                    byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                    _ => k.GetValue("HardwareInformation.MemorySize") switch { int i => (uint)i, byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0), _ => 0 },
                };
                if (mem > most) { most = mem; best = desc.Trim(); }
            }
        }
        catch { }
        return (best, Math.Max(0, most));
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetLogicalProcessorInformation(IntPtr buffer, ref uint length);

    static int PhysicalCores()
    {
        try
        {
            uint len = 0;
            GetLogicalProcessorInformation(IntPtr.Zero, ref len);
            if (len == 0) return 0;
            var buf = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!GetLogicalProcessorInformation(buf, ref len)) return 0;
                int size = IntPtr.Size == 8 ? 32 : 24, n = 0;
                for (int off = 0; off + size <= len; off += size)
                    if (Marshal.ReadInt32(buf, off + IntPtr.Size) == 0) n++; // RelationProcessorCore
                return n;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return 0; }
    }

    // ───────────── every second ─────────────

    [StructLayout(LayoutKind.Sequential)]
    struct CorePerf { public long Idle, Kernel, User, Dpc, Interrupt; public uint InterruptCount; }
    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int infoClass, [Out] CorePerf[] info, int length, out int returned);

    [StructLayout(LayoutKind.Sequential)]
    struct PowerInfo { public uint Number, MaxMhz, CurrentMhz, MhzLimit, MaxIdleState, CurrentIdleState; }
    [DllImport("powrprof.dll")] static extern uint CallNtPowerInformation(int level, IntPtr input, uint inputLength, [Out] PowerInfo[] output, uint outputLength);

    [StructLayout(LayoutKind.Sequential)]
    struct PerfInfo
    {
        public uint cb;
        public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }
    [DllImport("kernel32.dll", EntryPoint = "K32GetPerformanceInfo")] static extern bool GetPerformanceInfo(ref PerfInfo info, uint size);

    public Live Sample()
    {
        int n = Environment.ProcessorCount;
        // each core's load since the last reading (idle time against the total; kernel time includes idle)
        var cores = new double[n];
        try
        {
            var perf = new CorePerf[n];
            if (NtQuerySystemInformation(8, perf, Marshal.SizeOf<CorePerf>() * n, out _) == 0)
            {
                bool first = _idle.Length != n;
                if (first) { _idle = new long[n]; _total = new long[n]; }
                for (int i = 0; i < n; i++)
                {
                    long total = perf[i].Kernel + perf[i].User, idle = perf[i].Idle;
                    long dt = total - _total[i], di = idle - _idle[i];
                    cores[i] = first || dt <= 0 ? 0 : Math.Clamp(1 - (double)di / dt, 0, 1);
                    _total[i] = total; _idle[i] = idle;
                }
            }
        }
        catch { }

        double? ghz = null;
        try
        {
            var p = new PowerInfo[n];
            if (CallNtPowerInformation(11, IntPtr.Zero, 0, p, (uint)(Marshal.SizeOf<PowerInfo>() * n)) == 0 && p.Any(x => x.CurrentMhz > 0))
                ghz = p.Where(x => x.CurrentMhz > 0).Average(x => (double)x.CurrentMhz) / 1000;
        }
        catch { }

        double cache = 0, commit = 0, limit = 0;
        try
        {
            var pi = new PerfInfo { cb = (uint)Marshal.SizeOf<PerfInfo>() };
            if (GetPerformanceInfo(ref pi, pi.cb))
            {
                double page = pi.PageSize.ToUInt64(), gb = 1024.0 * 1024 * 1024;
                cache = pi.SystemCache.ToUInt64() * page / gb;
                commit = pi.CommitTotal.ToUInt64() * page / gb;
                limit = pi.CommitLimit.ToUInt64() * page / gb;
            }
        }
        catch { }

        var now = DateTime.UtcNow;
        if ((now - _ipAt).TotalSeconds > 20) { _ip = LocalIp(); _ipAt = now; }
        if ((now - _drivesAt).TotalSeconds > 15) { _drives = ReadDrives(); _drivesAt = now; }
        return new Live(cores, ghz, cache, commit, limit, _ip, _drives);
    }

    /// <summary>This PC's address on the local network: the one of the connection that has a gateway (not a VPN's or a virtual switch's).</summary>
    static string? LocalIp()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                if (nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || nic.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)) continue;
                var props = nic.GetIPProperties();
                if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any))) continue;
                var v4 = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                if (v4 != null) return v4.Address.ToString();
            }
        }
        catch { }
        return null;
    }

    static IReadOnlyList<Drive> ReadDrives()
    {
        var list = new List<Drive>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady || d.TotalSize <= 0) continue;
                    double gb = 1024.0 * 1024 * 1024;
                    list.Add(new Drive(d.Name.TrimEnd('\\'), d.VolumeLabel, (d.TotalSize - d.TotalFreeSpace) / gb, d.TotalSize / gb));
                }
                catch { }
            }
        }
        catch { }
        return list;
    }
}
