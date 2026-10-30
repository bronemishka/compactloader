using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static CompactLoader.Interop.NativeMethods;

namespace CompactLoader;

// Owns scratch allocations only. A timed-out call keeps its memory until process exit.
internal sealed class RemoteExecution(SafeProcessHandle handle) : IDisposable
{
    readonly List<nint> allocations = new();
    internal bool Uncertain { get; private set; }

    internal static void Check(bool value, string operation)
    {
        if (!value) throw new Win32Exception(Marshal.GetLastWin32Error(), operation);
    }

    internal nint Allocate(int size)
    {
        var address = VirtualAllocEx(handle, 0, checked((nuint)size), 0x3000, 0x04);
        Check(address != 0, "VirtualAllocEx");
        allocations.Add(address);
        return address;
    }

    internal void Write(nint address, byte[] bytes)
    {
        Check(WriteProcessMemory(handle, address, bytes, (nuint)bytes.Length, out var count), "WriteProcessMemory");
        if (count != (nuint)bytes.Length) throw new IOException("Incomplete remote memory write.");
    }

    internal byte[] Read(nint address, int size)
    {
        var bytes = new byte[size];
        Check(ReadProcessMemory(handle, address, bytes, (nuint)size, out var count), "ReadProcessMemory");
        if (count != (nuint)size) throw new IOException("Incomplete remote memory read.");
        return bytes;
    }

    internal nint String(string value, bool unicode = false)
    {
        var bytes = (unicode ? Encoding.Unicode : Encoding.ASCII).GetBytes(value + '\0');
        var address = Allocate(bytes.Length);
        Write(address, bytes);
        return address;
    }

    internal nint Code(byte[] bytes)
    {
        var address = Allocate(bytes.Length);
        Write(address, bytes);
        Protect(address, bytes.Length, 0x20);
        Check(FlushInstructionCache(handle, address, (nuint)bytes.Length), "FlushInstructionCache");
        return address;
    }

    internal void Protect(nint address, int size, uint protection) =>
        Check(VirtualProtectEx(handle, address, (nuint)size, protection, out _), "VirtualProtectEx");

    internal static void PutPointer(byte[] bytes, int offset, nint value) =>
        (Environment.Is64BitProcess ? BitConverter.GetBytes(value.ToInt64()) : BitConverter.GetBytes(value.ToInt32())).CopyTo(bytes, offset);

    internal static nint Pointer(byte[] bytes, int offset) => Environment.Is64BitProcess
        ? (nint)BitConverter.ToInt64(bytes, offset) : (nint)BitConverter.ToInt32(bytes, offset);

    // Resolves forwarded system exports through their actual owning module.
    internal static nint Export(Process process, string module, string name)
    {
        var address = GetProcAddress(GetModuleHandle(module), name);
        Check(address != 0, "GetProcAddress (" + name + ")");
        Check(GetModuleHandleEx(6, address, out var owner), "GetModuleHandleEx");
        var filename = new StringBuilder(32768);
        Check(GetModuleFileName(owner, filename, filename.Capacity) != 0, "GetModuleFileName");
        var basename = Path.GetFileName(filename.ToString());
        process.Refresh();
        foreach (ProcessModule candidate in process.Modules)
            if (string.Equals(candidate.ModuleName, basename, StringComparison.OrdinalIgnoreCase))
                return candidate.BaseAddress + (address - owner);
        throw new InvalidOperationException("Export owner is absent from target: " + basename);
    }

    nint callCode, callData;
    internal nint Call(nint function, params nint[] arguments)
    {
        if (Uncertain) throw new InvalidOperationException("A previous remote call has an unknown completion state.");
        if (arguments.Length > 4) throw new ArgumentException("At most four native arguments are supported.");
        if (callCode == 0)
        {
            // x86 restores ESP explicitly, supporting both stdcall and cdecl functions.
            callCode = Code(Environment.Is64BitProcess
                ? new byte[] { 0x53, 0x48,0x83,0xec,0x20, 0x48,0x89,0xcb,
                    0x48,0x8b,0x4b,0x08, 0x48,0x8b,0x53,0x10,
                    0x4c,0x8b,0x43,0x18, 0x4c,0x8b,0x4b,0x20,
                    0xff,0x13, 0x48,0x89,0x43,0x28, 0xc7,0x43,0x30,1,0,0,0,
                    0x48,0x83,0xc4,0x20, 0x5b,0xc3 }
                : new byte[] { 0x55,0x8b,0xec,0x53, 0x8b,0x5d,0x08,
                    0xff,0x73,0x20, 0xff,0x73,0x18, 0xff,0x73,0x10, 0xff,0x73,0x08,
                    0xff,0x13, 0x89,0x43,0x28, 0xc7,0x43,0x30,1,0,0,0,
                    0x8d,0x65,0xfc,0x5b,0x5d,0xc2,0x04,0x00 });
            callData = Allocate(64);
        }
        var data = new byte[64];
        PutPointer(data, 0, function);
        for (var i = 0; i < arguments.Length; i++) PutPointer(data, 8 + i * 8, arguments[i]);
        Write(callData, data);
        using var thread = CreateRemoteThread(handle, 0, 0, callCode, callData, 0, out _);
        Check(!thread.IsInvalid, "CreateRemoteThread");
        Uncertain = true;
        var result = WaitForSingleObject(thread, 15000);
        if (result == 0x102) throw new TimeoutException("Remote call timed out. Completion is unknown; allocations remain in the process. Do not retry.");
        Check(result != 0xffffffff, "WaitForSingleObject");
        if (result != 0) throw new InvalidOperationException("Unexpected remote thread wait result.");
        Uncertain = false;
        data = Read(callData, 64);
        if (BitConverter.ToInt32(data, 48) != 1) throw new InvalidOperationException("Remote function did not return normally.");
        return Pointer(data, 40);
    }

    internal void Retain() => allocations.Clear();

    public void Dispose()
    {
        if (Uncertain) return;
        foreach (var address in allocations.AsEnumerable().Reverse())
            if (!VirtualFreeEx(handle, address, 0, 0x8000)) Debug.WriteLine("VirtualFreeEx failed: " + Marshal.GetLastWin32Error());
        allocations.Clear();
    }
}
