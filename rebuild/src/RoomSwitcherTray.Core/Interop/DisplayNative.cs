using System.Runtime.InteropServices;

namespace RoomSwitcherTray.Core.Interop;

internal static class DisplayNative
{
    internal const uint QDC_ALL_PATHS = 0x00000001;
    internal const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    internal const uint DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1;
    internal const uint DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;
    internal const uint DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE = 10;
    internal const uint DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2 = 15;
    internal const uint DISPLAYCONFIG_DEVICE_INFO_SET_HDR_STATE = 16;
    internal const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    internal const int ENUM_CURRENT_SETTINGS = -1;
    internal const uint DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE = unchecked((uint)-3);
    internal const uint DISPLAYCONFIG_DEVICE_INFO_SET_DPI_SCALE = unchecked((uint)-4);
    internal const uint DISPLAYCONFIG_PATH_ACTIVE = 0x00000001;
    internal const uint DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xFFFFFFFF;
    internal const uint SDC_TOPOLOGY_SUPPLIED = 0x00000010;
    internal const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x00000020;
    internal const uint SDC_VALIDATE = 0x00000040;
    internal const uint SDC_APPLY = 0x00000080;
    internal const uint SDC_SAVE_TO_DATABASE = 0x00000200;
    internal const uint SDC_ALLOW_CHANGES = 0x00000400;
    internal const uint SDC_ALLOW_PATH_ORDER_CHANGES = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public RATIONAL refreshRate;
        public uint scanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)] public bool targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PATH_INFO
    {
        public PATH_SOURCE_INFO sourceInfo;
        public PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINTL { public int x; public int y; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct REGION { public uint cx; public uint cy; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct VIDEO_SIGNAL_INFO
    {
        public ulong pixelRate;
        public RATIONAL hSyncFreq;
        public RATIONAL vSyncFreq;
        public REGION activeSize;
        public REGION totalSize;
        public uint videoStandard;
        public uint scanLineOrdering;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct TARGET_MODE { public VIDEO_SIGNAL_INFO signal; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct SOURCE_MODE
    {
        public uint width;
        public uint height;
        public uint pixelFormat;
        public POINTL position;
    }
    [StructLayout(LayoutKind.Explicit)]
    internal struct MODE_UNION
    {
        [FieldOffset(0)] public TARGET_MODE target;
        [FieldOffset(0)] public SOURCE_MODE source;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MODE_INFO
    {
        public uint infoType;
        public uint id;
        public LUID adapterId;
        public MODE_UNION mode;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct TARGET_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SOURCE_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency,
            dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2,
            dmPanningWidth, dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SOURCE_DPI_SCALE_GET
    {
        public DEVICE_INFO_HEADER header;
        public int minScaleRel;
        public int curScaleRel;
        public int maxScaleRel;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SOURCE_DPI_SCALE_SET
    {
        public DEVICE_INFO_HEADER header;
        public int scaleRel;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ADVANCED_COLOR_INFO
    {
        public DEVICE_INFO_HEADER header;
        // Bit 0 = supported, bit 1 = enabled. The remaining flags are deliberately
        // ignored: they do not change whether the tray can offer a HDR toggle.
        public uint value;
        public uint colorEncoding;
        public uint bitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SET_ADVANCED_COLOR_STATE
    {
        public DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.Bool)] public bool enableAdvancedColor;
    }

    // Windows 11 24H2+ separates actual HDR from the broader "advanced color"
    // state. This is essential when Auto Color Management / WCG is enabled.
    [StructLayout(LayoutKind.Sequential)]
    internal struct ADVANCED_COLOR_INFO_2
    {
        public DEVICE_INFO_HEADER header;
        public uint value;
        public uint colorEncoding;
        public uint bitsPerColorChannel;
        public uint activeColorMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SET_HDR_STATE
    {
        public DEVICE_INFO_HEADER header;
        public uint value;
    }

    [DllImport("user32.dll")]
    internal static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);
    [DllImport("user32.dll")]
    internal static extern int QueryDisplayConfig(uint flags, ref uint pathCount,
        [Out] PATH_INFO[] paths, ref uint modeCount, [Out] MODE_INFO[] modes, nint topologyId);
    [DllImport("user32.dll")]
    internal static extern int SetDisplayConfig(uint pathCount, [In] PATH_INFO[] paths,
        uint modeCount, [In] MODE_INFO[]? modes, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int DisplayConfigGetDeviceInfo(ref TARGET_DEVICE_NAME request);
    [DllImport("user32.dll")]
    internal static extern int DisplayConfigGetDeviceInfo(ref SOURCE_DEVICE_NAME request);
    [DllImport("user32.dll")]
    internal static extern int DisplayConfigGetDeviceInfo(ref SOURCE_DPI_SCALE_GET request);
    [DllImport("user32.dll")]
    internal static extern int DisplayConfigGetDeviceInfo(ref ADVANCED_COLOR_INFO request);
    [DllImport("user32.dll")]
    internal static extern int DisplayConfigGetDeviceInfo(ref ADVANCED_COLOR_INFO_2 request);
    [DllImport("user32.dll")]
    internal static extern int DisplayConfigSetDeviceInfo(ref SET_ADVANCED_COLOR_STATE request);
    [DllImport("user32.dll")]
    internal static extern int DisplayConfigSetDeviceInfo(ref SET_HDR_STATE request);
    [DllImport("user32.dll")]
    internal static extern int DisplayConfigSetDeviceInfo(ref SOURCE_DPI_SCALE_SET request);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE mode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE mode,
        nint hwnd, uint flags, nint lParam);
}
