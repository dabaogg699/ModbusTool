// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Reflection;

namespace ModbusMaster.Tests;

/// <summary>
/// G14：用户数据迁移 %APPDATA% 与内置默认模板回归。
/// 内置模板以嵌入资源随 exe 分发，加载失败会导致首次启动空白，必须资源存在且可反序列化。
/// </summary>
public class AppDataAndDefaultTemplateTests
{
    [Fact]
    public void AppDataPaths_AreUnderRoamingModbusTool()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string expected = Path.Combine(root, "ModbusTool");

        Assert.StartsWith(expected, ModbusTool.App.Services.AppDataPathHelper.TemplatesFolder);
        Assert.StartsWith(expected, ModbusTool.App.Services.AppDataPathHelper.WorkspacesFolder);
        Assert.StartsWith(expected, ModbusTool.App.Services.AppDataPathHelper.RuntimeCacheFolder);
        Assert.StartsWith(expected, ModbusTool.App.Services.AppDataPathHelper.LogsFolder);
        Assert.Equal(
            Path.Combine(ModbusTool.App.Services.AppDataPathHelper.WorkspacesFolder, "last.mbwork"),
            ModbusTool.App.Services.AppDataPathHelper.LastWorkspaceFile);
    }

    [Fact]
    public async Task BuiltInDefaultTemplate_ResourceExistsAndDeserializes()
    {
        Assembly assembly = typeof(ModbusTool.App.Services.DefaultTemplateService).Assembly;
        string[] resources = assembly.GetManifestResourceNames();

        Assert.Contains("ModbusTool.App.Assets.Templates.default-template.mbwork", resources);

        ModbusMaster.MasterWorkspace workspace =
            await ModbusTool.App.Services.DefaultTemplateService.LoadBuiltInAsync();

        Assert.NotNull(workspace);
        Assert.Equal("默认模板", workspace.Name);
        Assert.NotEmpty(workspace.Devices);
        Assert.Equal(1, workspace.Devices[0].UnitId);
    }

    [Fact]
    public async Task BuiltInDefaultTemplate_DeserializationIsRepeatable()
    {
        ModbusMaster.MasterWorkspace first =
            await ModbusTool.App.Services.DefaultTemplateService.LoadBuiltInAsync();
        ModbusMaster.MasterWorkspace second =
            await ModbusTool.App.Services.DefaultTemplateService.LoadBuiltInAsync();

        // 两次加载相互独立，修改其中一个不影响另一个（模板只读语义）。
        Assert.NotSame(first, second);
        Assert.Equal(first.Devices[0].Name, second.Devices[0].Name);
    }
}
