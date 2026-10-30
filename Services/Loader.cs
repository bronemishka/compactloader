using static CompactLoader.Interop.NativeMethods;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Reflection.PortableExecutable;
using Microsoft.Win32.SafeHandles;
using System.IO;

namespace CompactLoader;

internal static class Loader
{
    static void Check(bool success, string operation) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), operation); }
    static ushort Machine(SafeProcessHandle process)
    {
        Check(IsWow64Process2(process, out var guest, out var host), "IsWow64Process2"); return guest == 0 ? host : guest;
    }
    internal static string Load(Target target, string filename, LoadMethod method = LoadMethod.LoadLibraryW)
    {
        if (!Enum.IsDefined(method)) throw new ArgumentOutOfRangeException(nameof(method));
        var path = Path.GetFullPath(filename);
        using (var stream = File.OpenRead(path))
        using (var pe = new PEReader(stream))
        {
            var headers = pe.PEHeaders;
            if (headers.PEHeader is null || (headers.CoffHeader.Characteristics & Characteristics.Dll) == 0) throw new InvalidOperationException("The selected file is not a PE DLL.");
            if (headers.CorHeader is not null) throw new InvalidOperationException("Managed .NET DLLs require CLR hosting. Select a native DLL.");
            var expected = Environment.Is64BitProcess ? 0x8664 : 0x014c;
            if ((int)headers.CoffHeader.Machine != expected) throw new InvalidOperationException("DLL architecture does not match the loader.");
        }
        using var process = Process.GetProcessById(target.Id);
        if (process.StartTime.ToUniversalTime().Ticks != target.Started) throw new InvalidOperationException("The target process has changed. Refresh the process list.");
        using var handle = OpenProcess(0x0002 | 0x0008 | 0x0010 | 0x0020 | 0x0400, false, target.Id);
        Check(!handle.IsInvalid, "OpenProcess");
        if (Machine(handle) != (Environment.Is64BitProcess ? 0x8664 : 0x014c)) throw new InvalidOperationException("Process, DLL and loader architectures must match (x64 or x86). ARM64 is not supported.");
        if (FindModule(process, path) != 0) return "DLL is already loaded in the target process. Duplicate load skipped.";
        if (method == LoadMethod.ThreadHijacking) return ThreadHijackMethod.Load(process, handle, path);
        if (method == LoadMethod.ManualMap) return ManualMapMethod.Load(process, handle, path);
        if (method == LoadMethod.LdrLoadDll)
        {
            NativeLoaderStub.Load(process, handle, path);
            process.Refresh();
            if (FindModule(process, path) == 0)
                throw new InvalidOperationException("LdrLoadDll returned a module handle, but the DLL was not found in the target module list.");
            return "DLL found in the target module list. LdrLoadDll load verified.";
        }
        return LoadLibraryMethod.Load(process, handle, path);
    }

    internal static nint FindModule(Process process, string path)
    {
        foreach (ProcessModule module in process.Modules)
            if (string.Equals(Path.GetFullPath(module.FileName), path, StringComparison.OrdinalIgnoreCase)) return module.BaseAddress;
        return 0;
    }
}
