using System.Diagnostics;
using System.Runtime.InteropServices;
using CompactLoader.Interop;
using Microsoft.Win32.SafeHandles;
using static CompactLoader.Interop.NativeMethods;
using static CompactLoader.RemoteExecution;

namespace CompactLoader;

internal static class ThreadHijackMethod
{
    internal static string Load(Process process, SafeProcessHandle handle, string path)
    {
        var loadLibrary = Export(process, "kernel32.dll", "LoadLibraryW");
        var ntContinue = Export(process, "ntdll.dll", "NtContinue");
        using var remote = new RemoteExecution(handle);
        var dataAddress = remote.Allocate(64);
        var data = new byte[64];
        PutPointer(data, 0, loadLibrary);
        PutPointer(data, 8, remote.String(path, true));
        PutPointer(data, 24, ntContinue);
        var code = new List<byte>();
        if (Environment.Is64BitProcess)
        {
            code.AddRange(new byte[] { 0x48,0x83,0xec,0x28, 0x48,0xbb });
            code.AddRange(BitConverter.GetBytes(dataAddress.ToInt64()));
            code.AddRange(new byte[] {
                0x65,0x8b,0x04,0x25,0x68,0,0,0, 0x89,0x43,0x38, // preserve LastErrorValue
                0x48,0x8b,0x4b,0x08, 0xff,0x13, 0x48,0x89,0x43,0x28,
                0x8b,0x43,0x38, 0x65,0x89,0x04,0x25,0x68,0,0,0,
                0xc7,0x43,0x30,1,0,0,0,
                0x48,0x8b,0x4b,0x10, 0x31,0xd2, 0xff,0x53,0x18,
                0x89,0x43,0x34, 0xeb,0xfe }); // NtContinue should never return on success
        }
        else
        {
            code.Add(0xbb);
            code.AddRange(BitConverter.GetBytes(dataAddress.ToInt32()));
            code.AddRange(new byte[] {
                0x64,0xa1,0x34,0,0,0, 0x89,0x43,0x38,
                0xff,0x73,0x08, 0xff,0x13, 0x89,0x43,0x28,
                0x8b,0x43,0x38, 0x64,0xa3,0x34,0,0,0,
                0xc7,0x43,0x30,1,0,0,0,
                0x6a,0, 0xff,0x73,0x10, 0xff,0x53,0x18,
                0x89,0x43,0x34, 0xeb,0xfe });
        }
        var entry = remote.Code(code.ToArray());
        Exception? lastError = null;
        foreach (ProcessThread candidate in process.Threads)
        {
            using (candidate)
            {
                if (process.Id == Environment.ProcessId && candidate.Id == GetCurrentThreadId()) continue;
                using var thread = OpenThread(0x0002 | 0x0008 | 0x0010 | 0x0040 | 0x100000, false, candidate.Id);
                if (thread.IsInvalid) continue;
                var previous = SuspendThread(thread);
                if (previous == uint.MaxValue) continue;
                bool suspended = true, redirected = false, started = false;
                ThreadContext? context = null;
                byte[]? original = null;
                try
                {
                    // Respect pre-existing suspensions instead of making another component's
                    // suspended thread runnable or leaving it with an altered context.
                    if (previous != 0) continue;
                    context = new ThreadContext(thread);
                    original = context.Snapshot();
                    var saved = remote.Allocate(original.Length);
                    remote.Write(saved, original);
                    PutPointer(data, 16, saved + context.Offset);
                    remote.Write(dataAddress, data);
                    var stack = (nint)((context.Stack.ToInt64() - 256) & ~15L);
                    if (Environment.Is64BitProcess) stack -= 8;
                    context.Redirect(entry, stack);
                    Check(SetThreadContext(thread, context.Context), "SetThreadContext");
                    redirected = true;
                    Check(ResumeThread(thread) != uint.MaxValue, "ResumeThread");
                    suspended = false;
                    started = true;
                    // NtContinue can still be reading the saved context when completion is
                    // observed. Retain scratch memory rather than freeing an in-use record.
                    remote.Retain();
                    var watch = Stopwatch.StartNew();
                    while (watch.ElapsedMilliseconds < 15000)
                    {
                        if (process.HasExited) throw new InvalidOperationException("Target exited during Thread Hijacking.");
                        var result = remote.Read(dataAddress, 64);
                        var failure = BitConverter.ToInt32(result, 52);
                        if (failure != 0)
                        {
                            // The stub is now parked after a failed NtContinue; restore from
                            // the injector while it is suspended and no longer inside DLL code.
                            Check(SuspendThread(thread) != uint.MaxValue, "SuspendThread (context recovery)");
                            suspended = true;
                            context.Restore(original);
                            Check(SetThreadContext(thread, context.Context), "SetThreadContext (recovery)");
                            redirected = false;
                            throw new InvalidOperationException($"NtContinue failed (NTSTATUS 0x{failure:X8}); original thread context restored.");
                        }
                        if (BitConverter.ToInt32(result, 48) == 1)
                        {
                            if (!HasLeftStub(thread, entry, code.Count, ntContinue))
                            {
                                Thread.Sleep(10);
                                continue;
                            }
                            if (Pointer(result, 40) == 0) throw new InvalidOperationException("LoadLibraryW failed on the hijacked thread.");
                            process.Refresh();
                            if (Loader.FindModule(process, path) == 0) throw new InvalidOperationException("DLL was not found in the target module list after Thread Hijacking.");
                            return $"DLL load verified using thread {candidate.Id}. Scratch code and saved context remain allocated until target exit.";
                        }
                        Thread.Sleep(10);
                    }
                    throw new TimeoutException("Thread Hijacking timed out. The thread may still be waiting or loading the DLL; saved context and stub remain allocated. Do not retry.");
                }
                catch (Exception ex) when (!started)
                {
                    lastError = ex;
                }
                finally
                {
                    if (suspended)
                    {
                        if (redirected && !started && context is not null && original is not null)
                        {
                            context.Restore(original);
                            if (!SetThreadContext(thread, context.Context))
                            {
                                remote.Retain();
                                throw new InvalidOperationException("Unable to restore redirected thread context after a launch failure.");
                            }
                        }
                        if (ResumeThread(thread) == uint.MaxValue)
                        {
                            remote.Retain();
                            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Unable to resume target thread.");
                        }
                    }
                    context?.Dispose();
                }
            }
        }
        throw new InvalidOperationException("No accessible, runnable target thread was available for Thread Hijacking.", lastError);
    }

    static bool HasLeftStub(SafeWaitHandle thread, nint entry, int length, nint continuation)
    {
        var previous = SuspendThread(thread);
        Check(previous != uint.MaxValue, "SuspendThread (completion check)");
        try
        {
            if (previous != 0) return false;
            using var context = new ThreadContext(thread);
            var ip = context.Instruction.ToInt64();
            // A load-return flag alone is insufficient: the stub must also hand
            // control back. Exclude the short NtContinue system-call thunk too.
            return !(ip >= entry.ToInt64() && ip < entry.ToInt64() + length) &&
                   !(ip >= continuation.ToInt64() && ip < continuation.ToInt64() + 64);
        }
        finally { Check(ResumeThread(thread) != uint.MaxValue, "ResumeThread (completion check)"); }
    }
}
