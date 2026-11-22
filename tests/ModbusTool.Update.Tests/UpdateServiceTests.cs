// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ModbusTool.Update.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public void ManifestRejectsHttpAndInvalidMd5()
    {
        UpdateManifest manifest = CreateManifest(CreatePackage(), Convert.ToHexString(MD5.HashData(CreatePackage())));
        manifest.Packages["win-x64"].Url = "http://updates.example.test/app.zip";
        Assert.Throws<InvalidDataException>(() => UpdateManifest.Parse(JsonSerializer.Serialize(manifest)));

        manifest = CreateManifest(CreatePackage(), "bad-md5");
        Assert.Throws<InvalidDataException>(() => UpdateManifest.Parse(JsonSerializer.Serialize(manifest)));
    }

    [Fact]
    public async Task CheckReadsManifestAndSelectsCurrentArchitecture()
    {
        byte[] package = CreatePackage();
        UpdateManifest manifest = CreateManifest(package, Md5(package));
        using UpdateService service = new(new FakeHandler(_ => JsonResponse(manifest)));

        UpdateCheckResult result = await service.CheckAsync(
            "https://updates.example.test/update.json",
            new Version(1, 0, 0));

        Assert.True(result.UpdateAvailable);
        Assert.Equal(new Version(1, 1, 0), result.LatestVersion);
        Assert.Equal(package.LongLength, result.Package.Size);
        Assert.Equal(RuntimeInformation.OSArchitecture == Architecture.X86 ? "win-x86" : "win-x64", result.RuntimeIdentifier);
    }

    [Fact]
    public async Task CheckTreatsThreePartAndFourPartVersionsAsEqual()
    {
        byte[] package = CreatePackage();
        UpdateManifest manifest = CreateManifest(package, Md5(package));
        manifest.Version = "1.0.0";
        using UpdateService service = new(new FakeHandler(_ => JsonResponse(manifest)));

        UpdateCheckResult result = await service.CheckAsync(
            "https://updates.example.test/update.json",
            new Version(1, 0, 0, 0));

        Assert.False(result.UpdateAvailable);
    }

    [Fact]
    public async Task PrepareVerifiesMd5AndExtractsMainProgramAndUpdater()
    {
        byte[] package = CreatePackage();
        using UpdateService service = new(new FakeHandler(_ => ByteResponse(package)));

        PreparedUpdate prepared = await service.PrepareAsync(
            CreateCheckResult(package, Md5(package)),
            new UpdateSettings());

        Assert.Equal(package.LongLength, new FileInfo(prepared.PackagePath).Length);
        Assert.True(File.Exists(Path.Combine(prepared.SourceDirectory, "ModbusTool.exe")));
        Assert.True(File.Exists(prepared.UpdaterPath));
        Assert.Equal("new-main", File.ReadAllText(Path.Combine(prepared.SourceDirectory, "ModbusTool.exe")));
    }

    [Fact]
    public async Task PrepareRejectsMd5Mismatch()
    {
        byte[] package = CreatePackage();
        using UpdateService service = new(new FakeHandler(_ => ByteResponse(package)));

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(
            CreateCheckResult(package, new string('0', 32)),
            new UpdateSettings()));

        Assert.Contains("MD5", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckRejectsHttpsToHttpRedirect()
    {
        using UpdateService service = new(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("http://updates.example.test/update.json") }
        }));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckAsync(
            "https://updates.example.test/update.json",
            new Version(1, 0, 0)));
    }

    [Fact]
    public async Task PrepareRejectsZipTraversal()
    {
        byte[] package = CreatePackage(includeTraversal: true);
        using UpdateService service = new(new FakeHandler(_ => ByteResponse(package)));

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(
            CreateCheckResult(package, Md5(package)),
            new UpdateSettings()));

        Assert.Contains("非法路径", exception.Message);
    }

    private static UpdateCheckResult CreateCheckResult(byte[] package, string md5)
    {
        UpdateManifest manifest = CreateManifest(package, md5);
        string rid = RuntimeInformation.OSArchitecture == Architecture.X86 ? "win-x86" : "win-x64";
        return new UpdateCheckResult(true, new Version(1, 0, 0), new Version(1, 1, 0), manifest, manifest.Packages[rid], rid);
    }

    private static UpdateManifest CreateManifest(byte[] package, string md5)
    {
        UpdatePackage updatePackage = new()
        {
            Url = "https://updates.example.test/app.zip",
            Md5 = md5,
            Size = package.LongLength
        };
        return new UpdateManifest
        {
            Version = "1.1.0",
            Packages = new Dictionary<string, UpdatePackage>(StringComparer.OrdinalIgnoreCase)
            {
                ["win-x64"] = updatePackage,
                ["win-x86"] = updatePackage
            }
        };
    }

    private static byte[] CreatePackage(bool includeTraversal = false)
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, true))
        {
            WriteEntry(archive, "ModbusTool.exe", "new-main");
            WriteEntry(archive, "ModbusTool.Updater.exe", "new-updater");
            WriteEntry(archive, "update-settings.json", "{\"SchemaVersion\":1}");
            if (includeTraversal)
            {
                WriteEntry(archive, "../outside.txt", "invalid");
            }
        }

        return stream.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name);
        using StreamWriter writer = new(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static HttpResponseMessage JsonResponse(UpdateManifest manifest) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(manifest), Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage ByteResponse(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes)
    };

    private static string Md5(byte[] bytes) => Convert.ToHexString(MD5.HashData(bytes));

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
