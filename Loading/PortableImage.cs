using System.IO;
using System.Reflection.PortableExecutable;
using System.Text;

namespace CompactLoader;

// Parses and validates the subset of PE images supported by the manual mapper.
internal sealed class PortableImage
{
    internal byte[] Bytes { get; }
    internal PEHeader Header { get; }
    internal SectionHeader[] Sections { get; }
    internal bool Is64 { get; }

    internal PortableImage(string path)
    {
        var file = File.ReadAllBytes(path);
        using var reader = new PEReader(new MemoryStream(file, false));
        Header = reader.PEHeaders.PEHeader ?? throw new BadImageFormatException("Missing PE optional header.");
        Is64 = Header.Magic == PEMagic.PE32Plus;
        Sections = reader.PEHeaders.SectionHeaders.ToArray();
        if (Header.SizeOfImage <= 0 || Header.SizeOfImage > 256 * 1024 * 1024 ||
            Header.SectionAlignment < 4096 || Header.SizeOfImage % 4096 != 0)
            throw new NotSupportedException("Manual Map requires a page-aligned PE image no larger than 256 MiB.");
        if (Header.ThreadLocalStorageTableDirectory.Size != 0)
            throw new NotSupportedException("Manual Map does not support static TLS or TLS callbacks. Use a native loader method for this DLL.");
        if (Header.DelayImportTableDirectory.Size != 0)
            throw new NotSupportedException("Manual Map does not support delay-load imports.");
        Bytes = new byte[Header.SizeOfImage];
        Range(0, Header.SizeOfHeaders);
        if (Header.SizeOfHeaders > file.Length) throw new BadImageFormatException("Truncated PE headers.");
        file.AsSpan(0, Header.SizeOfHeaders).CopyTo(Bytes);
        var ranges = new List<(int Start, int End)> { (0, Header.SizeOfHeaders) };
        foreach (var section in Sections)
        {
            var size = Math.Max(section.VirtualSize, section.SizeOfRawData);
            Range(section.VirtualAddress, size);
            if (section.VirtualAddress % 4096 != 0 ||
                ranges.Any(r => section.VirtualAddress < r.End && (long)section.VirtualAddress + size > r.Start))
                throw new BadImageFormatException("Overlapping or unaligned PE sections.");
            ranges.Add((section.VirtualAddress, checked(section.VirtualAddress + size)));
            if (section.PointerToRawData < 0 || section.SizeOfRawData < 0 ||
                (long)section.PointerToRawData + section.SizeOfRawData > file.Length)
                throw new BadImageFormatException("Truncated PE section.");
            file.AsSpan(section.PointerToRawData, section.SizeOfRawData).CopyTo(Bytes.AsSpan(section.VirtualAddress));
        }
        if (Header.AddressOfEntryPoint != 0 && !Sections.Any(s =>
            (s.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0 &&
            Header.AddressOfEntryPoint >= s.VirtualAddress &&
            (long)Header.AddressOfEntryPoint < (long)s.VirtualAddress + Math.Max(s.VirtualSize, s.SizeOfRawData)))
            throw new BadImageFormatException("PE entry point is outside executable sections.");
        var config = Header.LoadConfigTableDirectory;
        if (config.Size != 0)
        {
            Range(config.RelativeVirtualAddress, config.Size);
            var flagsOffset = Is64 ? 144 : 88;
            if (config.Size >= flagsOffset + 4 && U32(config.RelativeVirtualAddress + flagsOffset) != 0)
                throw new NotSupportedException("Manual Map does not support Guard CF/XFG load configuration.");
            if (!Is64 && config.Size >= 72 && U32(config.RelativeVirtualAddress + 68) != 0)
                throw new NotSupportedException("Manual Map does not register x86 SafeSEH tables.");
        }
    }

    internal void Range(int offset, int count)
    {
        if (offset < 0 || count < 0 || (long)offset + count > Bytes.Length)
            throw new BadImageFormatException("PE data is outside SizeOfImage.");
    }
    internal uint U32(int offset) { Range(offset, 4); return BitConverter.ToUInt32(Bytes, offset); }
    internal ulong Pointer(int offset)
    {
        Range(offset, Is64 ? 8 : 4);
        return Is64 ? BitConverter.ToUInt64(Bytes, offset) : BitConverter.ToUInt32(Bytes, offset);
    }
    internal void SetPointer(int offset, ulong value)
    {
        Range(offset, Is64 ? 8 : 4);
        (Is64 ? BitConverter.GetBytes(value) : BitConverter.GetBytes(unchecked((uint)value))).CopyTo(Bytes, offset);
    }
    internal string Name(int offset)
    {
        Range(offset, 1);
        var end = Array.IndexOf(Bytes, (byte)0, offset, Math.Min(32768, Bytes.Length - offset));
        if (end < 0 || end == offset) throw new BadImageFormatException("Invalid PE import name.");
        return Encoding.ASCII.GetString(Bytes, offset, end - offset);
    }
    internal void Relocate(nint address)
    {
        var actual = unchecked((ulong)address.ToInt64());
        if (!Is64) actual = unchecked((uint)address.ToInt32());
        var delta = unchecked(actual - Header.ImageBase);
        if (delta == 0) return;
        var directory = Header.BaseRelocationTableDirectory;
        if (directory.Size == 0) throw new NotSupportedException("The image has no base relocations and its preferred base is unavailable.");
        Range(directory.RelativeVirtualAddress, directory.Size);
        var end = checked(directory.RelativeVirtualAddress + directory.Size);
        for (var cursor = directory.RelativeVirtualAddress; cursor < end;)
        {
            if (end - cursor < 8) throw new BadImageFormatException("Truncated relocation block.");
            var page = U32(cursor);
            var size = U32(cursor + 4);
            if (size < 8 || size > end - cursor || size % 2 != 0) throw new BadImageFormatException("Invalid relocation block size.");
            for (var entry = cursor + 8; entry < cursor + size; entry += 2)
            {
                var encoded = BitConverter.ToUInt16(Bytes, entry);
                var type = encoded >> 12;
                if (type == 0) continue;
                if (type != (Is64 ? 10 : 3)) throw new NotSupportedException("Unsupported PE relocation type: " + type);
                var rva = checked((int)(page + (uint)(encoded & 0xfff)));
                SetPointer(rva, unchecked(Pointer(rva) + delta));
            }
            cursor = checked(cursor + (int)size);
        }
    }
}
