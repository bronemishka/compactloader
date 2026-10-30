using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static CompactLoader.Interop.NativeMethods;

namespace CompactLoader;

internal static class LoadLibraryMethod
{
    static void Check(bool success, string operation)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), operation);
    }

    internal static string Load(Process process, SafeProcessHandle handle, string path)
    {
        var localAddress = GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryW");
        Check(localAddress != 0, "GetProcAddress");
        // GetProcAddress can resolve a forwarded export into KernelBase.
        Check(GetModuleHandleEx(0x00000004 | 0x00000002, localAddress, out var owner), "GetModuleHandleEx");
        var moduleName = new StringBuilder(32768);
        Check(GetModuleFileName(owner, moduleName, moduleName.Capacity) != 0, "GetModuleFileName");
        var ownerName = Path.GetFileName(moduleName.ToString());
        nint remoteBase = 0;
        foreach (ProcessModule m in process.Modules)
            if (string.Equals(m.ModuleName, ownerName, StringComparison.OrdinalIgnoreCase)) { remoteBase = m.BaseAddress; break; }
        if (remoteBase == 0) throw new InvalidOperationException("Loader system module was not found in the target process.");
        var remoteFunction = remoteBase + (localAddress - owner);
        var bytes = Encoding.Unicode.GetBytes(path + '\0');
        var memory = VirtualAllocEx(handle, 0, (nuint)bytes.Length, 0x3000, 0x04);
        Check(memory != 0, "VirtualAllocEx");
        var canFree = true;
        try
        {
            Check(WriteProcessMemory(handle, memory, bytes, (nuint)bytes.Length, out var written), "WriteProcessMemory");
            if (written != (nuint)bytes.Length) throw new IOException("The DLL path was only partially written.");
            using var thread = CreateRemoteThread(handle, 0, 0, remoteFunction, memory, 0, out _);
            Check(!thread.IsInvalid, "CreateRemoteThread");
            canFree = false;
            var wait = WaitForSingleObject(thread, 15000);
            if (wait == 0x102) throw new TimeoutException("Remote thread timed out after 15 seconds. Load state is unknown. The path buffer remains allocated because the thread may still read it. Do not retry yet.");
            Check(wait != 0xffffffff, "WaitForSingleObject");
            if (wait != 0) throw new InvalidOperationException("Unexpected wait result. The path buffer remains allocated in the target process.");
            canFree = true;
            // Do not treat DWORD thread exit code as a 64-bit HMODULE.
            process.Refresh();
            if (Loader.FindModule(process, path) == 0) throw new InvalidOperationException("Remote thread exited, but the DLL was not found in the module list. Check DLL dependencies and DllMain. The remote LoadLibrary error code is unavailable.");
            return "DLL found in the target module list. Library load verified.";
        }
        finally { if (canFree && !VirtualFreeEx(handle, memory, 0, 0x8000)) Debug.WriteLine("VirtualFreeEx failed: " + Marshal.GetLastWin32Error()); }
    }
}
