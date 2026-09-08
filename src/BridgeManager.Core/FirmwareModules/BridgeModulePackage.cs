using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BridgeManager.Core.FirmwareModules;

public sealed class BridgeModulePackageDefinition
{
    public int SchemaVersion { get; init; } = 1;
    public int RuntimeApiVersion { get; init; } = 1;
    public string Id { get; init; } = "";
    public string ModuleVersion { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string BoardFamily { get; init; } = "";
    public string Description { get; init; } = "";
    public int Priority { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BridgeCapability Capabilities { get; init; }
    public BridgeModuleMatchDefinition[] MatchAny { get; init; } = [];
    public BridgeBoardDefinition[] Boards { get; init; } = [];
    public BridgeFirmwareDefinition[] Firmware { get; init; } = [];
    public BridgeHidDeviceDefinition[] HidDevices { get; init; } = [];
    public BridgeUsbRoleOption[] UsbRoles { get; init; } = [];
    public BridgeInputSourceOption[] InputSources { get; init; } = [];
    public BridgeWirelessControllerOption[] WirelessControllers { get; init; } = [];
    public string[] StatusCommands { get; init; } = ["status"];
    public string[] SelfTestCommands { get; init; } = ["status"];
    public string PrimaryStatusCommand { get; init; } = "status";
    public string InputStatusCommand { get; init; } = "status";
    public string? SaveSettingsCommand { get; init; }
    public string[] SettingsCommandTemplates { get; init; } = [];
    public string? RumbleCommandTemplate { get; init; }
    public Dictionary<string, BridgeModuleOperationDefinition> Operations { get; init; } = [];
    public BridgeModulePageDefinition[] Pages { get; init; } = [];
}

public sealed class BridgeModuleMatchDefinition
{
    public string[] ProfileKeys { get; init; } = [];
    public string[] SerialNumbers { get; init; } = [];
    public string[] ExcludedSerialNumbers { get; init; } = [];
    public string[] PlatformContains { get; init; } = [];
    public BridgeUsbIdentity[] UsbIdentities { get; init; } = [];
}

public sealed record BridgeUsbIdentity(ushort VendorId, ushort ProductId);

public sealed class BridgeHidDeviceDefinition
{
    public string Key { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public ushort VendorId { get; init; }
    public ushort ProductId { get; init; }
    public ushort ManagerUsagePage { get; init; } = BridgeHidUsages.VendorDefinedPage;
    public ushort ManagerUsageId { get; init; } = BridgeHidUsages.Manager;
    public byte ManagerFeatureReportId { get; init; } = ManagerProtocol.FeatureReportId;
    public byte? CommandOutputReportId { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BridgeUsbRole UsbRole { get; init; }
    public string? ManagerSerialNumber { get; init; }
    public string? LegacyManagerProductName { get; init; }
    public string[] Tags { get; init; } = [];
    public bool SupportsInputReports { get; init; }

    public BridgeDeviceProfile ToProfile() => new(
        Key, DisplayName, VendorId, ProductId, ManagerUsagePage, ManagerUsageId,
        ManagerFeatureReportId, CommandOutputReportId, UsbRole,
        ManagerSerialNumber, LegacyManagerProductName, Tags)
        { SupportsInputReports = SupportsInputReports };
}

public sealed record BridgeModuleLoadIssue(string Path, string Message);

public sealed record BridgeModuleInstallResult(
    string ModuleId,
    string ModuleVersion,
    string InstallPath);

public sealed class ManifestFirmwareModule : IBridgeFirmwareModule
{
    private readonly BridgeModulePackageDefinition _definition;

    public ManifestFirmwareModule(BridgeModulePackageDefinition definition,
                                  string packageRoot)
    {
        _definition = definition;
        PackageRoot = packageRoot;
        HidDevices = definition.HidDevices.Select(device => device.ToProfile())
            .ToArray();
    }

    public string Id => _definition.Id;
    public string DisplayName => _definition.DisplayName;
    public string BoardFamily => _definition.BoardFamily;
    public string Description => _definition.Description;
    public string ModuleVersion => _definition.ModuleVersion;
    public int RuntimeApiVersion => _definition.RuntimeApiVersion;
    public string? PackageRoot { get; }
    public int Priority => _definition.Priority;
    public BridgeCapability Capabilities => _definition.Capabilities;
    public IReadOnlyList<BridgeBoardDefinition> Boards => _definition.Boards;
    public IReadOnlyList<BridgeFirmwareDefinition> Firmware => _definition.Firmware;
    public IReadOnlyList<BridgeDeviceProfile> HidDevices { get; }
    public IReadOnlyList<BridgeUsbRoleOption> UsbRoles => _definition.UsbRoles;
    public IReadOnlyList<BridgeInputSourceOption> InputSources => _definition.InputSources;
    public IReadOnlyList<BridgeWirelessControllerOption> WirelessControllers =>
        _definition.WirelessControllers;
    public IReadOnlyList<string> StatusCommands => _definition.StatusCommands;
    public IReadOnlyList<string> SelfTestCommands => _definition.SelfTestCommands;
    public string PrimaryStatusCommand => _definition.PrimaryStatusCommand;
    public string InputStatusCommand => _definition.InputStatusCommand;
    public string? SaveSettingsCommand => _definition.SaveSettingsCommand;
    public IReadOnlyDictionary<string, BridgeModuleOperationDefinition> Operations =>
        _definition.Operations;
    public IReadOnlyList<BridgeModulePageDefinition> Pages => _definition.Pages;

    public bool Matches(DeviceDescriptor descriptor)
    {
        return _definition.MatchAny.Any(match => Matches(match, descriptor));
    }

    private static bool Matches(BridgeModuleMatchDefinition match,
                                DeviceDescriptor descriptor)
    {
        if (match.ExcludedSerialNumbers.Contains(
                descriptor.SerialNumber ?? "", StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (match.SerialNumbers.Length > 0 &&
            !match.SerialNumbers.Contains(
                descriptor.SerialNumber ?? "", StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var hasSelector = match.ProfileKeys.Length > 0 ||
                          match.PlatformContains.Length > 0 ||
                          match.UsbIdentities.Length > 0;
        return hasSelector && (match.ProfileKeys.Contains(
                   descriptor.ProfileKey ?? "", StringComparer.OrdinalIgnoreCase) ||
               match.PlatformContains.Any(value => descriptor.Platform.Contains(
                   value, StringComparison.OrdinalIgnoreCase)) ||
               match.UsbIdentities.Any(identity =>
                   identity.VendorId == descriptor.VendorId &&
                   identity.ProductId == descriptor.ProductId));
    }

    public IReadOnlyList<string> BuildSettingsCommands(
        BridgeModuleSettings settings) =>
        _definition.SettingsCommandTemplates
            .Select(template => ExpandSettings(template, settings))
            .ToArray();

    public string? BuildRumbleCommand(string target) =>
        _definition.RumbleCommandTemplate?.Replace(
            "{target}", target, StringComparison.OrdinalIgnoreCase);

    private static string ExpandSettings(string template,
                                         BridgeModuleSettings settings) =>
        template
            .Replace("{reportRateHz}", settings.ReportRateHz.ToString(),
                     StringComparison.OrdinalIgnoreCase)
            .Replace("{usbRaw}", settings.UsbRawPassthrough ? "on" : "off",
                     StringComparison.OrdinalIgnoreCase)
            .Replace("{liveParsing}", settings.LiveParsing ? "on" : "off",
                     StringComparison.OrdinalIgnoreCase)
            .Replace("{rumbleScalePercent}", settings.RumbleScalePercent.ToString(),
                     StringComparison.OrdinalIgnoreCase)
            .Replace("{rumbleHoldMs}", settings.RumbleHoldMs.ToString(),
                     StringComparison.OrdinalIgnoreCase)
            .Replace("{rumbleTickMs}", settings.RumbleTickMs.ToString(),
                     StringComparison.OrdinalIgnoreCase)
            .Replace("{rumbleStopPackets}", settings.RumbleStopPackets.ToString(),
                     StringComparison.OrdinalIgnoreCase);
}

public static class BridgeModulePackageLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    public static IReadOnlyList<IBridgeFirmwareModule> LoadDirectory(
        string directory,
        ICollection<BridgeModuleLoadIssue>? issues = null)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var modules = new List<IBridgeFirmwareModule>();
        foreach (var path in Directory.EnumerateFiles(
                     directory, "*.json", SearchOption.AllDirectories)
                     .Where(path => IsModuleManifestPath(path) &&
                                    !IsInstallerWorkPath(directory, path)))
        {
            try
            {
                modules.Add(LoadFile(path));
            }
            catch (Exception ex) when (ex is IOException or
                                       UnauthorizedAccessException or
                                       JsonException or InvalidDataException)
            {
                issues?.Add(new BridgeModuleLoadIssue(path, ex.Message));
            }
        }
        return modules;
    }

    public static bool IsModuleManifestPath(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".bridge-module.json",
                   StringComparison.OrdinalIgnoreCase) ||
               name.Equals("module.json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInstallerWorkPath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Split(Path.DirectorySeparatorChar)
            .SkipLast(1)
            .Any(part => part.StartsWith(".staging-",
                            StringComparison.OrdinalIgnoreCase) ||
                         part.EndsWith(".previous",
                            StringComparison.OrdinalIgnoreCase));
    }

    public static IBridgeFirmwareModule LoadFile(string path)
    {
        var json = File.ReadAllText(path);
        var definition = JsonSerializer.Deserialize<BridgeModulePackageDefinition>(
            json, JsonOptions) ?? throw new InvalidDataException(
                "Module manifest is empty.");
        Validate(definition);
        return new ManifestFirmwareModule(definition,
            Path.GetDirectoryName(Path.GetFullPath(path))!);
    }

    public static void Validate(BridgeModulePackageDefinition definition)
    {
        if (definition.SchemaVersion != 1)
        {
            throw new InvalidDataException(
                $"Unsupported module schema {definition.SchemaVersion}.");
        }
        if (definition.RuntimeApiVersion >
            BridgeFirmwareModuleRegistry.RuntimeApiVersion)
        {
            throw new InvalidDataException(
                $"Module requires manager API {definition.RuntimeApiVersion}.");
        }
        if (definition.RuntimeApiVersion < 1)
        {
            throw new InvalidDataException(
                "Module runtimeApiVersion must be at least 1.");
        }
        if (string.IsNullOrWhiteSpace(definition.Id) ||
            definition.Id.Any(ch => !(char.IsAsciiLetterOrDigit(ch) ||
                                      ch is '-' or '_')))
        {
            throw new InvalidDataException("Module id is invalid.");
        }
        if (string.IsNullOrWhiteSpace(definition.DisplayName) ||
            string.IsNullOrWhiteSpace(definition.ModuleVersion))
        {
            throw new InvalidDataException(
                "Module display name and version are required.");
        }
        if (!BridgeModuleVersion.IsValid(definition.ModuleVersion))
        {
            throw new InvalidDataException(
                "Module version must use Semantic Versioning (for example 1.2.3)." );
        }
        if (definition.MatchAny is null || definition.Boards is null ||
            definition.Firmware is null || definition.HidDevices is null ||
            definition.UsbRoles is null ||
            definition.InputSources is null ||
            definition.WirelessControllers is null ||
            definition.StatusCommands is null ||
            definition.SelfTestCommands is null ||
            definition.SettingsCommandTemplates is null ||
            definition.MatchAny.Any(match => match is null ||
                match.ProfileKeys is null || match.SerialNumbers is null ||
                match.ExcludedSerialNumbers is null ||
                match.PlatformContains is null || match.UsbIdentities is null) ||
            definition.HidDevices.Any(device => device is null ||
                string.IsNullOrWhiteSpace(device.Key) ||
                string.IsNullOrWhiteSpace(device.DisplayName) ||
                device.Tags is null))
        {
            throw new InvalidDataException(
                "Module manifest collections must not be null.");
        }
        BridgeDynamicModuleValidator.Validate(definition);
    }
}

public static class BridgeModulePackageInstaller
{
    public static void RecoverIncompleteInstallations(
        string installRoot,
        ICollection<BridgeModuleLoadIssue>? issues = null)
    {
        if (!Directory.Exists(installRoot))
        {
            return;
        }
        foreach (var backup in Directory.EnumerateDirectories(
                     installRoot, "*.previous", SearchOption.TopDirectoryOnly))
        {
            var destination = backup[..^".previous".Length];
            try
            {
                if (Directory.Exists(destination))
                {
                    Directory.Delete(backup, recursive: true);
                }
                else
                {
                    Directory.Move(backup, destination);
                }
            }
            catch (Exception ex) when (ex is IOException or
                                       UnauthorizedAccessException)
            {
                issues?.Add(new BridgeModuleLoadIssue(backup,
                    $"Could not recover an interrupted module installation: {ex.Message}"));
            }
        }
    }

    public static BridgeModuleInstallResult Install(string packagePath,
                                                     string installRoot)
    {
        Directory.CreateDirectory(installRoot);
        var root = Path.GetFullPath(installRoot);
        var staging = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            if (archive.Entries.Count > 1024 ||
                archive.Entries.Sum(entry => entry.Length) > 512L * 1024L * 1024L)
            {
                throw new InvalidDataException(
                    "Module package exceeds the entry or uncompressed-size limit.");
            }
            foreach (var entry in archive.Entries)
            {
                var destination = Path.GetFullPath(Path.Combine(staging,
                    entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(staging + Path.DirectorySeparatorChar,
                                            StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "Module package contains an unsafe path.");
                }
                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }

            var manifests = Directory.EnumerateFiles(
                    staging, "*.json", SearchOption.AllDirectories)
                .Where(BridgeModulePackageLoader.IsModuleManifestPath)
                .ToArray();
            if (manifests.Length != 1 ||
                !Path.GetFullPath(manifests[0]).Equals(
                    Path.Combine(staging, "module.json"),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "A module package must contain one root module.json manifest.");
            }

            var contentRoot = Path.GetDirectoryName(manifests[0])!;
            VerifyPackageHashes(contentRoot);
            if (!File.Exists(Path.Combine(contentRoot, "LICENSE")))
            {
                throw new InvalidDataException(
                    "Module package is missing the required LICENSE file.");
            }
            var module = BridgeModulePackageLoader.LoadFile(manifests[0]);
            ValidatePackageArtifacts(module);
            var destinationRoot = Path.Combine(root, module.Id);
            var destinationFull = Path.GetFullPath(destinationRoot);
            if (!destinationFull.StartsWith(root + Path.DirectorySeparatorChar,
                                            StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Module destination is unsafe.");
            }

            var backup = destinationFull + ".previous";
            RecoverDestination(destinationFull, backup);
            if (Directory.Exists(destinationFull))
            {
                var installedManifests = Directory.EnumerateFiles(
                        destinationFull, "*.json", SearchOption.AllDirectories)
                    .Where(BridgeModulePackageLoader.IsModuleManifestPath)
                    .ToArray();
                if (installedManifests.Length != 1)
                {
                    throw new InvalidDataException(
                        "The installed module is incomplete; remove or repair it before updating.");
                }
                var installed = BridgeModulePackageLoader.LoadFile(
                    installedManifests[0]);
                if (!installed.Id.Equals(module.Id,
                        StringComparison.OrdinalIgnoreCase) ||
                    BridgeModuleVersion.Compare(module.ModuleVersion,
                                                installed.ModuleVersion) < 0)
                {
                    throw new InvalidDataException(
                        $"Refusing to downgrade {module.Id} from " +
                        $"{installed.ModuleVersion} to {module.ModuleVersion}.");
                }
            }
            var movedPrevious = false;
            try
            {
                if (Directory.Exists(destinationFull))
                {
                    Directory.Move(destinationFull, backup);
                    movedPrevious = true;
                }
                Directory.Move(contentRoot, destinationFull);
            }
            catch
            {
                if (movedPrevious && Directory.Exists(backup) &&
                    !Directory.Exists(destinationFull))
                {
                    Directory.Move(backup, destinationFull);
                }
                throw;
            }
            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }
            return new BridgeModuleInstallResult(
                module.Id, module.ModuleVersion, destinationFull);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static void RecoverDestination(string destination, string backup)
    {
        if (!Directory.Exists(backup))
        {
            return;
        }
        if (Directory.Exists(destination))
        {
            Directory.Delete(backup, recursive: true);
        }
        else
        {
            Directory.Move(backup, destination);
        }
    }

    private static void ValidatePackageArtifacts(IBridgeFirmwareModule module)
    {
        foreach (var firmware in module.Firmware)
        {
            if (firmware.FlashMethod == FirmwareFlashMethod.None &&
                string.IsNullOrWhiteSpace(firmware.ArtifactRelativePath))
            {
                continue;
            }
            var artifact = FirmwareFlashService.ResolveArtifact(module, firmware);
            if (artifact is null)
            {
                throw new InvalidDataException(
                    $"Firmware artifact is missing from the module: {firmware.Id}");
            }
            FirmwareFlashService.ValidateArtifactBundle(
                firmware.FlashMethod, artifact);
        }
    }

    private static void VerifyPackageHashes(string contentRoot)
    {
        var hashPath = Path.Combine(contentRoot, "MODULE-SHA256.txt");
        if (!File.Exists(hashPath))
        {
            throw new InvalidDataException(
                "Module package is missing MODULE-SHA256.txt.");
        }

        var root = Path.GetFullPath(contentRoot);
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) +
                     Path.DirectorySeparatorChar;
        var verified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(hashPath))
        {
            var separator = line.IndexOf("  ", StringComparison.Ordinal);
            if (separator != 64)
            {
                throw new InvalidDataException(
                    "Module hash manifest contains an invalid entry.");
            }
            var expected = line[..separator];
            var relative = line[(separator + 2)..].Replace(
                '/', Path.DirectorySeparatorChar);
            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(path))
            {
                throw new InvalidDataException(
                    $"Module hash entry is unsafe or missing: {relative}");
            }
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Module hash mismatch: {relative}");
            }
            verified.Add(Path.GetRelativePath(root, path).Replace(
                Path.DirectorySeparatorChar, '/'));
        }

        var unlisted = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !path.Equals(hashPath, StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(root, path).Replace(
                Path.DirectorySeparatorChar, '/'))
            .FirstOrDefault(relative => !verified.Contains(relative));
        if (unlisted is not null)
        {
            throw new InvalidDataException(
                $"Module file is not covered by the hash manifest: {unlisted}");
        }
    }
}
