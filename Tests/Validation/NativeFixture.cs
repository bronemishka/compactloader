using System.Text;

namespace CompactLoader;

// Minimal native PE fixtures: a one-thread counter process and a DLL whose
// entry point calls an imported API and writes its PID into the data section.
// The two fixtures share a preferred base, forcing DLL relocation.
internal static class NativeFixture
{
    internal static byte[] Build(bool executable, bool tls = false)
    {
        bool x64 = Environment.Is64BitProcess;
        ulong imageBase = x64 ? 0x180000000ul : 0x62000000ul;
        var file = new byte[0xa00];
        using var stream = new MemoryStream(file);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        void At(int offset) => stream.Position = offset;
        void U16(int offset, ushort value) { At(offset); writer.Write(value); }
        void U32(int offset, uint value) { At(offset); writer.Write(value); }
        void U64(int offset, ulong value) { At(offset); writer.Write(value); }
        void Text(int offset, string value) { At(offset); writer.Write(Encoding.ASCII.GetBytes(value + '\0')); }
        void Pointer(int offset, ulong value) { if (x64) U64(offset, value); else U32(offset, (uint)value); }
        U16(0, 0x5a4d); U32(0x3c, 0x80); U32(0x80, 0x4550);
        U16(0x84, x64 ? (ushort)0x8664 : (ushort)0x14c); U16(0x86, 3);
        U16(0x94, x64 ? (ushort)0xf0 : (ushort)0xe0);
        U16(0x96, executable ? (ushort)0x22 : (ushort)0x2022);
        const int optional = 0x98;
        U16(optional, x64 ? (ushort)0x20b : (ushort)0x10b);
        U32(optional + 4, 0x200); U32(optional + 8, 0x600);
        U32(optional + 16, 0x1000); U32(optional + 20, 0x1000);
        if (x64) U64(optional + 24, imageBase);
        else { U32(optional + 24, 0x2000); U32(optional + 28, (uint)imageBase); }
        U32(optional + 32, 0x1000); U32(optional + 36, 0x200);
        U16(optional + 40, 6); U16(optional + 48, 6);
        U32(optional + 56, 0x4000); U32(optional + 60, 0x200);
        U16(optional + 68, 2); U16(optional + 70, 0x140);
        if (x64)
        {
            U64(optional + 72, 0x100000); U64(optional + 80, 0x1000);
            U64(optional + 88, 0x100000); U64(optional + 96, 0x1000); U32(optional + 108, 16);
        }
        else
        {
            U32(optional + 72, 0x100000); U32(optional + 76, 0x1000);
            U32(optional + 80, 0x100000); U32(optional + 84, 0x1000); U32(optional + 92, 16);
        }
        var directories = optional + (x64 ? 112 : 96);
        U32(directories + 8, 0x2100); U32(directories + 12, 40);
        U32(directories + 40, 0x3000); U32(directories + 44, 12);
        if (tls) { U32(directories + 72, 0x2300); U32(directories + 76, x64 ? 40u : 24u); }
        var sectionTable = optional + (x64 ? 0xf0 : 0xe0);
        void Section(int index, string name, uint rva, uint size, uint raw, uint flags)
        {
            int offset = sectionTable + index * 40;
            Text(offset, name); U32(offset + 8, size); U32(offset + 12, rva);
            U32(offset + 16, size); U32(offset + 20, raw); U32(offset + 36, flags);
        }
        Section(0, ".text", 0x1000, 0x200, 0x200, 0x60000020);
        Section(1, ".data", 0x2000, 0x400, 0x400, 0xc0000040);
        Section(2, ".reloc", 0x3000, 0x200, 0x800, 0x42000040);
        U32(0x500, 0x2160); U32(0x50c, 0x2140); U32(0x510, 0x2180);
        Text(0x540, "kernel32.dll"); Pointer(0x560, 0x21a0); Pointer(0x580, 0x21a0);
        Text(0x5a2, "GetCurrentProcessId");
        var code = new List<byte>();
        var relocations = new List<int>();
        void Immediate(ulong value)
        {
            relocations.Add(code.Count);
            code.AddRange(x64 ? BitConverter.GetBytes(value) : BitConverter.GetBytes((uint)value));
        }
        if (executable)
        {
            code.AddRange(x64 ? new byte[] { 0x48,0xb8 } : new byte[] { 0xb8 });
            Immediate(imageBase + 0x2000);
            code.AddRange(new byte[] { 0xf0,0xff,0x00, 0xf3,0x90, 0xeb,0xf9 });
        }
        else if (x64)
        {
            code.AddRange(new byte[] { 0x48,0x83,0xec,0x28, 0x48,0xb8 }); Immediate(imageBase + 0x2180);
            code.AddRange(new byte[] { 0xff,0x10, 0x48,0xba }); Immediate(imageBase + 0x2000);
            code.AddRange(new byte[] { 0x89,0x02, 0x48,0x83,0xc4,0x28, 0xb8,1,0,0,0, 0xc3 });
            U32(directories + 24, 0x2200); U32(directories + 28, 12);
            U32(0x600, 0x1000); U32(0x604, (uint)(0x1000 + code.Count)); U32(0x608, 0x2300);
            At(0x700); writer.Write(new byte[] { 1,4,1,0, 4,0x42,0,0 });
        }
        else
        {
            code.Add(0xb8); Immediate(imageBase + 0x2180);
            code.AddRange(new byte[] { 0xff,0x10, 0xba }); Immediate(imageBase + 0x2000);
            code.AddRange(new byte[] { 0x89,0x02, 0xb8,1,0,0,0, 0xc2,0x0c,0 });
        }
        At(0x200); writer.Write(code.ToArray());
        U32(0x800, 0x1000); U32(0x804, 12);
        for (int i = 0; i < relocations.Count; i++) U16(0x808 + i * 2, (ushort)((x64 ? 10 : 3) << 12 | relocations[i]));
        return file;
    }
}
