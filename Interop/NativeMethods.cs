using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CompactLoader.Interop;

internal static class NativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWow64Process2(SafeProcessHandle process, out ushort guest, out ushort host);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint GetModuleHandle(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)] internal static extern nint GetProcAddress(nint module, string name);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleExW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetModuleHandleEx(uint flags, nint address, out nint module);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern uint GetModuleFileName(nint module, StringBuilder name, int length);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern nint VirtualAllocEx(SafeProcessHandle process, nint address, nuint size, uint type, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool VirtualFreeEx(SafeProcessHandle process, nint address, nuint size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WriteProcessMemory(SafeProcessHandle process, nint address, byte[] buffer, nuint size, out nuint written);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeWaitHandle CreateRemoteThread(SafeProcessHandle process, nint attributes, nuint stackSize, nint start, nint parameter, uint flags, out uint id);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool VirtualProtectEx(SafeProcessHandle process, nint address, nuint size, uint protection, out uint oldProtection);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool FlushInstructionCache(SafeProcessHandle process, nint address, nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ReadProcessMemory(SafeProcessHandle process, nint address, byte[] data, nuint size, out nuint read);
    [DllImport("ntdll.dll")] internal static extern uint RtlNtStatusToDosError(int status);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeWaitHandle OpenThread(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint SuspendThread(SafeWaitHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(SafeWaitHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetThreadContext(SafeWaitHandle thread, nint context);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetThreadContext(SafeWaitHandle thread, nint context);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool InitializeContext(nint buffer, uint flags, out nint context, ref uint length);
    [DllImport("kernel32.dll")] internal static extern ulong GetEnabledXStateFeatures();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetXStateFeaturesMask(nint context, ulong mask);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
}
