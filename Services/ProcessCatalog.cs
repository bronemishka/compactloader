using System.ComponentModel;
using System.Diagnostics;
using static CompactLoader.Interop.NativeMethods;

namespace CompactLoader;

internal static class ProcessCatalog
{
    internal static List<Target> GetTargets()
    {
        var list = new List<Target>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id != Environment.ProcessId)
                        list.Add(new(p.Id, p.ProcessName, p.StartTime.ToUniversalTime().Ticks, ProcessArchitecture(p.Id), p.MainWindowHandle != IntPtr.Zero ? p.MainWindowTitle : ""));
                }
                catch (Win32Exception) { }
                catch (InvalidOperationException) { }
                catch (NotSupportedException) { }
            }
        }
        return list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id).ToList();
    }

    static string ProcessArchitecture(int id)
    {
        using var handle = OpenProcess(0x1000, false, id);
        if (handle.IsInvalid || !IsWow64Process2(handle, out var guest, out var host)) return "?";
        return (guest == 0 ? host : guest) switch { 0x014c => "x86", 0x8664 => "x64", 0xaa64 => "ARM64", _ => "?" };
    }
}
