using System;
using System.Runtime.InteropServices;

namespace PCL.Core.UI.Effects;

/// <summary>Use Windows' SDR content brightness when compositing SDR labels in an scRGB surface.</summary>
internal static unsafe class HdrDisplayWhiteLevel
{
    internal static float GetScale(string deviceName)
    {
        // Topology can change between the size query and the path query. Retry the whole
        // snapshot, and use reference white if the optional query cannot be completed.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            uint pathCount, modeCount;
            if (GetDisplayConfigBufferSizes(2, &pathCount, &modeCount) != 0
                || pathCount > 256 || modeCount > 1024) return 1;
            var paths = new DisplayPath[pathCount];
            var modes = new ModeInfo[modeCount];
            int result;
            fixed (DisplayPath* pathPointer = paths)
            fixed (ModeInfo* modePointer = modes)
                result = QueryDisplayConfig(2, &pathCount, pathPointer, &modeCount, modePointer, 0);
            if (result == 122) continue;
            if (result != 0) return 1;
            for (var p = 0; p < pathCount; p++)
            {
                var path = paths[p];
                var source = new SourceName { Header = Header.Create(1, Marshal.SizeOf<SourceName>(), path.Source.Adapter, path.Source.Id) };
                if (GetSourceName(&source) != 0 || !string.Equals(new string(source.DeviceName), deviceName, StringComparison.OrdinalIgnoreCase)) continue;
                var white = new WhiteLevel { Header = Header.Create(11, Marshal.SizeOf<WhiteLevel>(), path.Target.Adapter, path.Target.Id) };
                // Windows defines this as a multiplier of 80 nits, scaled by 1000.
                if (GetWhiteLevel(&white) == 0 && white.Level is >= 1000 and <= 10000)
                    return white.Level / 1000f;
            }
        }
        return 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SourceInfo { public Luid Adapter; public uint Id, ModeIndex, Status; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TargetInfo
    {
        public Luid Adapter;
        public uint Id, ModeIndex, Technology, Rotation, Scaling, RefreshNumerator, RefreshDenominator, Scanline;
        public int Available;
        public uint Status;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayPath { public SourceInfo Source; public TargetInfo Target; public uint Flags; }
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct ModeInfo { public uint Type; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        public uint Type, Size;
        public Luid Adapter;
        public uint Id;
        public static Header Create(uint type, int size, Luid adapter, uint id) =>
            new() { Type = type, Size = (uint)size, Adapter = adapter, Id = id };
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceName
    {
        public Header Header;
        public fixed char DeviceName[32];
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct WhiteLevel { public Header Header; public uint Level; }

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, uint* pathCount, uint* modeCount);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, uint* pathCount,
        DisplayPath* paths, uint* modeCount, ModeInfo* modes, nint topology);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetSourceName(SourceName* name);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetWhiteLevel(WhiteLevel* level);
}
