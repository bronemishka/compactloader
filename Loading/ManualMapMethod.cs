using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection.PortableExecutable;
using Microsoft.Win32.SafeHandles;
using static CompactLoader.Interop.NativeMethods;
using static CompactLoader.RemoteExecution;

namespace CompactLoader;

internal static class ManualMapMethod
{
    // Manual mappings are not in the loader's module list; track them for this session.
    static readonly ConcurrentDictionary<(int Pid, long Started, string Path), nint> mapped = new();

    internal static string Load(Process process, SafeProcessHandle handle, string path)
    {
        var key = (process.Id, process.StartTime.ToUniversalTime().Ticks, path.ToUpperInvariant());
        if (mapped.TryGetValue(key, out var previous))
            return $"DLL was manually mapped in this session at 0x{previous:X}. Duplicate load skipped.";
        var image = new PortableImage(path); // Reject unsupported images before touching the target.
        var preferred = unchecked((nint)(long)image.Header.ImageBase);
        var address = VirtualAllocEx(handle, preferred, (nuint)image.Bytes.Length, 0x3000, 0x04);
        if (address == 0) address = VirtualAllocEx(handle, 0, (nuint)image.Bytes.Length, 0x3000, 0x04);
        Check(address != 0, "VirtualAllocEx (PE image)");
        using var remote = new RemoteExecution(handle);
        bool keepImage = false, functionTableRegistered = false;
        nint functionTable = 0;
        try
        {
            image.Relocate(address);
            ResolveImports(process, remote, image);
            remote.Write(address, image.Bytes);
            // Default gaps and headers to read-only; apply the section access flags afterward.
            remote.Protect(address, image.Bytes.Length, 0x02);
            foreach (var section in image.Sections)
            {
                var size = Math.Max(section.VirtualSize, section.SizeOfRawData);
                if (size == 0) continue;
                var flags = section.SectionCharacteristics;
                var execute = (flags & SectionCharacteristics.MemExecute) != 0;
                var read = (flags & SectionCharacteristics.MemRead) != 0;
                var write = (flags & SectionCharacteristics.MemWrite) != 0;
                uint protection = execute ? (write ? 0x40u : read ? 0x20u : 0x10u) : (write ? 0x04u : read ? 0x02u : 0x01u);
                remote.Protect(address + section.VirtualAddress, size, protection);
            }
            Check(FlushInstructionCache(handle, address, (nuint)image.Bytes.Length), "FlushInstructionCache (PE image)");
            var exception = image.Header.ExceptionTableDirectory;
            if (image.Is64 && exception.Size != 0)
            {
                image.Range(exception.RelativeVirtualAddress, exception.Size);
                if (exception.Size % 12 != 0) throw new BadImageFormatException("Invalid x64 runtime function table size.");
                functionTable = address + exception.RelativeVirtualAddress;
                if (remote.Call(Export(process, "kernel32.dll", "RtlAddFunctionTable"), functionTable, exception.Size / 12, address) == 0)
                    throw new InvalidOperationException("RtlAddFunctionTable rejected the mapped image.");
                functionTableRegistered = true;
            }
            if (image.Header.AddressOfEntryPoint != 0)
            {
                // Once initialization begins, DLL code can publish pointers or start threads.
                // Never unmap it automatically after an initialization failure or timeout.
                keepImage = true;
                if (remote.Call(address + image.Header.AddressOfEntryPoint, address, 1, 0) == 0)
                    throw new InvalidOperationException($"DllMain returned FALSE. Image remains at 0x{address:X}; restart the target before retrying.");
            }
            keepImage = true;
            mapped[key] = address;
            return $"Manual Map initialized the image at 0x{address:X}. The image is not registered in the loader module list; unload is not supported.";
        }
        catch (Exception ex)
        {
            if (remote.Uncertain || keepImage)
                throw new InvalidOperationException($"Manual Map did not complete cleanly. Image retained at 0x{address:X}; do not retry before restarting the target. {ex.Message}", ex);
            throw;
        }
        finally
        {
            if (!keepImage && !remote.Uncertain)
            {
                if (functionTableRegistered)
                {
                    // Do not unmap memory while the unwind table still references it.
                    try { keepImage = remote.Call(Export(process, "kernel32.dll", "RtlDeleteFunctionTable"), functionTable) == 0; }
                    catch { keepImage = true; }
                }
                if (!keepImage && !VirtualFreeEx(handle, address, 0, 0x8000)) Debug.WriteLine("VirtualFreeEx (PE image) failed.");
            }
        }
    }

    static void ResolveImports(Process process, RemoteExecution remote, PortableImage image)
    {
        var directory = image.Header.ImportTableDirectory;
        if (directory.Size == 0) return;
        image.Range(directory.RelativeVirtualAddress, directory.Size);
        var end = checked(directory.RelativeVirtualAddress + directory.Size);
        var loadLibrary = Export(process, "kernel32.dll", "LoadLibraryW");
        var getProcAddress = Export(process, "kernel32.dll", "GetProcAddress");
        bool terminated = false;
        for (var descriptor = directory.RelativeVirtualAddress; descriptor <= end - 20; descriptor += 20)
        {
            var lookup = image.U32(descriptor);
            var name = image.U32(descriptor + 12);
            var iat = image.U32(descriptor + 16);
            if (lookup == 0 && name == 0 && iat == 0) { terminated = true; break; }
            var dependency = image.Name(checked((int)name));
            var module = remote.Call(loadLibrary, remote.String(dependency, true));
            if (module == 0) throw new InvalidOperationException("Unable to load dependency: " + dependency);
            // Dependencies stay referenced for the lifetime of this mapping.
            var source = checked((int)(lookup == 0 ? iat : lookup));
            var destination = checked((int)iat);
            for (var index = 0; ; index += image.Is64 ? 8 : 4)
            {
                var entry = image.Pointer(checked(source + index));
                if (entry == 0) break;
                var ordinalFlag = image.Is64 ? 0x8000000000000000ul : 0x80000000ul;
                var symbol = (entry & ordinalFlag) != 0
                    ? (nint)(entry & 0xffff)
                    : remote.String(image.Name(checked((int)entry + 2)));
                var address = remote.Call(getProcAddress, module, symbol);
                if (address == 0) throw new InvalidOperationException("Unresolved import in " + dependency);
                image.SetPointer(checked(destination + index), unchecked((ulong)address.ToInt64()));
            }
        }
        if (!terminated) throw new BadImageFormatException("PE import descriptors have no terminator.");
    }
}
