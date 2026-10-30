using static CompactLoader.Interop.NativeMethods;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CompactLoader;

internal static class NativeLoaderStub
{
    // The parameter block has fixed offsets on both architectures. Only pointer fields differ.
    internal static void Load(Process process, SafeProcessHandle handle, string path)
    {
        var encodedPath = Encoding.Unicode.GetBytes(path + '\0');
        if (encodedPath.Length > ushort.MaxValue)
            throw new ArgumentException("DLL path exceeds the UNICODE_STRING length limit.");
        var function = RemoteExport(process, "ntdll.dll", "LdrLoadDll");
        byte[] code = Environment.Is64BitProcess
            ? new byte[] {
                0x53,                         // push rbx
                0x48, 0x83, 0xec, 0x20,       // sub rsp, 32 (shadow space; stack aligned)
                0x48, 0x89, 0xcb,             // mov rbx, rcx
                0x31, 0xc9, 0x31, 0xd2,       // xor ecx, ecx; xor edx, edx
                0x4c, 0x8d, 0x43, 0x10,       // lea r8, [rbx + 16]
                0x4c, 0x8d, 0x4b, 0x20,       // lea r9, [rbx + 32]
                0xff, 0x13,                   // call [rbx]
                0x89, 0x43, 0x28,             // mov [rbx + 40], eax
                0x48, 0x83, 0xc4, 0x20,       // add rsp, 32
                0x5b, 0xc3                    // pop rbx; ret
            }
            : new byte[] {
                0x53,                         // push ebx
                0x8b, 0x5c, 0x24, 0x08,       // mov ebx, [esp + 8]
                0x8d, 0x43, 0x20, 0x50,       // lea eax, [ebx + 32]; push eax
                0x8d, 0x43, 0x10, 0x50,       // lea eax, [ebx + 16]; push eax
                0x6a, 0x00, 0x6a, 0x00,       // push 0; push 0
                0xff, 0x13,                   // call [ebx] (NTAPI / stdcall)
                0x89, 0x43, 0x28,             // mov [ebx + 40], eax
                0x5b, 0xc2, 0x04, 0x00        // pop ebx; ret 4
            };
        nint data = 0, executable = 0;
        bool completed = true;
        try
        {
            data = VirtualAllocEx(handle, 0, (nuint)(48 + encodedPath.Length), 0x3000, 0x04);
            Check(data != 0, "VirtualAllocEx (stub data)");
            executable = VirtualAllocEx(handle, 0, (nuint)code.Length, 0x3000, 0x04);
            Check(executable != 0, "VirtualAllocEx (stub code)");
            var block = new byte[48 + encodedPath.Length];
            PutPointer(block, 0, function);
            BitConverter.GetBytes((ushort)(encodedPath.Length - 2)).CopyTo(block, 16);
            BitConverter.GetBytes((ushort)encodedPath.Length).CopyTo(block, 18);
            PutPointer(block, Environment.Is64BitProcess ? 24 : 20, data + 48);
            // Pending sentinel prevents an unexecuted stub from appearing successful.
            BitConverter.GetBytes(unchecked((int)0x00000103)).CopyTo(block, 40);
            encodedPath.CopyTo(block, 48);
            Write(handle, data, block);
            Write(handle, executable, code);
            Check(VirtualProtectEx(handle, executable, (nuint)code.Length, 0x20, out _), "VirtualProtectEx (execute/read)");
            Check(FlushInstructionCache(handle, executable, (nuint)code.Length), "FlushInstructionCache");
            using var thread = CreateRemoteThread(handle, 0, 0, executable, data, 0, out _);
            Check(!thread.IsInvalid, "CreateRemoteThread (LdrLoadDll)");
            completed = false;
            var wait = WaitForSingleObject(thread, 15000);
            if (wait == 0x102)
                throw new TimeoutException("LdrLoadDll stub timed out after 15 seconds. Load state is unknown. Stub code and data remain allocated because the thread may still use them. Do not retry yet.");
            Check(wait != 0xffffffff, "WaitForSingleObject");
            if (wait != 0) throw new InvalidOperationException("Unexpected wait result. Stub allocations remain in the target process.");
            completed = true;
            var result = new byte[48];
            Check(ReadProcessMemory(handle, data, result, (nuint)result.Length, out var read), "ReadProcessMemory (stub result)");
            if (read != (nuint)result.Length) throw new IOException("Incomplete LdrLoadDll result block.");
            var status = BitConverter.ToInt32(result, 40);
            var module = Environment.Is64BitProcess ? BitConverter.ToInt64(result, 32) : BitConverter.ToUInt32(result, 32);
            if (status < 0)
                throw new Win32Exception(unchecked((int)RtlNtStatusToDosError(status)), $"LdrLoadDll failed (NTSTATUS 0x{status:X8})");
            if (status == 0x103 || module == 0)
                throw new InvalidOperationException($"LdrLoadDll stub returned no module handle (NTSTATUS 0x{status:X8}).");
        }
        finally
        {
            if (completed)
            {
                if (executable != 0 && !VirtualFreeEx(handle, executable, 0, 0x8000)) Debug.WriteLine("VirtualFreeEx (stub code) failed: " + Marshal.GetLastWin32Error());
                if (data != 0 && !VirtualFreeEx(handle, data, 0, 0x8000)) Debug.WriteLine("VirtualFreeEx (stub data) failed: " + Marshal.GetLastWin32Error());
            }
        }
    }

    static nint RemoteExport(Process process, string module, string export)
    {
        var owner = GetModuleHandle(module);
        Check(owner != 0, "GetModuleHandle (" + module + ")");
        var address = GetProcAddress(owner, export);
        Check(address != 0, "GetProcAddress (" + export + ")");
        foreach (ProcessModule candidate in process.Modules)
            if (string.Equals(candidate.ModuleName, module, StringComparison.OrdinalIgnoreCase))
                return candidate.BaseAddress + (address - owner);
        throw new InvalidOperationException(module + " was not found in the target process.");
    }
    static void PutPointer(byte[] bytes, int offset, nint pointer)
    {
        var encoded = Environment.Is64BitProcess ? BitConverter.GetBytes(pointer.ToInt64()) : BitConverter.GetBytes(pointer.ToInt32());
        encoded.CopyTo(bytes, offset);
    }
    static void Write(SafeProcessHandle handle, nint address, byte[] bytes)
    {
        Check(WriteProcessMemory(handle, address, bytes, (nuint)bytes.Length, out var written), "WriteProcessMemory (stub)");
        if (written != (nuint)bytes.Length) throw new IOException("Incomplete stub write.");
    }
    static void Check(bool value, string operation)
    {
        if (!value) throw new Win32Exception(Marshal.GetLastWin32Error(), operation);
    }
}
