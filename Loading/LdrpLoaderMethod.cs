using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static CompactLoader.Interop.NativeMethods;
using static CompactLoader.RemoteExecution;

namespace CompactLoader;

// Private ABI verified against the Microsoft PDB and disassembly of this exact binary.
// Do not extend the allowlist by changing only a version number or function RVA.
internal static class LdrpLoaderMethod
{
    const string SupportedHash = "651AC9041AF8E6B5EBD331D53163244C0A27D36D1433F0F15CAC1F28C5F2E82C";
    const int InitializePath = 0x91c80, LoadDll = 0x2a630, Dereference = 0x225f0;

    internal static bool IsSupported => Environment.Is64BitProcess &&
        Environment.OSVersion.Version.Build == 26300 &&
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(Environment.SystemDirectory, "ntdll.dll")))) == SupportedHash;

    internal static string Load(Process process, SafeProcessHandle handle, string path)
    {
        if (!IsSupported)
            throw new NotSupportedException("LdrpLoader requires x64 Windows build 26300 with the verified ntdll.dll 10.0.26100.9549 binary. Other binaries and x86 are unsupported.");
        var encoded = Encoding.Unicode.GetBytes(path + '\0');
        if (encoded.Length > ushort.MaxValue)
            throw new ArgumentException("DLL path exceeds the UNICODE_STRING length limit.");
        process.Refresh();
        var module = process.Modules.Cast<ProcessModule>().Single(m =>
            string.Equals(m.ModuleName, "ntdll.dll", StringComparison.OrdinalIgnoreCase));
        var file = File.ReadAllBytes(module.FileName);
        if (Convert.ToHexString(SHA256.HashData(file)) != SupportedHash)
            throw new NotSupportedException("Target ntdll.dll does not match the verified LdrpLoader binary.");
        using var remote = new RemoteExecution(handle);
        // Check the mapped headers and entry bytes as well as the backing file.
        using (var reader = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(file, false)))
        {
            // The loader can change optional-header fields (including ImageBase).
            // Match the PE signature/COFF identity and mapped image size instead.
            var peOffset = BitConverter.ToInt32(file, 0x3c);
            if (module.ModuleMemorySize != reader.PEHeaders.PEHeader!.SizeOfImage ||
                !remote.Read(module.BaseAddress + peOffset, 24).SequenceEqual(file.Skip(peOffset).Take(24)))
                throw new NotSupportedException("Target ntdll headers differ from the verified binary.");
            foreach (var rva in new[] { InitializePath, LoadDll, Dereference })
                if (!remote.Read(module.BaseAddress + rva, 32).SequenceEqual(reader.GetSectionData(rva).GetContent(0, 32)))
                    throw new NotSupportedException("Target ntdll function bytes differ from the verified binary.");
        }
        // Block: function pointers 0..31, UNICODE_STRING 32..47,
        // LDRP_PATH_SEARCH_CONTEXT 64..191, entry 192, HMODULE 200,
        // NTSTATUS 208, completion 212, UTF-16 path 224...
        var data = remote.Allocate(checked(224 + encoded.Length));
        var block = new byte[224 + encoded.Length];
        PutPointer(block, 0, module.BaseAddress + InitializePath);
        PutPointer(block, 8, module.BaseAddress + LoadDll);
        PutPointer(block, 16, module.BaseAddress + Dereference);
        PutPointer(block, 24, Export(process, "ntdll.dll", "RtlReleasePath"));
        BitConverter.GetBytes((ushort)(encoded.Length - 2)).CopyTo(block, 32);
        BitConverter.GetBytes((ushort)encoded.Length).CopyTo(block, 34);
        PutPointer(block, 40, data + 224);
        BitConverter.GetBytes(0x103).CopyTo(block, 208);
        encoded.CopyTo(block, 224);
        remote.Write(data, block);
        var code = remote.Code(BuildStub());
        using var thread = CreateRemoteThread(handle, 0, 0, code, data, 0, out _);
        Check(!thread.IsInvalid, "CreateRemoteThread (LdrpLoader)");
        // Any failure before confirmed return leaves the scratch memory intact.
        try
        {
            var wait = WaitForSingleObject(thread, 15000);
            if (wait == 0x102)
                throw new TimeoutException("LdrpLoader timed out. Completion is unknown; code and data remain allocated. Do not retry.");
            Check(wait != 0xffffffff, "WaitForSingleObject (LdrpLoader)");
            if (wait != 0) throw new InvalidOperationException("Unexpected LdrpLoader wait result.");
            block = remote.Read(data, 224);
            if (BitConverter.ToInt32(block, 212) != 1)
                throw new InvalidOperationException("LdrpLoader did not return normally. Code and data remain allocated; restart the target before retrying.");
        }
        catch { remote.Retain(); throw; }
        var status = BitConverter.ToInt32(block, 208);
        if (status < 0 || status == 0x103)
            throw new InvalidOperationException($"LdrpLoadDll failed (NTSTATUS 0x{status:X8}).");
        var loaded = Pointer(block, 200);
        process.Refresh();
        if (loaded == 0 || Loader.FindModule(process, path) != loaded)
            throw new InvalidOperationException("LdrpLoader module handle was not confirmed by the target module list.");
        return "DLL found in the target module list. LdrpLoader load verified (experimental, pinned ntdll x64).";
    }

    static byte[] BuildStub()
    {
        var bytes = new List<byte>();
        void Emit(params byte[] value) => bytes.AddRange(value);
        int Jump(byte condition) { Emit(0x0f, condition, 0, 0, 0, 0); return bytes.Count - 4; }
        void Bind(int offset)
        {
            var displacement = BitConverter.GetBytes(bytes.Count - offset - 4);
            for (var i = 0; i < 4; i++) bytes[offset + i] = displacement[i];
        }
        Emit(0x53, 0x48,0x83,0xec,0x20, 0x48,0x89,0xcb); // rbx = block; aligned shadow space
        Emit(0x48,0x8b,0x4b,0x28, 0x31,0xd2, 0x4c,0x8d,0x43,0x40, 0xff,0x13); // initialize path
        Emit(0x48,0x8d,0x4b,0x20, 0x48,0x8d,0x53,0x40, 0x45,0x31,0xc0);
        Emit(0x4c,0x8d,0x8b,0xc0,0,0,0, 0xff,0x53,0x08); // LdrpLoadDll(name, context, 0, &entry)
        Emit(0x89,0x83,0xd0,0,0,0); // save NTSTATUS before cleanup
        Emit(0x80,0xbb,0xbc,0,0,0,0); // context.PathAllocated at +0x7c
        var noRelease = Jump(0x84);
        Emit(0x48,0x8b,0x4b,0x40, 0xff,0x53,0x18); // RtlReleasePath(context.Path)
        Bind(noRelease);
        Emit(0x83,0xbb,0xd0,0,0,0,0);
        var failed = Jump(0x88); // signed NTSTATUS < 0
        Emit(0x48,0x8b,0x8b,0xc0,0,0,0, 0x48,0x85,0xc9);
        var noEntry = Jump(0x84);
        Emit(0x48,0x8b,0x41,0x30, 0x48,0x89,0x83,0xc8,0,0,0, 0xff,0x53,0x10); // DllBase; dereference entry
        Bind(failed); Bind(noEntry);
        Emit(0xc7,0x83,0xd4,0,0,0,1,0,0,0, 0x48,0x83,0xc4,0x20, 0x5b,0xc3);
        return bytes.ToArray();
    }
}
