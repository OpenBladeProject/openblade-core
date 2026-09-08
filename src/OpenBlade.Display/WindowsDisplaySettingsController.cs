using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenBlade.Display;

public sealed class WindowsDisplaySettingsController : IDisplaySettingsController, IDisposable
{
    private const int MaximumDisplayConfigurationRetries = 5;
    private static readonly string[] RazerPresetProfileFiles =
    [
        "Native_CTR.icm",
        "DisplayP3_CTR.icm",
        "sRGB_CTR.icm",
        "BT709_CTR.icm",
        "AdobeRGB_CTR.icm",
    ];

    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly Func<string, CancellationToken, PreparedDisplayMutation> prepareRefresh;
    private readonly Func<bool, CancellationToken, PreparedDisplayMutation> prepareHdr;
    private readonly Func<string, CancellationToken, PreparedDisplayMutation> prepareColorProfile;
    private bool disposed;

    public WindowsDisplaySettingsController()
        : this(PrepareRefresh, PrepareHdr, PrepareColorProfile)
    {
    }

    internal WindowsDisplaySettingsController(
        Func<string, CancellationToken, PreparedDisplayMutation> prepareRefresh,
        Func<bool, CancellationToken, PreparedDisplayMutation> prepareHdr,
        Func<string, CancellationToken, PreparedDisplayMutation> prepareColorProfile)
    {
        this.prepareRefresh = prepareRefresh ?? throw new ArgumentNullException(nameof(prepareRefresh));
        this.prepareHdr = prepareHdr ?? throw new ArgumentNullException(nameof(prepareHdr));
        this.prepareColorProfile = prepareColorProfile ?? throw new ArgumentNullException(nameof(prepareColorProfile));
    }

    public async Task<DisplaySettingsSnapshot> QueryAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunNativeAsync(
                () => QueryCore(cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Unavailable(exception.Message);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public Task<DisplayOperationResult> SetRefreshAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return RunMutationAsync(token => prepareRefresh(key, token), cancellationToken);
    }

    public Task<DisplayOperationResult> SetHdrAsync(
        bool enabled,
        CancellationToken cancellationToken = default) =>
        RunMutationAsync(token => prepareHdr(enabled, token), cancellationToken);

    public Task<DisplayOperationResult> SetColorProfileAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return RunMutationAsync(token => prepareColorProfile(id, token), cancellationToken);
    }

    private async Task<DisplayOperationResult> RunMutationAsync(
        Func<CancellationToken, PreparedDisplayMutation> prepare,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunNativeAsync(() =>
            {
                PreparedDisplayMutation mutation = prepare(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                // Native submission is not interruptible. Once it starts, retain
                // ownership through readback and any restoration, even if the
                // caller's deadline or power transition has been canceled.
                return mutation.Execute();
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private static PreparedDisplayMutation PrepareRefresh(string key, CancellationToken cancellationToken)
    {
        DisplayTopology original = QueryTopology();
        DisplayTarget target = GetSingleInternalTarget(original);
        DisplaySettingsSnapshot before = BuildSnapshot(original, target, cancellationToken);
        DisplayRefreshOption? requested = before.RefreshOptions.FirstOrDefault(option => option.Key == key);
        if (requested is null)
        {
            return new(() => DisplayOperationResult.Failure("That refresh-rate option is not available for the internal display.", before));
        }
        if (string.Equals(before.CurrentRefreshKey, key, StringComparison.Ordinal))
        {
            return new(() => DisplayOperationResult.Success(before));
        }
        return PrepareRefreshAfterSnapshot(before, () =>
        {
            DisplayTopology requestedTopology = original.Copy();
            DisplayTarget requestedTarget = GetSingleInternalTarget(requestedTopology);
            PrepareBoostState(requestedTopology, requestedTarget, requested.IsDynamic, requested.DesktopHertz);
            return new(() =>
            {
                try
                {
                    ApplyTopology(requestedTopology);
                    DisplaySettingsSnapshot after = QueryCore(CancellationToken.None);
                    if (!string.Equals(after.CurrentRefreshKey, key, StringComparison.Ordinal))
                    {
                        TryRestoreTopology(original);
                        return DisplayOperationResult.Failure(
                            "Windows did not confirm the requested refresh-rate mode, so OpenBlade attempted to restore the previous display configuration.",
                            QueryCore(CancellationToken.None));
                    }
                    return DisplayOperationResult.Success(after);
                }
                catch (Exception exception)
                {
                    TryRestoreTopology(original);
                    return DisplayOperationResult.Failure(
                        $"The refresh-rate change failed and OpenBlade attempted to restore the previous display configuration: {exception.Message}",
                        TryQuerySnapshot(CancellationToken.None));
                }
            });
        });
    }

    internal static PreparedDisplayMutation PrepareRefreshAfterSnapshot(
        DisplaySettingsSnapshot before,
        Func<PreparedDisplayMutation> prepare)
    {
        try
        {
            return prepare();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Validation is read-only. Preserve the typed failure and known
            // snapshot without submitting a restoration for an unapplied change.
            return new(() => DisplayOperationResult.Failure(
                $"The refresh-rate configuration could not be prepared: {exception.Message}",
                before));
        }
    }

    private static PreparedDisplayMutation PrepareHdr(bool enabled, CancellationToken cancellationToken)
    {
        try
        {
            return PrepareHdrCore(enabled, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Preserve the controller's typed HDR failure/reconciliation contract
            // when native preparation fails before a mutation is submitted.
            return new(() => ResolveHdrMutationResult(
                enabled, exception.Message, TryQuerySnapshot(CancellationToken.None)));
        }
    }

    private static PreparedDisplayMutation PrepareHdrCore(bool enabled, CancellationToken cancellationToken)
    {
        DisplayTopology topology = QueryTopology();
        DisplayTarget target = GetSingleInternalTarget(topology);
        AdvancedColorState current = QueryAdvancedColor(target);
        DisplaySettingsSnapshot before = BuildSnapshot(topology, target, cancellationToken);
        if (!current.HdrSupported)
        {
            return new(() => DisplayOperationResult.Failure("HDR is not supported for the active internal display.", before));
        }
        if (current.HdrEnabled == enabled)
        {
            return new(() => DisplayOperationResult.Success(before));
        }
        DisplayConfigSetHdrState request = new()
        {
            Header = CreateHeader(
                WindowsDisplayNative.DisplayConfigDeviceInfoSetHdrState,
                target.Path.TargetInfo.AdapterId,
                target.Path.TargetInfo.Id,
                Marshal.SizeOf<DisplayConfigSetHdrState>()),
            Value = enabled ? 1u : 0u,
        };
        return new(() =>
        {
            try
            {
                int result = WindowsDisplayNative.DisplayConfigSetHdrState(ref request);
                ThrowIfWin32Error(result, "Windows could not change HDR");
                DisplaySettingsSnapshot after = QueryCore(CancellationToken.None);
                return after.HdrEnabled == enabled
                    ? DisplayOperationResult.Success(after)
                    : DisplayOperationResult.Failure("Windows did not confirm the requested HDR state.", after);
            }
            catch (Exception exception)
            {
                return ResolveHdrMutationResult(enabled, exception.Message, TryQuerySnapshot(CancellationToken.None));
            }
        });
    }

    internal static DisplayOperationResult ResolveHdrMutationResult(
        bool requestedEnabled,
        string failure,
        DisplaySettingsSnapshot? observedState) =>
        observedState is
        {
            IsAvailable: true,
            HdrSupported: true,
            HdrEnabled: var observedEnabled,
        }
        && observedEnabled == requestedEnabled
            ? DisplayOperationResult.Success(observedState)
            : DisplayOperationResult.Failure(failure, observedState);

    private static PreparedDisplayMutation PrepareColorProfile(string id, CancellationToken cancellationToken)
    {
        DisplayTopology topology = QueryTopology();
        DisplayTarget target = GetSingleInternalTarget(topology);
        DisplaySettingsSnapshot state = BuildSnapshot(topology, target, cancellationToken);
        if (!state.ColorProfilesEnabled)
        {
            return new(() => DisplayOperationResult.Failure("Color profiles are unavailable while HDR is on.", state));
        }
        DisplayColorProfile? requested = state.ColorProfiles.FirstOrDefault(profile => ProfilesMatch(profile.Id, id));
        if (requested is null)
        {
            return new(() => DisplayOperationResult.Failure("That color profile is not available for the internal display.", state));
        }
        if (ProfilesMatch(requested.Id, state.ActiveColorProfileId))
        {
            return new(() => DisplayOperationResult.Success(state));
        }
        uint originalScope = GetSelectedColorProfileScope(target);
        string? previous = state.ActiveColorProfileId;
        return new(() =>
        {
            try
            {
                if (originalScope != WindowsDisplayNative.CurrentUserScope)
                {
                    SetUsePerUserProfiles(target, true);
                }
                // Scope and default-profile writes form one transaction. Do not
                // leave a changed scope behind when cancellation arrives between them.
                SetDefaultStandardColorProfile(target, requested.Id);
                DisplaySettingsSnapshot after = QueryCore(CancellationToken.None);
                if (!ProfilesMatch(after.ActiveColorProfileId ?? string.Empty, requested.Id))
                {
                    TryRestoreColorProfile(target, previous, originalScope);
                    return DisplayOperationResult.Failure(
                        "Windows did not confirm the requested color profile, so OpenBlade attempted to restore the previous profile.",
                        QueryCore(CancellationToken.None));
                }
                return DisplayOperationResult.Success(after);
            }
            catch (Exception exception)
            {
                TryRestoreColorProfile(target, previous, originalScope);
                return DisplayOperationResult.Failure(
                    $"The color-profile change failed and OpenBlade attempted to restore the previous profile: {exception.Message}",
                    TryQuerySnapshot(CancellationToken.None));
            }
        });
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        operationGate.Dispose();
    }

    internal static Task<T> RunNativeAsync<T>(
        Func<T> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return Task.Run(operation, cancellationToken);
    }

    private static DisplaySettingsSnapshot QueryCore(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DisplayTopology topology = QueryTopology();
        DisplayTarget target = GetSingleInternalTarget(topology);
        return BuildSnapshot(topology, target, cancellationToken);
    }

    private static DisplaySettingsSnapshot BuildSnapshot(
        DisplayTopology topology,
        DisplayTarget target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DevMode currentMode = GetCurrentMode(target.SourceName);
        double physicalHertz = GetPhysicalRefresh(topology, target);
        IReadOnlyList<DisplayRefreshOption> refreshOptions = BuildRefreshOptions(
            target,
            checked((int)currentMode.PelsWidth),
            checked((int)currentMode.PelsHeight),
            checked((int)currentMode.DisplayFrequency),
            physicalHertz);
        double virtualHertz = target.Path.TargetInfo.RefreshRate.Hertz;
        string? currentRefreshKey = RefreshRateClassifier.Classify(
            target.Path.Flags,
            virtualHertz,
            physicalHertz,
            refreshOptions);
        AdvancedColorState advancedColor = QueryAdvancedColor(target);
        uint profileScope = GetSelectedColorProfileScope(target);
        IReadOnlyList<string> associatedProfiles = GetAssociatedColorProfiles(target, profileScope);
        string? defaultProfile = advancedColor.HdrEnabled
            ? null
            : GetDefaultStandardColorProfile(target, profileScope);
        string[] uniqueProfiles = associatedProfiles
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.FirstOrDefault(profile => ProfilesMatch(profile, defaultProfile))
                ?? group.First())
            .ToArray();
        string? activeProfile = uniqueProfiles.FirstOrDefault(
            profile => ProfilesMatch(profile, defaultProfile));
        IReadOnlyList<DisplayColorProfile> profiles = uniqueProfiles
            .Select(profile => new DisplayColorProfile(
                profile,
                GetFriendlyProfileName(profile),
                ProfilesMatch(profile, activeProfile)))
            .OrderBy(profile => ProfileSortOrder(profile.Name))
            .ThenBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        return new DisplaySettingsSnapshot(
            true,
            target.FriendlyName,
            checked((int)currentMode.PelsWidth),
            checked((int)currentMode.PelsHeight),
            currentRefreshKey,
            refreshOptions,
            advancedColor.HdrSupported,
            advancedColor.HdrEnabled,
            !advancedColor.HdrEnabled,
            activeProfile,
            profiles,
            currentRefreshKey is null
                ? "The current refresh-rate mode was not reported as available by Windows."
                : null,
            advancedColor.BitsPerColorChannel);
    }

    private static DisplayTopology QueryTopology()
    {
        const uint flags = WindowsDisplayNative.QdcOnlyActivePaths |
            WindowsDisplayNative.QdcVirtualModeAware |
            WindowsDisplayNative.QdcVirtualRefreshRateAware;

        for (int attempt = 0; attempt < MaximumDisplayConfigurationRetries; attempt++)
        {
            int sizeResult = WindowsDisplayNative.GetDisplayConfigBufferSizes(flags, out uint pathCount, out uint modeCount);
            ThrowIfWin32Error(sizeResult, "Windows could not size the active display configuration");

            DisplayConfigPathInfo[] paths = new DisplayConfigPathInfo[pathCount];
            DisplayConfigModeInfo[] modes = new DisplayConfigModeInfo[modeCount];
            int queryResult = WindowsDisplayNative.QueryDisplayConfig(
                flags,
                ref pathCount,
                paths,
                ref modeCount,
                modes,
                IntPtr.Zero);

            if (queryResult == WindowsDisplayNative.ErrorInsufficientBuffer)
            {
                continue;
            }

            ThrowIfWin32Error(queryResult, "Windows could not read the active display configuration");
            Array.Resize(ref paths, checked((int)pathCount));
            Array.Resize(ref modes, checked((int)modeCount));
            return new DisplayTopology(paths, modes);
        }

        throw new InvalidOperationException("The display configuration kept changing while OpenBlade was reading it.");
    }

    private static DisplayTarget GetSingleInternalTarget(DisplayTopology topology)
    {
        DisplayConfigPathInfo[] internalPaths = topology.Paths
            .Where(path =>
                (path.Flags & WindowsDisplayNative.DisplayConfigPathActive) != 0 &&
                IsInternalOutputTechnology(path.TargetInfo.OutputTechnology))
            .ToArray();

        if (internalPaths.Length != 1)
        {
            throw new InvalidOperationException(
                internalPaths.Length == 0
                    ? "No active internal laptop display was found."
                    : "More than one active internal display was found, so OpenBlade did not choose one automatically.");
        }

        DisplayConfigPathInfo path = internalPaths[0];
        string sourceName = GetSourceName(path);
        DisplayTargetIdentity identity = GetTargetIdentity(path);
        return new DisplayTarget(path, sourceName, identity.FriendlyName, identity.MonitorDevicePath);
    }

    private static string GetSourceName(DisplayConfigPathInfo path)
    {
        DisplayConfigSourceDeviceName request = new()
        {
            Header = CreateHeader(
                WindowsDisplayNative.DisplayConfigDeviceInfoGetSourceName,
                path.SourceInfo.AdapterId,
                path.SourceInfo.Id,
                Marshal.SizeOf<DisplayConfigSourceDeviceName>()),
            ViewGdiDeviceName = string.Empty,
        };

        int result = WindowsDisplayNative.DisplayConfigGetSourceName(ref request);
        ThrowIfWin32Error(result, "Windows could not identify the internal display source");
        return request.ViewGdiDeviceName.TrimEnd('\0');
    }

    private static DisplayTargetIdentity GetTargetIdentity(DisplayConfigPathInfo path)
    {
        DisplayConfigTargetDeviceName request = new()
        {
            Header = CreateHeader(
                WindowsDisplayNative.DisplayConfigDeviceInfoGetTargetName,
                path.TargetInfo.AdapterId,
                path.TargetInfo.Id,
                Marshal.SizeOf<DisplayConfigTargetDeviceName>()),
            MonitorFriendlyDeviceName = string.Empty,
            MonitorDevicePath = string.Empty,
        };

        int result = WindowsDisplayNative.DisplayConfigGetTargetName(ref request);
        if (result != WindowsDisplayNative.ErrorSuccess)
        {
            return new DisplayTargetIdentity("Internal display", null);
        }

        string friendlyName = !string.IsNullOrWhiteSpace(request.MonitorFriendlyDeviceName)
            ? request.MonitorFriendlyDeviceName.TrimEnd('\0')
            : "Internal display";
        string? monitorDevicePath = !string.IsNullOrWhiteSpace(request.MonitorDevicePath)
            ? request.MonitorDevicePath.TrimEnd('\0')
            : null;
        return new DisplayTargetIdentity(friendlyName, monitorDevicePath);
    }

    private static AdvancedColorState QueryAdvancedColor(DisplayTarget target)
    {
        DisplayConfigGetAdvancedColorInfo2 request = new()
        {
            Header = CreateHeader(
                WindowsDisplayNative.DisplayConfigDeviceInfoGetAdvancedColorInfo2,
                target.Path.TargetInfo.AdapterId,
                target.Path.TargetInfo.Id,
                Marshal.SizeOf<DisplayConfigGetAdvancedColorInfo2>()),
        };

        int result = WindowsDisplayNative.DisplayConfigGetAdvancedColorInfo2(ref request);
        if (result != WindowsDisplayNative.ErrorSuccess)
        {
            return new AdvancedColorState(false, false, null);
        }

        bool supported = AdvancedColorBits.IsHdrSupported(request.Value);
        bool userEnabled = AdvancedColorBits.IsHdrUserEnabled(request.Value);
        int? bitsPerColorChannel = request.BitsPerColorChannel > 0
            ? checked((int)request.BitsPerColorChannel)
            : null;
        return new AdvancedColorState(supported, supported && userEnabled, bitsPerColorChannel);
    }

    private static IReadOnlyList<string> GetAssociatedColorProfiles(DisplayTarget target, uint scope)
    {
        IntPtr profileList = IntPtr.Zero;
        List<string> profiles = [];
        try
        {
            int result = WindowsDisplayNative.ColorProfileGetDisplayList(
                scope,
                target.Path.SourceInfo.AdapterId,
                target.Path.SourceInfo.Id,
                out profileList,
                out uint profileCount);
            if (result >= 0 && profileList != IntPtr.Zero)
            {
                for (int index = 0; index < profileCount; index++)
                {
                    IntPtr profilePointer = Marshal.ReadIntPtr(
                        profileList,
                        checked(index * IntPtr.Size));
                    string? profile = Marshal.PtrToStringUni(profilePointer);
                    if (!string.IsNullOrWhiteSpace(profile))
                    {
                        profiles.Add(profile);
                    }
                }
            }

            string colorDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "spool",
                "drivers",
                "color");
            foreach (string fileName in RazerPresetProfileFiles)
            {
                string path = Path.Combine(colorDirectory, fileName);
                if (IsValidMonitorRgbIcc(path))
                {
                    profiles.Add(path);
                }
            }

            return profiles;
        }
        finally
        {
            if (profileList != IntPtr.Zero)
            {
                _ = WindowsDisplayNative.LocalFree(profileList);
            }
        }
    }

    private static string? GetDefaultStandardColorProfile(DisplayTarget target, uint scope)
    {
        IntPtr profileName = IntPtr.Zero;
        try
        {
            int result = WindowsDisplayNative.ColorProfileGetDisplayDefault(
                scope,
                target.Path.SourceInfo.AdapterId,
                target.Path.SourceInfo.Id,
                WindowsDisplayNative.ColorProfileTypeIcc,
                WindowsDisplayNative.ColorProfileSubtypeStandardDisplay,
                out profileName);
            return result >= 0 && profileName != IntPtr.Zero ? Marshal.PtrToStringUni(profileName) : null;
        }
        finally
        {
            if (profileName != IntPtr.Zero)
            {
                _ = WindowsDisplayNative.LocalFree(profileName);
            }
        }
    }

    private static void SetDefaultStandardColorProfile(DisplayTarget target, string profile)
    {
        int associationResult = WindowsDisplayNative.ColorProfileAddDisplayAssociation(
            WindowsDisplayNative.CurrentUserScope,
            profile,
            target.Path.SourceInfo.AdapterId,
            target.Path.SourceInfo.Id,
            setAsDefault: 1,
            associateAsAdvancedColor: 0);
        ThrowIfHResultFailed(associationResult, "Windows could not associate the color profile");

        int defaultResult = WindowsDisplayNative.ColorProfileSetDisplayDefaultAssociation(
            WindowsDisplayNative.CurrentUserScope,
            profile,
            WindowsDisplayNative.ColorProfileTypeIcc,
            WindowsDisplayNative.ColorProfileSubtypeStandardDisplay,
            target.Path.SourceInfo.AdapterId,
            target.Path.SourceInfo.Id);
        ThrowIfHResultFailed(defaultResult, "Windows could not make the color profile active");
    }

    private static uint GetSelectedColorProfileScope(DisplayTarget target)
    {
        int result = WindowsDisplayNative.ColorProfileGetDisplayUserScope(
            target.Path.SourceInfo.AdapterId,
            target.Path.SourceInfo.Id,
            out uint scope);
        ThrowIfHResultFailed(result, "Windows could not determine the active color-profile scope");
        return scope;
    }

    private static void SetUsePerUserProfiles(DisplayTarget target, bool enabled)
    {
        if (!WindowsDisplayNative.WcsSetUsePerUserProfiles(
                target.SourceName,
                WindowsDisplayNative.ClassMonitor,
                enabled))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows could not change the color-profile scope");
        }

        uint expected = enabled
            ? WindowsDisplayNative.CurrentUserScope
            : WindowsDisplayNative.SystemWideScope;
        if (GetSelectedColorProfileScope(target) != expected)
        {
            throw new InvalidOperationException("Windows did not confirm the requested color-profile scope.");
        }
    }

    private static void TryRestoreColorProfile(
        DisplayTarget target,
        string? previous,
        uint originalScope)
    {
        try
        {
            if (previous is not null)
            {
                SetDefaultStandardColorProfile(target, previous);
            }

            if (originalScope == WindowsDisplayNative.SystemWideScope)
            {
                SetUsePerUserProfiles(target, false);
            }
        }
        catch
        {
        }
    }

    private static bool IsValidMonitorRgbIcc(string path)
    {
        try
        {
            byte[] header = new byte[128];
            using FileStream stream = File.OpenRead(path);
            stream.ReadExactly(header);

            return header.AsSpan(12, 4).SequenceEqual("mntr"u8)
                && header.AsSpan(16, 4).SequenceEqual("RGB "u8)
                && header.AsSpan(36, 4).SequenceEqual("acsp"u8);
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<DisplayRefreshOption> BuildRefreshOptions(
        DisplayTarget target,
        int width,
        int height,
        int currentHertz,
        double physicalHertz)
    {
        int physical = RefreshRateClassifier.Normalize(physicalHertz);
        int[] driverRates = EnumerateFixedRefreshRates(target.SourceName, width, height);
        byte[] edid = [];
        int[] advertisedRates = [];
        if (target.MonitorDevicePath is not null && MonitorEdidReader.TryRead(target.MonitorDevicePath, out edid))
        {
            advertisedRates = EdidDetailedTimingParser.Parse(edid)
                .Where(timing => timing.Width == width && timing.Height == height)
                .Select(timing => timing.Hertz)
                .Distinct()
                .Order()
                .ToArray();
        }

        int[] fixedRates = RefreshRateVisibilityFilter.SelectFixedRates(
            driverRates,
            advertisedRates,
            RefreshRateClassifier.Normalize(currentHertz),
            physical);
        List<DisplayRefreshOption> options = fixedRates
            .Select(hertz => new DisplayRefreshOption(
                DisplayRefreshKeys.Fixed(hertz),
                $"{hertz} Hz",
                hertz,
                hertz,
                hertz,
                false))
            .ToList();

        bool supportsVirtualMode =
            (target.Path.Flags & WindowsDisplayNative.DisplayConfigPathSupportVirtualMode) != 0;
        int desktopHertz = fixedRates.FirstOrDefault(hertz => hertz >= 60 && hertz < physical);
        if (supportsVirtualMode && desktopHertz > 0 && physical >= 120)
        {
            EdidVerticalRange range = new(desktopHertz, physical);
            if (edid.Length > 0 &&
                EdidVerticalRangeParser.TryParse(edid, out EdidVerticalRange reported) &&
                reported.MaximumHertz >= physical)
            {
                range = reported with { MaximumHertz = physical };
            }

            options.Add(new DisplayRefreshOption(
                DisplayRefreshKeys.Dynamic(range.MinimumHertz, range.MaximumHertz),
                $"Dynamic ({range.MinimumHertz}-{range.MaximumHertz} Hz)",
                range.MinimumHertz,
                range.MaximumHertz,
                desktopHertz,
                true));
        }

        return options;
    }

    private static int[] EnumerateFixedRefreshRates(string sourceName, int width, int height)
    {
        HashSet<int> rates = [];
        for (uint index = 0; ; index++)
        {
            DevMode mode = DevMode.Create();
            if (!WindowsDisplayNative.EnumDisplaySettingsEx(sourceName, index, ref mode, 0))
            {
                break;
            }

            if (mode.PelsWidth == width &&
                mode.PelsHeight == height &&
                mode.DisplayFrequency is >= 20 and <= 1000 &&
                (mode.DisplayFlags & WindowsDisplayNative.DmInterlaced) == 0)
            {
                rates.Add(checked((int)mode.DisplayFrequency));
            }
        }

        return rates.Order().ToArray();
    }

    private static DevMode GetCurrentMode(string sourceName)
    {
        DevMode mode = DevMode.Create();
        if (!WindowsDisplayNative.EnumDisplaySettingsEx(
                sourceName,
                WindowsDisplayNative.EnumCurrentSettings,
                ref mode,
                0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not read the internal display mode");
        }

        return mode;
    }

    private static double GetPhysicalRefresh(DisplayTopology topology, DisplayTarget target)
    {
        uint modeIndex = GetTargetModeIndex(target.Path);
        if (modeIndex >= topology.Modes.Length)
        {
            throw new InvalidOperationException("Windows returned an invalid physical refresh-rate mode index.");
        }

        DisplayConfigModeInfo mode = topology.Modes[modeIndex];
        if (mode.InfoType != 2 ||
            mode.Id != target.Path.TargetInfo.Id ||
            !mode.AdapterId.Equals(target.Path.TargetInfo.AdapterId))
        {
            throw new InvalidOperationException("Windows returned a mismatched physical refresh-rate mode.");
        }

        return mode.Mode.TargetMode.TargetVideoSignalInfo.VSyncFreq.Hertz;
    }

    private static uint GetTargetModeIndex(DisplayConfigPathInfo path)
    {
        const uint invalid16 = 0xFFFF;
        const uint invalid32 = 0xFFFFFFFF;
        bool virtualMode = (path.Flags & 0x00000008) != 0;
        uint index = virtualMode ? (path.TargetInfo.ModeInfoIdx >> 16) & invalid16 : path.TargetInfo.ModeInfoIdx;
        if (index == invalid16 || index == invalid32)
        {
            throw new InvalidOperationException("The internal display does not have an active physical mode.");
        }

        return index;
    }

    private static void PrepareBoostState(
        DisplayTopology topology,
        DisplayTarget target,
        bool enabled,
        int virtualHertz)
    {
        int pathIndex = Array.FindIndex(
            topology.Paths,
            path => path.SourceInfo.AdapterId.Equals(target.Path.SourceInfo.AdapterId) &&
                path.SourceInfo.Id == target.Path.SourceInfo.Id &&
                path.TargetInfo.AdapterId.Equals(target.Path.TargetInfo.AdapterId) &&
                path.TargetInfo.Id == target.Path.TargetInfo.Id);
        if (pathIndex < 0)
        {
            throw new InvalidOperationException("The internal display path changed before the refresh-rate setting could be applied.");
        }

        DisplayConfigPathInfo path = topology.Paths[pathIndex];
        path.TargetInfo.RefreshRate = DisplayConfigRational.FromHertz(checked((uint)virtualHertz));
        if (enabled)
        {
            path.Flags |= WindowsDisplayNative.DisplayConfigPathBoostRefreshRate;
        }
        else
        {
            path.Flags &= ~WindowsDisplayNative.DisplayConfigPathBoostRefreshRate;
        }

        topology.Paths[pathIndex] = path;
        ValidateTopology(topology);
    }

    private static void ValidateTopology(DisplayTopology topology)
    {
        const uint commonFlags = WindowsDisplayNative.SdcUseSuppliedDisplayConfig |
            WindowsDisplayNative.SdcVirtualModeAware |
            WindowsDisplayNative.SdcVirtualRefreshRateAware;
        int validationResult = WindowsDisplayNative.SetDisplayConfig(
            checked((uint)topology.Paths.Length),
            topology.Paths,
            checked((uint)topology.Modes.Length),
            topology.Modes,
            commonFlags | WindowsDisplayNative.SdcValidate);
        ThrowIfWin32Error(validationResult, "Windows rejected the requested display configuration");
    }

    private static void ApplyTopology(DisplayTopology topology)
    {
        const uint commonFlags = WindowsDisplayNative.SdcUseSuppliedDisplayConfig |
            WindowsDisplayNative.SdcVirtualModeAware |
            WindowsDisplayNative.SdcVirtualRefreshRateAware;
        int applyResult = WindowsDisplayNative.SetDisplayConfig(
            checked((uint)topology.Paths.Length),
            topology.Paths,
            checked((uint)topology.Modes.Length),
            topology.Modes,
            commonFlags | WindowsDisplayNative.SdcApply | WindowsDisplayNative.SdcSaveToDatabase);
        ThrowIfWin32Error(applyResult, "Windows could not apply the requested display configuration");
    }

    private static void TryRestoreTopology(DisplayTopology original)
    {
        try
        {
            ValidateTopology(original);
            ApplyTopology(original);
        }
        catch
        {
            // The original operation error remains more useful than a best-effort rollback error.
        }
    }

    private static DisplaySettingsSnapshot? TryQuerySnapshot(CancellationToken cancellationToken)
    {
        try
        {
            return QueryCore(cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private static DisplayConfigDeviceInfoHeader CreateHeader(
        uint type,
        DisplayConfigLuid adapterId,
        uint id,
        int size) => new()
        {
            Type = type,
            Size = checked((uint)size),
            AdapterId = adapterId,
            Id = id,
        };

    private static void ThrowIfWin32Error(int result, string operation)
    {
        if (result != WindowsDisplayNative.ErrorSuccess)
        {
            throw new Win32Exception(result, $"{operation}: {new Win32Exception(result).Message}");
        }
    }

    private static void ThrowIfHResultFailed(int result, string operation)
    {
        if (result < 0)
        {
            throw new COMException(operation, result);
        }
    }

    private static string GetFriendlyProfileName(string profile)
    {
        string name = Path.GetFileNameWithoutExtension(profile);
        string normalized = name.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();

        if (normalized.Contains("DCIP3", StringComparison.Ordinal)
            || normalized.Contains("DISPLAYP3", StringComparison.Ordinal))
        {
            return "DCI-P3";
        }

        if (normalized.Contains("SRGB", StringComparison.Ordinal))
        {
            return "sRGB";
        }

        if (normalized.Contains("REC709", StringComparison.Ordinal)
            || normalized.Contains("BT709", StringComparison.Ordinal))
        {
            return "Rec. 709";
        }

        if (normalized.Contains("ADOBERGB", StringComparison.Ordinal))
        {
            return "Adobe RGB";
        }

        if (normalized.Contains("NATIVE", StringComparison.Ordinal))
        {
            return "Native";
        }

        if (normalized.Equals("BLADE", StringComparison.Ordinal))
        {
            return "Factory calibration";
        }

        return name;
    }

    private static int ProfileSortOrder(string name) => name switch
    {
        "Native" => 0,
        "DCI-P3" => 1,
        "sRGB" => 2,
        "Rec. 709" => 3,
        "Adobe RGB" => 4,
        _ => 5,
    };

    internal static bool IsInternalOutputTechnology(uint technology) => technology is
        WindowsDisplayNative.DisplayConfigOutputTechnologyLvds or
        WindowsDisplayNative.DisplayConfigOutputTechnologyDisplayPortEmbedded or
        WindowsDisplayNative.DisplayConfigOutputTechnologyUdiEmbedded or
        WindowsDisplayNative.DisplayConfigOutputTechnologyInternal;

    private static bool ProfilesMatch(string profile, string? other)
    {
        if (other is null)
        {
            return false;
        }

        return string.Equals(profile, other, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileName(profile), Path.GetFileName(other), StringComparison.OrdinalIgnoreCase);
    }

    private static DisplaySettingsSnapshot Unavailable(string message) => new(
        false,
        null,
        0,
        0,
        null,
        [],
        false,
        false,
        false,
        null,
        [],
        message);

    private sealed record DisplayTopology(DisplayConfigPathInfo[] Paths, DisplayConfigModeInfo[] Modes)
    {
        internal DisplayTopology Copy() => new((DisplayConfigPathInfo[])Paths.Clone(), (DisplayConfigModeInfo[])Modes.Clone());
    }

    private sealed record DisplayTarget(
        DisplayConfigPathInfo Path,
        string SourceName,
        string FriendlyName,
        string? MonitorDevicePath);

    private readonly record struct DisplayTargetIdentity(string FriendlyName, string? MonitorDevicePath);

    private readonly record struct AdvancedColorState(
        bool HdrSupported,
        bool HdrEnabled,
        int? BitsPerColorChannel);
}

/// <summary>
/// Read-only native preparation produces one submission/readback transaction.
/// Execute deliberately takes no cancellation token: ownership must continue
/// until native completion and reconciliation after submission has begun.
/// </summary>
internal sealed record PreparedDisplayMutation(Func<DisplayOperationResult> Execute);
