using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static CompactLoader.Interop.NativeMethods;
using static CompactLoader.RemoteExecution;

namespace CompactLoader.Interop;

// InitializeContext supplies the architecture-specific alignment and the extended
// register storage, including enabled AVX/XState features.
internal sealed class ThreadContext : IDisposable
{
    readonly nint allocation;
    internal nint Buffer { get; }
    internal nint Context { get; }
    internal int Length { get; }
    internal int Offset => checked((int)(Context - Buffer));

    internal ThreadContext(SafeWaitHandle thread)
    {
        uint flags = Environment.Is64BitProcess ? 0x0010005fu : 0x0001007fu;
        uint length = 0;
        InitializeContext(0, flags, out _, ref length);
        if (length == 0 || length > 1024 * 1024)
            throw new InvalidOperationException("Unable to determine extended thread context size.");
        Length = checked((int)length);
        allocation = Marshal.AllocHGlobal(checked(Length + 64));
        Buffer = (nint)((allocation.ToInt64() + 63) & ~63L);
        try
        {
            Marshal.Copy(new byte[Length], 0, Buffer, Length);
            Check(InitializeContext(Buffer, flags, out var context, ref length), "InitializeContext");
            Context = context;
            Check(SetXStateFeaturesMask(Context, GetEnabledXStateFeatures()), "SetXStateFeaturesMask");
            Check(GetThreadContext(thread, Context), "GetThreadContext");
        }
        catch { Marshal.FreeHGlobal(allocation); throw; }
    }

    internal byte[] Snapshot()
    {
        var result = new byte[Length];
        Marshal.Copy(Buffer, result, 0, Length);
        return result;
    }
    internal void Restore(byte[] bytes) => Marshal.Copy(bytes, 0, Buffer, Length);
    internal nint Stack => Environment.Is64BitProcess ? (nint)Marshal.ReadInt64(Context, 152) : (nint)Marshal.ReadInt32(Context, 196);
    internal nint Instruction => Environment.Is64BitProcess ? (nint)Marshal.ReadInt64(Context, 248) : (nint)Marshal.ReadInt32(Context, 184);
    internal void Redirect(nint instruction, nint stack)
    {
        if (Environment.Is64BitProcess)
        {
            Marshal.WriteInt64(Context, 248, instruction.ToInt64());
            Marshal.WriteInt64(Context, 152, stack.ToInt64());
        }
        else
        {
            Marshal.WriteInt32(Context, 184, instruction.ToInt32());
            Marshal.WriteInt32(Context, 196, stack.ToInt32());
        }
    }
    public void Dispose() => Marshal.FreeHGlobal(allocation);
}
