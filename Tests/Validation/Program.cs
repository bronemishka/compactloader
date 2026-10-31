using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using static CompactLoader.Interop.NativeMethods;

namespace CompactLoader;

internal static class Program
{
    static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static int Main()
    {
        var folder = Path.Combine(Path.GetTempPath(), "CompactLoader-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var executable = Path.Combine(folder, "Counter.exe");
        var library = Path.Combine(folder, "Fixture.dll");
        var tls = Path.Combine(folder, "Tls.dll");
        File.WriteAllBytes(executable, NativeFixture.Build(true));
        File.WriteAllBytes(library, NativeFixture.Build(false));
        File.WriteAllBytes(tls, NativeFixture.Build(false, true));
        Console.WriteLine($"Architecture: {(Environment.Is64BitProcess ? "x64" : "x86")}; OS: {Environment.OSVersion}");
        try
        {
            ValidateMalformedImages(folder);
            foreach (var method in Enum.GetValues<LoadMethod>())
            {
                using var target = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden })!;
                try
                {
                    Thread.Sleep(200);
                    Assert(!target.HasExited, "Native fixture process exited unexpectedly.");
                    using var handle = OpenProcess(0x43a, false, target.Id);
                    using var remote = new RemoteExecution(handle);
                    var preferred = Environment.Is64BitProcess ? unchecked((nint)0x180000000L) : (nint)0x62000000;
                    // Reserve the preferred DLL base if the ASLR-relocated EXE left it free.
                    VirtualAllocEx(handle, preferred, 0x4000, 0x3000, 0x04);
                    var descriptor = new Target(target.Id, target.ProcessName, target.StartTime.ToUniversalTime().Ticks, Environment.Is64BitProcess ? "x64" : "x86");
                    var exeBase = target.MainModule!.BaseAddress;
                    var before = BitConverter.ToUInt32(remote.Read(exeBase + 0x2000, 4));
                    if (method == LoadMethod.LdrpLoader && !LdrpLoaderMethod.IsSupported)
                    {
                        try { Loader.Load(descriptor, library, method); throw new Exception("Unsupported LdrpLoader environment accepted."); }
                        catch (NotSupportedException) { Console.WriteLine("LdrpLoader: unsupported binary/architecture rejected; execution scenario skipped."); }
                        continue;
                    }
                    var message = Loader.Load(descriptor, library, method);
                    Console.WriteLine(method + ": " + message);
                    var dllBase = method == LoadMethod.ManualMap
                        ? unchecked((nint)(long)ulong.Parse(Regex.Match(message, @"0x([0-9A-Fa-f]+)").Groups[1].Value, NumberStyles.HexNumber))
                        : Loader.FindModule(target, library);
                    Assert(dllBase != 0, "No module base returned.");
                    Assert(dllBase != preferred, "Fixture did not exercise base relocation.");
                    Assert(BitConverter.ToInt32(remote.Read(dllBase + 0x2000, 4)) == target.Id, "DllMain did not call the imported API and record the target PID.");
                    Thread.Sleep(100);
                    var after = BitConverter.ToUInt32(remote.Read(exeBase + 0x2000, 4));
                    Assert(after != before, "Original thread did not resume the counter loop.");
                    var duplicate = Loader.Load(descriptor, library, method);
                    Assert(duplicate.Contains("Duplicate load skipped"), "Duplicate mapping was not detected.");
                    if (method == LoadMethod.LdrpLoader)
                    {
                        var rejected = Path.Combine(folder, "LdrpRejected.dll");
                        var bytes = NativeFixture.Build(false);
                        var changed = false;
                        for (var i = 0x200; i < 0x3fb; i++)
                            if (bytes[i] == 0xb8 && bytes[i + 1] == 1 && bytes[i + 2] == 0 && bytes[i + 3] == 0 && bytes[i + 4] == 0)
                            { bytes[i + 1] = 0; changed = true; break; }
                        Assert(changed, "Could not prepare LdrpLoader initialization failure fixture.");
                        File.WriteAllBytes(rejected, bytes);
                        try
                        {
                            try { Loader.Load(descriptor, rejected, method); throw new Exception("LdrpLoader accepted failed initialization."); }
                            catch (InvalidOperationException ex) when (ex.Message.Contains("NTSTATUS 0xC0000142")) { }
                            target.Refresh();
                            Assert(Loader.FindModule(target, rejected) == 0, "LdrpLoader left a failed module registered.");
                            Console.WriteLine("LdrpLoader: DllMain failure reported; failed module absent from loader list.");
                        }
                        finally { File.Delete(rejected); }
                    }
                    if (method == LoadMethod.ManualMap)
                    {
                        try { Loader.Load(descriptor, tls, method); throw new Exception("TLS image was accepted."); }
                        catch (NotSupportedException ex) when (ex.Message.Contains("TLS")) { Console.WriteLine("Unsupported TLS: rejected."); }
                        var malformed = Path.Combine(folder, "Malformed.dll");
                        var failed = Path.Combine(folder, "Failed.dll");
                        try
                        {
                            if (Environment.Is64BitProcess)
                            {
                                var bytes = NativeFixture.Build(false);
                                // Corrupt the x64 exception-directory size in the PE fixture.
                                BitConverter.GetBytes(13).CopyTo(bytes, 0x98 + 112 + 28);
                                File.WriteAllBytes(malformed, bytes);
                                try { _ = new PortableImage(malformed); throw new Exception("Malformed exception table accepted."); }
                                catch (BadImageFormatException) { Console.WriteLine("Invalid exception table: rejected during preflight."); }
                            }
                            var failureBytes = NativeFixture.Build(false);
                            var changed = false;
                            for (var i = 0x200; i < 0x3fb; i++)
                                if (failureBytes[i] == 0xb8 && failureBytes[i + 1] == 1 && failureBytes[i + 2] == 0 && failureBytes[i + 3] == 0 && failureBytes[i + 4] == 0)
                                { failureBytes[i + 1] = 0; changed = true; break; }
                            Assert(changed, "Could not prepare DllMain failure fixture.");
                            File.WriteAllBytes(failed, failureBytes);
                            try { Loader.Load(descriptor, failed, method); throw new Exception("DllMain failure accepted."); }
                            catch (InvalidOperationException ex) when (ex.Message.Contains("calling DLL entry point") && ex.Message.Contains("retained")) { }
                            try { Loader.Load(descriptor, failed, method); throw new Exception("Unsafe retry accepted."); }
                            catch (InvalidOperationException ex) when (ex.Message.Contains("previous Manual Map attempt")) { Console.WriteLine("Failed initialization: retained image tracked; retry blocked."); }
                        }
                        finally { File.Delete(malformed); File.Delete(failed); }
                    }
                    Console.WriteLine("PASS: imported API, relocation, continued target execution, duplicate load.");
                }
                finally { if (!target.HasExited) target.Kill(); target.WaitForExit(); }
            }
            Console.WriteLine("All validation scenarios passed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            try
            {
                File.Delete(executable); File.Delete(library); File.Delete(tls);
                Directory.Delete(folder);
            }
            catch (IOException) { }
        }
    }

    static void ValidateMalformedImages(string folder)
    {
        var path = Path.Combine(folder, "Invalid.dll");
        var directories = 0x98 + (Environment.Is64BitProcess ? 112 : 96);
        void Reject(string scenario, Action<byte[]> corrupt)
        {
            var bytes = NativeFixture.Build(false);
            corrupt(bytes);
            File.WriteAllBytes(path, bytes);
            try { _ = new PortableImage(path); }
            catch (Exception ex) when (ex is BadImageFormatException or NotSupportedException)
            { Console.WriteLine("Preflight rejected: " + scenario); return; }
            throw new Exception("Preflight accepted: " + scenario);
        }
        void U32(byte[] bytes, int offset, uint value) => BitConverter.GetBytes(value).CopyTo(bytes, offset);
        try
        {
            Reject("missing import terminator", b => U32(b, directories + 12, 20));
            Reject("missing IAT", b => U32(b, 0x510, 0));
            Reject("import name outside image", b => U32(b, 0x50c, 0x4000));
            Reject("truncated IAT", b => U32(b, 0x510, Environment.Is64BitProcess ? 0x3ff8u : 0x3ffcu));
            Reject("invalid ordinal bits", b =>
            {
                if (Environment.Is64BitProcess) BitConverter.GetBytes(0x8000000000010001ul).CopyTo(b, 0x560);
                else U32(b, 0x560, 0x80010001);
            });
            Reject("truncated relocation block", b => U32(b, 0x804, 14));
            Reject("relocation outside image", b => U32(b, 0x800, 0x4000));
            Reject("unsupported relocation type", b => BitConverter.GetBytes((ushort)0x1000).CopyTo(b, 0x808));
            // OriginalFirstThunk == 0 is a valid fallback to the unbound IAT.
            var fallback = NativeFixture.Build(false);
            U32(fallback, 0x500, 0);
            File.WriteAllBytes(path, fallback);
            _ = new PortableImage(path);
            Console.WriteLine("Preflight accepted: import lookup fallback to IAT.");
        }
        finally { File.Delete(path); }
    }
}
