using RoomSwitcherTray.Core.Interop;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RoomSwitcherTray.Core.Services;

public sealed class DisplayService
{
    public IReadOnlyList<DisplayDevice> GetDisplays() => GetDisplays(false);
    public IReadOnlyList<DisplayDevice> GetKnownDisplays() => GetDisplays(true);

    private IReadOnlyList<DisplayDevice> GetDisplays(bool includeUnavailable)
    {
        DisplayNative.PATH_INFO[] paths = QueryPaths();
        var result = new Dictionary<string, DisplayDevice>(StringComparer.OrdinalIgnoreCase);

        foreach (DisplayNative.PATH_INFO path in paths)
        {
            (string id, string name, Guid? containerId) =
                GetIdentity(path.targetInfo.adapterId, path.targetInfo.id);
            if (string.IsNullOrWhiteSpace(id))
                continue;

            bool active = (path.flags & DisplayNative.DISPLAYCONFIG_PATH_ACTIVE) != 0;
            var device = new DisplayDevice(id, name, active,
                path.targetInfo.targetAvailable, containerId);
            if (!result.TryGetValue(id, out DisplayDevice? existing) ||
                (!existing.IsAvailable && device.IsAvailable) ||
                (existing.IsAvailable == device.IsAvailable && !existing.IsActive && active))
                result[id] = device;
        }

        return result.Values
            .Where(device => includeUnavailable || device.IsAvailable)
            .OrderByDescending(device => device.IsActive)
            .ThenBy(device => device.Name)
            .ToList();
    }

    public void ApplyDisplays(IReadOnlyCollection<string> displayIds)
    {
        string[] requestedIds = displayIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (requestedIds.Length is < 1 or > 4)
            throw new InvalidOperationException("Сценарий должен содержать от одного до четырёх дисплеев.");

        DisplayNative.PATH_INFO[] allPaths = QueryPaths();
        var candidates = new Dictionary<string, List<DisplayNative.PATH_INFO>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (DisplayNative.PATH_INFO candidate in allPaths)
        {
            if (!candidate.targetInfo.targetAvailable) continue;
            (string id, _, _) = GetIdentity(candidate.targetInfo.adapterId, candidate.targetInfo.id);
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (!candidates.TryGetValue(id, out List<DisplayNative.PATH_INFO>? paths))
                candidates[id] = paths = [];
            paths.Add(candidate);
        }

        var requestedCandidates = new List<DisplayNative.PATH_INFO[]>(requestedIds.Length);
        foreach (string id in requestedIds)
        {
            if (!candidates.TryGetValue(id, out List<DisplayNative.PATH_INFO>? paths))
                throw new InvalidOperationException("Выбранный дисплей сейчас недоступен.");

            DisplayNative.PATH_INFO[] orderedPaths = paths
                .OrderByDescending(item =>
                    (item.flags & DisplayNative.DISPLAYCONFIG_PATH_ACTIVE) != 0)
                .ThenByDescending(item => item.targetInfo.targetAvailable)
                .Select(PreparePath)
                .ToArray();
            requestedCandidates.Add(orderedPaths);
        }

        // Restore Windows' persisted topology for exactly these displays. This keeps the
        // user's resolution, primary monitor and relative positions without owning them.
        foreach (DisplayNative.PATH_INFO[] topology in BuildExtendedTopologies(requestedCandidates))
        {
            uint databaseFlags = DisplayNative.SDC_TOPOLOGY_SUPPLIED |
                DisplayNative.SDC_ALLOW_PATH_ORDER_CHANGES;
            int databaseError = DisplayNative.SetDisplayConfig((uint)topology.Length, topology,
                0, null, databaseFlags | DisplayNative.SDC_VALIDATE);
            if (databaseError != 0)
                continue;

            databaseError = DisplayNative.SetDisplayConfig((uint)topology.Length, topology,
                0, null, databaseFlags | DisplayNative.SDC_APPLY);
            if (databaseError == 0)
                return;
        }

        // There is no saved topology yet. Ask Windows to create one, but only from paths
        // with distinct sources so a multi-display scenario is extended, never cloned.
        DisplayNative.PATH_INFO[] requestedPaths = BuildExtendedTopologies(requestedCandidates)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "Не удалось подобрать раздельные видеовыходы для выбранных дисплеев.");
        uint common = DisplayNative.SDC_USE_SUPPLIED_DISPLAY_CONFIG | DisplayNative.SDC_ALLOW_CHANGES;
        int error = DisplayNative.SetDisplayConfig((uint)requestedPaths.Length, requestedPaths, 0, null,
            common | DisplayNative.SDC_VALIDATE);
        if (error != 0)
            throw new Win32Exception(error, "Windows отклонила выбранную конфигурацию дисплея.");

        error = DisplayNative.SetDisplayConfig((uint)requestedPaths.Length, requestedPaths, 0, null,
            common | DisplayNative.SDC_APPLY | DisplayNative.SDC_SAVE_TO_DATABASE);
        if (error != 0)
            throw new Win32Exception(error, "Не удалось включить выбранные дисплеи.");
    }

    public IReadOnlyList<ActiveDisplayStatus> GetActiveDisplayStatuses(
        IReadOnlyCollection<string> configuredDisplayIds)
    {
        var requested = configuredDisplayIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requested.Count == 0) return [];
        return GetActiveDisplayStatuses(requested, configuredDisplayIds);
    }

    public IReadOnlyList<ActiveDisplayStatus> GetActiveDisplayStatuses()
        => GetActiveDisplayStatuses(null, null);

    private IReadOnlyList<ActiveDisplayStatus> GetActiveDisplayStatuses(
        HashSet<string>? requested, IReadOnlyCollection<string>? configuredDisplayIds)
    {

        DisplayConfiguration configuration = QueryConfiguration(DisplayNative.QDC_ONLY_ACTIVE_PATHS);
        var result = new Dictionary<string, ActiveDisplayStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (DisplayNative.PATH_INFO path in configuration.Paths)
        {
            (string id, string name, _) = GetIdentity(path.targetInfo.adapterId, path.targetInfo.id);
            if (string.IsNullOrWhiteSpace(id) || (requested is not null && !requested.Contains(id))) continue;

            int width = 0, height = 0;
            if (path.sourceInfo.modeInfoIdx != DisplayNative.DISPLAYCONFIG_PATH_MODE_IDX_INVALID &&
                path.sourceInfo.modeInfoIdx < configuration.Modes.Length)
            {
                DisplayNative.MODE_INFO mode = configuration.Modes[path.sourceInfo.modeInfoIdx];
                if (mode.infoType == DisplayNative.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
                {
                    width = unchecked((int)mode.mode.source.width);
                    height = unchecked((int)mode.mode.source.height);
                }
            }

            (DisplayNative.LUID adapterId, uint targetId) = GetColorTarget(path, configuration.Modes);
            (bool supported, bool enabled) = GetHdrState(adapterId, targetId);
            result[id] = new ActiveDisplayStatus(id, name, width, height, supported, enabled);
        }
        return configuredDisplayIds is null
            ? result.Values.ToList()
            : configuredDisplayIds.Where(result.ContainsKey).Select(id => result[id]).ToList();
    }

    public void SetHdr(string displayId, bool enabled)
    {
        DisplayConfiguration configuration = QueryConfiguration(DisplayNative.QDC_ONLY_ACTIVE_PATHS);
        foreach (DisplayNative.PATH_INFO path in configuration.Paths)
        {
            (string id, _, _) = GetIdentity(path.targetInfo.adapterId, path.targetInfo.id);
            if (!id.Equals(displayId, StringComparison.OrdinalIgnoreCase)) continue;

            (DisplayNative.LUID adapterId, uint targetId) = GetColorTarget(path, configuration.Modes);
            (bool supported, _) = GetHdrState(adapterId, targetId);
            if (!supported)
                throw new InvalidOperationException("Этот дисплей не поддерживает HDR.");

            var hdrRequest = new DisplayNative.SET_HDR_STATE
            {
                header = new DisplayNative.DEVICE_INFO_HEADER
                {
                    type = DisplayNative.DISPLAYCONFIG_DEVICE_INFO_SET_HDR_STATE,
                    size = (uint)Marshal.SizeOf<DisplayNative.SET_HDR_STATE>(),
                    adapterId = adapterId,
                    id = targetId
                },
                value = enabled ? 1u : 0u
            };
            int error = DisplayNative.DisplayConfigSetDeviceInfo(ref hdrRequest);
            if (error != 0)
            {
                // Windows 10 and pre-24H2 Windows 11 do not provide the dedicated
                // HDR request. They retain the legacy advanced-color fallback.
                var legacyRequest = new DisplayNative.SET_ADVANCED_COLOR_STATE
                {
                    header = new DisplayNative.DEVICE_INFO_HEADER
                    {
                        type = DisplayNative.DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE,
                        size = (uint)Marshal.SizeOf<DisplayNative.SET_ADVANCED_COLOR_STATE>(),
                        adapterId = adapterId,
                        id = targetId
                    },
                    enableAdvancedColor = enabled
                };
                error = DisplayNative.DisplayConfigSetDeviceInfo(ref legacyRequest);
            }
            if (error != 0) throw new Win32Exception(error, "Windows не удалось изменить HDR.");
            return;
        }
        throw new InvalidOperationException("Активный дисплей не найден.");
    }

    public void ApplyDisplaySettings(
        IReadOnlyDictionary<string, DisplayResolutionPreset> resolutions,
        IReadOnlyDictionary<string, int> scales)
    {
        foreach ((string id, DisplayResolutionPreset preset) in resolutions)
            if (preset != DisplayResolutionPreset.KeepCurrent)
                SetResolution(id, preset);

        foreach ((string id, int percent) in scales)
            SetScale(id, percent);
    }

    public void SetScale(string displayId, int percent)
    {
        if (!DpiValues.Contains(percent)) throw new ArgumentOutOfRangeException(nameof(percent));
        (DisplayNative.LUID adapterId, uint sourceId) = FindActiveSource(displayId);
        DpiScaleInfo info = GetDpiScale(adapterId, sourceId);
        int targetIndex = Array.IndexOf(DpiValues, percent);
        int recommendedIndex = Array.IndexOf(DpiValues, info.Recommended);
        if (targetIndex < 0 || recommendedIndex < 0 || percent < info.Minimum || percent > info.Maximum)
            throw new InvalidOperationException($"Масштаб {percent}% недоступен для этого экрана.");
        var request = new DisplayNative.SOURCE_DPI_SCALE_SET
        {
            header = new DisplayNative.DEVICE_INFO_HEADER
            {
                type = DisplayNative.DISPLAYCONFIG_DEVICE_INFO_SET_DPI_SCALE,
                size = (uint)Marshal.SizeOf<DisplayNative.SOURCE_DPI_SCALE_SET>(),
                adapterId = adapterId,
                id = sourceId
            },
            scaleRel = targetIndex - recommendedIndex
        };
        if (Marshal.SizeOf<DisplayNative.SOURCE_DPI_SCALE_SET>() != 0x18)
            throw new InvalidOperationException("Структура масштаба несовместима с этой версией Windows.");
        int error = DisplayNative.DisplayConfigSetDeviceInfo(ref request);
        if (error != 0) throw new Win32Exception(error, "Windows не удалось изменить масштаб.");
    }

    public int? GetScale(string displayId)
    {
        try
        {
            (DisplayNative.LUID adapterId, uint sourceId) = FindActiveSource(displayId);
            return GetDpiScale(adapterId, sourceId).Current;
        }
        catch { return null; }
    }

    private static readonly int[] DpiValues = [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];

    public IReadOnlyList<int> GetSupportedScales(string displayId)
    {
        try
        {
            (DisplayNative.LUID adapterId, uint sourceId) = FindActiveSource(displayId);
            DpiScaleInfo info = GetDpiScale(adapterId, sourceId);
            return [.. new[] { 100, 125, 150, 175, 200 }
                .Where(percent => percent >= info.Minimum && percent <= info.Maximum)];
        }
        catch { return []; }
    }

    private static DpiScaleInfo GetDpiScale(DisplayNative.LUID adapterId, uint sourceId)
    {
        var request = new DisplayNative.SOURCE_DPI_SCALE_GET
        {
            header = new DisplayNative.DEVICE_INFO_HEADER
            {
                type = DisplayNative.DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE,
                size = (uint)Marshal.SizeOf<DisplayNative.SOURCE_DPI_SCALE_GET>(),
                adapterId = adapterId,
                id = sourceId
            }
        };
        if (Marshal.SizeOf<DisplayNative.SOURCE_DPI_SCALE_GET>() != 0x20)
            throw new InvalidOperationException("Структура масштаба несовместима с этой версией Windows.");
        int error = DisplayNative.DisplayConfigGetDeviceInfo(ref request);
        if (error != 0) throw new Win32Exception(error, "Windows не удалось прочитать масштаб.");
        int recommendedIndex = Math.Abs(request.minScaleRel);
        int currentIndex = recommendedIndex + Math.Clamp(request.curScaleRel, request.minScaleRel, request.maxScaleRel);
        int maximumIndex = recommendedIndex + request.maxScaleRel;
        if (recommendedIndex < 0 || currentIndex < 0 || maximumIndex >= DpiValues.Length)
            throw new InvalidOperationException("Windows вернула неизвестную шкалу масштаба.");
        return new(DpiValues[0], DpiValues[maximumIndex], DpiValues[currentIndex], DpiValues[recommendedIndex]);
    }

    private static (DisplayNative.LUID AdapterId, uint SourceId) FindActiveSource(string displayId)
    {
        DisplayConfiguration configuration = QueryConfiguration(DisplayNative.QDC_ONLY_ACTIVE_PATHS);
        foreach (DisplayNative.PATH_INFO path in configuration.Paths)
        {
            (string id, _, _) = GetIdentity(path.targetInfo.adapterId, path.targetInfo.id);
            if (id.Equals(displayId, StringComparison.OrdinalIgnoreCase))
                return (path.sourceInfo.adapterId, path.sourceInfo.id);
        }
        throw new InvalidOperationException("Активный дисплей не найден.");
    }

    private sealed record DpiScaleInfo(int Minimum, int Maximum, int Current, int Recommended);

    public void SetResolution(string displayId, DisplayResolutionPreset preset)
    {
        (int width, int height) = DisplayResolution.Size(preset);
        if (width == 0) return;
        string? deviceName = GetActiveSourceName(displayId);
        if (deviceName is null) throw new InvalidOperationException("Активный дисплей не найден.");
        var chosen = new DisplayNative.DEVMODE { dmSize = (ushort)Marshal.SizeOf<DisplayNative.DEVMODE>() };
        bool found = false;
        for (int index = 0; index < 256; index++)
        {
            var mode = new DisplayNative.DEVMODE { dmSize = (ushort)Marshal.SizeOf<DisplayNative.DEVMODE>() };
            if (!DisplayNative.EnumDisplaySettings(deviceName, index, ref mode)) break;
            if (mode.dmPelsWidth != width || mode.dmPelsHeight != height) continue;
            chosen = mode; found = true; break;
        }
        if (!found) throw new InvalidOperationException($"Дисплей не поддерживает {width} × {height}.");
        int error = DisplayNative.ChangeDisplaySettingsEx(deviceName, ref chosen, nint.Zero, 0, nint.Zero);
        if (error != 0) throw new Win32Exception(error, "Windows не удалось изменить разрешение.");
    }

    public IReadOnlyList<DisplayResolutionPreset> GetSupportedResolutionPresets(string displayId)
    {
        string? deviceName = GetActiveSourceName(displayId);
        if (deviceName is null) return [];
        var sizes = new HashSet<(int Width, int Height)>();
        for (int index = 0; index < 256; index++)
        {
            var mode = new DisplayNative.DEVMODE { dmSize = (ushort)Marshal.SizeOf<DisplayNative.DEVMODE>() };
            if (!DisplayNative.EnumDisplaySettings(deviceName, index, ref mode)) break;
            sizes.Add(((int)mode.dmPelsWidth, (int)mode.dmPelsHeight));
        }
        return [.. DisplayResolution.Presets.Where(preset => sizes.Contains(DisplayResolution.Size(preset)))];
    }

    private static string? GetActiveSourceName(string displayId)
    {
        DisplayConfiguration configuration = QueryConfiguration(DisplayNative.QDC_ONLY_ACTIVE_PATHS);
        foreach (DisplayNative.PATH_INFO path in configuration.Paths)
        {
            (string id, _, _) = GetIdentity(path.targetInfo.adapterId, path.targetInfo.id);
            if (!id.Equals(displayId, StringComparison.OrdinalIgnoreCase)) continue;
            var request = new DisplayNative.SOURCE_DEVICE_NAME
            {
                header = new DisplayNative.DEVICE_INFO_HEADER
                {
                    type = DisplayNative.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                    size = (uint)Marshal.SizeOf<DisplayNative.SOURCE_DEVICE_NAME>(),
                    adapterId = path.sourceInfo.adapterId, id = path.sourceInfo.id
                }
            };
            return DisplayNative.DisplayConfigGetDeviceInfo(ref request) == 0 ? request.viewGdiDeviceName : null;
        }
        return null;
    }

    private static DisplayNative.PATH_INFO PreparePath(DisplayNative.PATH_INFO path)
    {
        path.flags = DisplayNative.DISPLAYCONFIG_PATH_ACTIVE;
        path.sourceInfo.modeInfoIdx = DisplayNative.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
        path.targetInfo.modeInfoIdx = DisplayNative.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
        return path;
    }

    private static IEnumerable<DisplayNative.PATH_INFO[]> BuildExtendedTopologies(
        IReadOnlyList<DisplayNative.PATH_INFO[]> candidates)
    {
        var selected = new DisplayNative.PATH_INFO[candidates.Count];
        var usedSources = new HashSet<(uint Low, int High, uint Id)>();

        return Build(0);

        IEnumerable<DisplayNative.PATH_INFO[]> Build(int index)
        {
            if (index == candidates.Count)
            {
                yield return [.. selected];
                yield break;
            }

            foreach (DisplayNative.PATH_INFO path in candidates[index])
            {
                var source = (path.sourceInfo.adapterId.LowPart,
                    path.sourceInfo.adapterId.HighPart, path.sourceInfo.id);
                if (!usedSources.Add(source)) continue;
                selected[index] = path;
                foreach (DisplayNative.PATH_INFO[] topology in Build(index + 1))
                    yield return topology;
                usedSources.Remove(source);
            }
        }
    }

    private static (bool Supported, bool Enabled) GetHdrState(DisplayNative.LUID adapterId, uint targetId)
    {
        var hdrRequest = new DisplayNative.ADVANCED_COLOR_INFO_2
        {
            header = new DisplayNative.DEVICE_INFO_HEADER
            {
                type = DisplayNative.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2,
                size = (uint)Marshal.SizeOf<DisplayNative.ADVANCED_COLOR_INFO_2>(),
                adapterId = adapterId,
                id = targetId
            }
        };
        if (DisplayNative.DisplayConfigGetDeviceInfo(ref hdrRequest) == 0)
        {
            bool supported = (hdrRequest.value & 0x10) != 0;
            // The mode is authoritative: advanced color can remain active as WCG
            // while the user-facing "Use HDR" switch is off.
            return (supported, supported && hdrRequest.activeColorMode == 2);
        }

        var request = new DisplayNative.ADVANCED_COLOR_INFO
        {
            header = new DisplayNative.DEVICE_INFO_HEADER
            {
                type = DisplayNative.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO,
                size = (uint)Marshal.SizeOf<DisplayNative.ADVANCED_COLOR_INFO>(),
                adapterId = adapterId,
                id = targetId
            }
        };
        if (DisplayNative.DisplayConfigGetDeviceInfo(ref request) != 0) return (false, false);
        return ((request.value & 0x1) != 0, (request.value & 0x2) != 0);
    }

    private static (DisplayNative.LUID AdapterId, uint TargetId) GetColorTarget(
        DisplayNative.PATH_INFO path, IReadOnlyList<DisplayNative.MODE_INFO> modes)
    {
        if (path.targetInfo.modeInfoIdx != DisplayNative.DISPLAYCONFIG_PATH_MODE_IDX_INVALID &&
            path.targetInfo.modeInfoIdx < modes.Count)
        {
            DisplayNative.MODE_INFO mode = modes[(int)path.targetInfo.modeInfoIdx];
            return (mode.adapterId, mode.id);
        }
        return (path.targetInfo.adapterId, path.targetInfo.id);
    }

    private static DisplayNative.PATH_INFO[] QueryPaths() =>
        QueryConfiguration(DisplayNative.QDC_ALL_PATHS).Paths;

    private static DisplayConfiguration QueryConfiguration(uint flags)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int error = DisplayNative.GetDisplayConfigBufferSizes(flags,
                out uint pathCount, out uint modeCount);
            if (error != 0) throw new Win32Exception(error);

            var paths = new DisplayNative.PATH_INFO[pathCount];
            var modes = new DisplayNative.MODE_INFO[modeCount];
            error = DisplayNative.QueryDisplayConfig(flags,
                ref pathCount, paths, ref modeCount, modes, nint.Zero);
            if (error == 0)
            {
                Array.Resize(ref paths, (int)pathCount);
                Array.Resize(ref modes, (int)modeCount);
                return new DisplayConfiguration(paths, modes);
            }
            if (error != 122) throw new Win32Exception(error);
        }
        throw new Win32Exception(122);
    }

    private sealed record DisplayConfiguration(
        DisplayNative.PATH_INFO[] Paths,
        DisplayNative.MODE_INFO[] Modes);

    private static (string Id, string Name, Guid? ContainerId) GetIdentity(
        DisplayNative.LUID adapterId, uint targetId)
    {
        var request = new DisplayNative.TARGET_DEVICE_NAME
        {
            header = new DisplayNative.DEVICE_INFO_HEADER
            {
                type = 2,
                size = (uint)Marshal.SizeOf<DisplayNative.TARGET_DEVICE_NAME>(),
                adapterId = adapterId,
                id = targetId
            }
        };
        if (DisplayNative.DisplayConfigGetDeviceInfo(ref request) != 0)
            return (string.Empty, string.Empty, null);

        string name = string.IsNullOrWhiteSpace(request.monitorFriendlyDeviceName)
            ? $"Дисплей {targetId + 1}"
            : request.monitorFriendlyDeviceName;
        string path = request.monitorDevicePath ?? string.Empty;
        return (path, name, DeviceIdentityService.GetContainerId(path));
    }
}
