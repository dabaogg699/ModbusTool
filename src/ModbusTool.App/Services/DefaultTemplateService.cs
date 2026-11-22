// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.IO;
using System.Reflection;
using ModbusMaster;

namespace ModbusTool.App.Services;

/// <summary>
/// 内置出厂默认模板（只读，随 exe 嵌入分发）与 last.mbwork 的加载 / 保存。
/// 启动优先恢复上次工作区；不存在或损坏时回退到内置默认模板。
/// </summary>
public static class DefaultTemplateService
{
    private const string ResourceName = "ModbusTool.App.Assets.Templates.default-template.mbwork";

    /// <summary>从嵌入资源读取内置默认模板（每次返回独立副本）。</summary>
    public static async ValueTask<MasterWorkspace> LoadBuiltInAsync(CancellationToken cancellationToken = default)
    {
        Assembly assembly = typeof(DefaultTemplateService).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("内置默认模板资源缺失：" + ResourceName);
        MasterWorkspace workspace = await ModbusMaster.MasterWorkspaceManager
            .DeserializeWorkspaceAsync(stream, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("内置默认模板解析失败。");
        return workspace;
    }

    /// <summary>
    /// 启动加载：存在 last.mbwork 则加载上次工作区（返回其路径），否则加载内置默认模板（返回 null）。
    /// last.mbwork 损坏时返回内置模板，原文件重命名保留待排查。
    /// </summary>
    public static async ValueTask<(MasterWorkspace Workspace, string? LoadedPath)> LoadStartupAsync(
        MasterWorkspaceManager manager,
        CancellationToken cancellationToken = default)
    {
        string lastPath = AppDataPathHelper.LastWorkspaceFile;
        if (File.Exists(lastPath))
        {
            try
            {
                await manager.LoadAsync(lastPath, cancellationToken).ConfigureAwait(false);
                return (manager.Current, lastPath);
            }
            catch (Exception)
            {
                // 损坏的 last.mbwork 不阻断启动：改名保留现场，回退出厂模板。
                try
                {
                    string backup = lastPath + ".corrupt-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    File.Move(lastPath, backup);
                }
                catch (Exception)
                {
                    // 保留失败可忽略，继续回退。
                }
            }
        }

        MasterWorkspace builtIn = await LoadBuiltInAsync(cancellationToken).ConfigureAwait(false);
        manager.LoadModel(builtIn, markDirty: false);
        return (builtIn, null);
    }

    /// <summary>退出时把当前工作区另存为 last.mbwork；失败不影响退出。</summary>
    public static async ValueTask SaveLastAsync(MasterWorkspaceManager manager)
    {
        try
        {
            Directory.CreateDirectory(AppDataPathHelper.WorkspacesFolder);
            await manager.WriteSnapshotAsync(AppDataPathHelper.LastWorkspaceFile).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 退出保存失败静默忽略，避免影响正常关闭。
        }
    }
}
