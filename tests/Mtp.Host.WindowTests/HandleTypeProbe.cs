using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Mtp.Host.WindowTests;

/// <summary>
/// Enumerates this process's kernel handles by object type.
///
/// The combination regression grows about +19 handles per round in the test host process and nothing
/// about GDI/USER objects, thread count or MTP's own window accounting explains it. Without the object
/// type there is no way to tell a leaked WinUI composition object from an event/mutex retained by the
/// runtime. handle.exe is not installed on this machine, so this reads the kernel handle table directly.
///
/// NtQueryObject across processes dereferences a UNICODE_STRING that lives in the *target* process's
/// address space and therefore faults. This type never queries a foreign handle directly: it duplicates
/// the handle into this process with dwDesiredAccess = 0 and queries the copy, which is safe for any
/// handle this process already owns.
///
/// Layout of SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX (SystemExtendedHandleInformation = 64) as measured on
/// this machine, in bytes relative to ((byte*)buffer + 8) + 16:
///   +0  UniqueProcessId   (int)
///   +8  HandleValue       (long)
///   +16 GrantedAccess     (int)
///   +22 ObjectTypeIndex   (ushort)
///   +24 Object            (pointer, do NOT dereference)
/// Record size is 40 bytes. See .scratch/tool-cache/hkprobe/句柄表布局实测.md for the differential
/// experiment that confirmed every offset.
/// </summary>
internal static class HandleTypeProbe
{
    private const int SystemExtendedHandleInformation = 64;
    private const int ObjectTypeInformation = 2;
    private const int RecordSize = 40;
    private const int RecordBase = 8 + 16;
    private const int DuplicateSameAccess = 0x00000002;

    public sealed record Sample(int Total, int Scanned, IReadOnlyDictionary<string, int> ByType)
    {
        public int Of(string type) => ByType.TryGetValue(type, out int value) ? value : 0;
    }

    public static Sample Capture()
    {
        int pid = Environment.ProcessId;
        var byIndex = Scan(pid);
        var names = new Dictionary<int, string>();
        var byType = new Dictionary<string, int>();
        int total = 0;
        foreach (var pair in byIndex)
        {
            total += pair.Value.Count;
            names[pair.Key] = ResolveName(pair.Value.Sample) ?? ("unknown-" + pair.Key);
        }
        foreach (var pair in byIndex)
        {
            string name = names[pair.Key];
            byType[name] = byType.TryGetValue(name, out int existing) ? existing + pair.Value.Count : pair.Value.Count;
        }
        return new Sample(total, total, byType);
    }

    private sealed class TypeSlot
    {
        public int Count;
        public nint Sample;
    }

    private static Dictionary<int, TypeSlot> Scan(int pid)
    {
        int length = 1 << 27;
        nint buffer = Marshal.AllocHGlobal(length);
        try
        {
            int status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, length, out _);
            if (status != 0) return [];
            int count = Marshal.ReadInt32(buffer);
            nint record = buffer + RecordBase;
            var slots = new Dictionary<int, TypeSlot>();
            for (int i = 0; i < count; i++)
            {
                nint entry = record + i * RecordSize;
                if (Marshal.ReadInt32(entry) != pid) continue;
                int typeIndex = Marshal.ReadInt16(entry + 22);
                if (!slots.TryGetValue(typeIndex, out var slot)) slots[typeIndex] = slot = new TypeSlot { Sample = Marshal.ReadIntPtr(entry + 8) };
                slot.Count++;
            }
            return slots;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? ResolveName(nint handle)
    {
        nint source = Process.GetCurrentProcess().Handle;
        if (!DuplicateHandle(source, handle, source, out nint copy, 0, false, DuplicateSameAccess)) return null;
        try
        {
            nint buffer = Marshal.AllocHGlobal(2048);
            try
            {
                if (NtQueryObject(copy, ObjectTypeInformation, buffer, 2048, out _) != 0) return null;
                int length = Marshal.ReadInt16(buffer);
                nint text = Marshal.ReadIntPtr(buffer + 8);
                return length > 0 && text != 0 ? Marshal.PtrToStringUni(text, length / 2) : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(copy);
        }    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int informationClass, nint buffer, int length, out int returned);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryObject(nint handle, int informationClass, nint buffer, int length, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(nint sourceProcess, nint source, nint targetProcess, out nint target, uint access, bool inherit, uint options);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}
