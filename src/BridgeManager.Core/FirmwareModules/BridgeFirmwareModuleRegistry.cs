namespace BridgeManager.Core.FirmwareModules;

public sealed class BridgeFirmwareModuleRegistry
{
    public const int RuntimeApiVersion = 2;
    private readonly IReadOnlyList<IBridgeFirmwareModule> _modules;

    public BridgeFirmwareModuleRegistry(
        IEnumerable<IBridgeFirmwareModule>? modules = null,
        IEnumerable<string>? moduleDirectories = null)
    {
        var selected = (modules ?? CreateBuiltIns()).ToDictionary(
            module => module.Id, StringComparer.OrdinalIgnoreCase);
        var issues = new List<BridgeModuleLoadIssue>();
        foreach (var directory in moduleDirectories ?? [])
        {
            BridgeModulePackageInstaller.RecoverIncompleteInstallations(
                directory, issues);
            foreach (var module in BridgeModulePackageLoader.LoadDirectory(
                         directory, issues))
            {
                if (!selected.TryGetValue(module.Id, out var current) ||
                    IsSameOrNewer(module.ModuleVersion,
                                  current.ModuleVersion))
                {
                    selected[module.Id] = module;
                }
                else
                {
                    issues.Add(new BridgeModuleLoadIssue(directory,
                        $"Ignored older module {module.Id} " +
                        $"{module.ModuleVersion}; active version is " +
                        $"{current.ModuleVersion}."));
                }
            }
        }

        LoadIssues = issues;
        _modules = selected.Values
            .OrderByDescending(module => module.Priority)
            .ToArray();
        Fallback = _modules.OfType<GenericManagerFirmwareModule>().FirstOrDefault()
            ?? new GenericManagerFirmwareModule();
    }

    public IReadOnlyList<IBridgeFirmwareModule> Modules => _modules;
    public IReadOnlyList<BridgeModuleLoadIssue> LoadIssues { get; }
    public IBridgeFirmwareModule Fallback { get; }

    public IBridgeFirmwareModule Resolve(DeviceDescriptor descriptor) =>
        _modules.FirstOrDefault(module => module.Id != Fallback.Id &&
                                         module.Matches(descriptor)) ?? Fallback;

    public IBridgeFirmwareModule? Find(string id) =>
        _modules.FirstOrDefault(module =>
            string.Equals(module.Id, id, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<BridgeBoardDefinition> GetBoards() =>
        _modules.SelectMany(module => module.Boards)
            .GroupBy(board => board.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(board => board.Family, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(board => board.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    public IReadOnlyList<(IBridgeFirmwareModule Module,
                          BridgeFirmwareDefinition Firmware)> GetFirmwareForBoard(
        string boardId) =>
        _modules
            .SelectMany(module => module.Firmware.Select(firmware => (module, firmware)))
            .Where(item => item.firmware.BoardIds.Contains(
                boardId, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(item => item.module.Priority)
            .Select(item => (item.module, item.firmware))
            .ToArray();

    public IReadOnlyList<BridgeDeviceProfile> GetHidDeviceProfiles() =>
        _modules.Where(module => module.Id != Fallback.Id)
            .SelectMany(module => module.HidDevices)
            .Concat(BridgeDeviceProfiles.All)
            .GroupBy(profile =>
                $"{profile.Key}:{profile.VendorId:x4}:{profile.ProductId:x4}:" +
                $"{profile.ManagerSerialNumber}:{profile.LegacyManagerProductName}:" +
                $"{profile.ManagerFeatureReportId}:{profile.CommandOutputReportId}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

    private static IEnumerable<IBridgeFirmwareModule> CreateBuiltIns()
    {
        yield return new Sf32UnifiedFirmwareModule();
        yield return new Esp32S3Ns2BridgeFirmwareModule();
        yield return new PicoUnifiedFirmwareModule();
        yield return new GenericManagerFirmwareModule();
    }

    private static bool IsSameOrNewer(string candidate, string current)
    {
        if (current.Equals("builtin", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!BridgeModuleVersion.IsValid(candidate))
        {
            return false;
        }
        return !BridgeModuleVersion.IsValid(current) ||
               BridgeModuleVersion.Compare(candidate, current) >= 0;
    }
}
