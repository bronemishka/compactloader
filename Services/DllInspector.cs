using System.IO;
using System.Reflection.PortableExecutable;

namespace CompactLoader;

// Reads metadata only: never loads or executes the selected library.
internal static class DllInspector
{
    internal static string Inspect(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new PEReader(stream);
        var headers = reader.PEHeaders;
        var pe = headers.PEHeader ?? throw new BadImageFormatException("Missing PE optional header.");
        var isDll = (headers.CoffHeader.Characteristics & Characteristics.Dll) != 0;
        var architecture = headers.CoffHeader.Machine switch
        {
            Machine.I386 => "x86", Machine.Amd64 => "x64", Machine.Arm64 => "ARM64",
            _ => headers.CoffHeader.Machine.ToString()
        };
        var lines = new List<string>
        {
            "File: " + Path.GetFullPath(path),
            $"Size: {stream.Length:N0} bytes",
            $"Architecture: {architecture}; format: {pe.Magic}",
            "DLL flag: " + (isDll ? "yes" : "no — this is not a DLL"),
            "Runtime: " + (headers.CorHeader is null ? "native" : "managed / mixed .NET"),
            $"Sections: {headers.SectionHeaders.Length}; image size: {pe.SizeOfImage:N0} bytes",
            "TLS directory: " + (pe.ThreadLocalStorageTableDirectory.Size == 0 ? "absent" : "present"),
            "Delay imports: " + (pe.DelayImportTableDirectory.Size == 0 ? "absent" : "present"),
            "Exception directory: " + (pe.ExceptionTableDirectory.Size == 0 ? "absent" : "present")
        };
        if (!isDll) lines.Add("Warning: select a PE DLL.");
        if (headers.CorHeader is not null) lines.Add("Warning: CompactLoader does not host the CLR for managed DLLs.");
        var loaderArchitecture = Environment.Is64BitProcess ? "x64" : "x86";
        if (architecture != loaderArchitecture) lines.Add($"Warning: this build of CompactLoader is {loaderArchitecture}; DLL architecture differs.");
        if (pe.ThreadLocalStorageTableDirectory.Size != 0 || pe.DelayImportTableDirectory.Size != 0)
            lines.Add("Warning: this DLL uses directories unsupported by CompactLoader's Manual Map.");
        lines.Add("Metadata inspection only; dependencies and runtime compatibility are not verified.");
        return string.Join(Environment.NewLine, lines);
    }
}