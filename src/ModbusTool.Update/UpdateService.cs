// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ModbusTool.Update;

public sealed class UpdateService : IDisposable
{
    private const int MaximumManifestBytes = 1024 * 1024;
    private const int MaximumRedirects = 5;
    private readonly HttpClient _httpClient;

    public UpdateService(HttpMessageHandler? messageHandler = null)
    {
        _httpClient = new HttpClient(messageHandler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public async Task<UpdateCheckResult> CheckAsync(
        string manifestUrl,
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        Uri uri = UpdateUri.ValidateHttps(manifestUrl, "更新清单地址");
        using HttpResponseMessage response = await SendWithSecureRedirectsAsync(uri, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"更新清单请求失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        if (response.Content.Headers.ContentLength > MaximumManifestBytes)
        {
            throw new InvalidDataException("更新清单超过 1 MB 限制。");
        }

        await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream output = new();
        await CopyWithLimitAsync(input, output, MaximumManifestBytes, null, cancellationToken).ConfigureAwait(false);
        UpdateManifest manifest = UpdateManifest.Parse(Encoding.UTF8.GetString(output.ToArray()));
        string runtimeIdentifier = GetRuntimeIdentifier();
        UpdatePackage package = manifest.GetPackage(runtimeIdentifier);
        Version latestVersion = manifest.GetVersion();
        return new UpdateCheckResult(
            NormalizeVersion(latestVersion) > NormalizeVersion(currentVersion),
            currentVersion,
            latestVersion,
            manifest,
            package,
            runtimeIdentifier);
    }

    public async Task<PreparedUpdate> PrepareAsync(
        UpdateCheckResult checkResult,
        UpdateSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkResult);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        if (!checkResult.UpdateAvailable)
        {
            throw new InvalidOperationException("当前版本已经是最新版本，无需下载更新。");
        }

        UpdatePackage package = checkResult.Package;
        if (package.Size > settings.MaximumPackageBytes)
        {
            throw new InvalidDataException("安装包超过更新设置允许的下载大小。");
        }

        string workDirectory = Path.Combine(
            Path.GetTempPath(),
            "ModbusTool",
            "updates",
            $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
        string sourceDirectory = Path.Combine(workDirectory, "source");
        string packagePath = Path.Combine(workDirectory, "update.zip");
        Directory.CreateDirectory(workDirectory);

        Uri packageUri = UpdateUri.ValidateHttps(package.Url, "安装包地址");
        using (HttpResponseMessage response = await SendWithSecureRedirectsAsync(packageUri, cancellationToken).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"安装包下载失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            if (response.Content.Headers.ContentLength is long contentLength && contentLength != package.Size)
            {
                throw new InvalidDataException("服务器返回的安装包大小与更新清单不一致。");
            }

            await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using FileStream output = new(packagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            long copied = await CopyWithLimitAsync(
                input,
                output,
                package.Size,
                md5,
                cancellationToken).ConfigureAwait(false);
            if (copied != package.Size)
            {
                throw new InvalidDataException($"安装包大小不一致：清单 {package.Size} 字节，实际 {copied} 字节。");
            }

            string actualMd5 = Convert.ToHexString(md5.GetHashAndReset());
            if (!string.Equals(actualMd5, package.Md5, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"安装包 MD5 校验失败：期望 {package.Md5}，实际 {actualMd5}。");
            }
        }

        Directory.CreateDirectory(sourceDirectory);
        ExtractPackage(packagePath, sourceDirectory, settings.MaximumExtractedBytes);
        string updaterPath = Path.Combine(sourceDirectory, "ModbusTool.Updater.exe");
        if (!File.Exists(Path.Combine(sourceDirectory, "ModbusTool.exe")) || !File.Exists(updaterPath))
        {
            throw new InvalidDataException("安装包必须同时包含 ModbusTool.exe 和 ModbusTool.Updater.exe。");
        }

        return new PreparedUpdate(checkResult, workDirectory, sourceDirectory, packagePath, updaterPath);
    }

    public void Dispose() => _httpClient.Dispose();

    private async Task<HttpResponseMessage> SendWithSecureRedirectsAsync(
        Uri initialUri,
        CancellationToken cancellationToken)
    {
        Uri current = initialUri;
        for (int redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, current);
            HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently
                or HttpStatusCode.Redirect
                or HttpStatusCode.RedirectMethod
                or HttpStatusCode.RedirectKeepVerb
                or HttpStatusCode.TemporaryRedirect
                or HttpStatusCode.PermanentRedirect))
            {
                return response;
            }

            if (response.Headers.Location is null)
            {
                response.Dispose();
                throw new InvalidDataException("更新服务器返回了没有目标地址的重定向。");
            }

            Uri next = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new Uri(current, response.Headers.Location);
            UpdateUri.ValidateHttps(next.AbsoluteUri, "重定向地址");
            response.Dispose();
            current = next;
        }

        throw new InvalidDataException("更新服务器重定向次数过多。");
    }

    private static async Task<long> CopyWithLimitAsync(
        Stream input,
        Stream output,
        long maximumBytes,
        IncrementalHash? hash,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return total;
            }

            total += read;
            if (total > maximumBytes)
            {
                throw new InvalidDataException("下载内容超过声明大小或安全限制。");
            }

            hash?.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ExtractPackage(string packagePath, string sourceDirectory, long maximumExtractedBytes)
    {
        using ZipArchive archive = ZipFile.OpenRead(packagePath);
        long totalBytes = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            totalBytes = checked(totalBytes + entry.Length);
            if (totalBytes > maximumExtractedBytes)
            {
                throw new InvalidDataException("安装包解压后的内容超过安全限制。");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            string destinationPath = Path.GetFullPath(Path.Combine(sourceDirectory, entry.FullName));
            string sourcePrefix = sourceDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!destinationPath.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"安装包包含非法路径：{entry.FullName}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, false);
        }
    }

    private static Version NormalizeVersion(Version version) => new(
        version.Major,
        version.Minor,
        Math.Max(version.Build, 0),
        Math.Max(version.Revision, 0));

    private static string GetRuntimeIdentifier()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("ModbusTool 更新仅支持 Windows。");
        }

        return RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.X86 => "win-x86",
            _ => throw new PlatformNotSupportedException($"当前系统架构 {RuntimeInformation.OSArchitecture} 没有可用更新包。")
        };
    }
}
