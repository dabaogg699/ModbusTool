// © 2026 工控菜鸡刘 · ModbusTool · MIT License
namespace ModbusTool.Update;

public sealed record UpdateCheckResult(
    bool UpdateAvailable,
    Version CurrentVersion,
    Version LatestVersion,
    UpdateManifest Manifest,
    UpdatePackage Package,
    string RuntimeIdentifier);

public sealed record PreparedUpdate(
    UpdateCheckResult CheckResult,
    string WorkDirectory,
    string SourceDirectory,
    string PackagePath,
    string UpdaterPath);
