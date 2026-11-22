# © 2026 工控菜鸡刘 · ModbusTool · MIT License
<#
.SYNOPSIS
    校验当前版本，按目标框架与发布模式构建 ModbusTool 主从站程序，并生成分发包与安装包。

.DESCRIPTION
    version.json 的 current 是唯一版本源。脚本不会通过命令行另选版本，也不会自动 bump；
    -Version 仅用于核对调用者预期，必须与 current 完全一致。支持完整 SemVer，包括预发布后缀。

    双目标说明：
    - net8：主版本，要求 Windows 10 1809 及以上，使用 ScottPlot 5.1.x。
    - net6：旧系统兼容版，支持 Windows 7 SP1 及以上，使用 ScottPlot 5.0.x。

    发布模式：
    - FrameworkDependent：框架依赖，程序目录只含自身 dll 与资源，体积小；目标机需安装对应
      .NET Desktop Runtime（net8 或 net6）。推荐给已有运行时的用户与安装包。
    - SelfContained：自包含单文件，内置运行时，离线即用；体积大，不需要预装 .NET。
    - Both：同时产出上述两种模式（默认）。

    执行流程：
    1. 校验 SemVer、历史版本单调递增且未重复、构建属性同步、CHANGELOG 条目、csproj 无版本覆盖。
    2. 前置校验全部通过后，清空输出目录，避免旧文件混入本次交付。
    3. 对每个目标框架与模式执行 Release 发布；主从站共用同一程序目录，避免重复依赖。
    4. 剔除 PDB、多余多国语言资源与 Assets 副本，仅保留 zh-Hans。
    5. 写入 about.json，并附带 README、LICENSE、version.json 与 docs/help 离线帮助。
    6. 生成 ZIP；检测到 7-Zip 时额外生成固实压缩 .7z，体积更小。
    7. 指定 -Installer 时调用 Inno Setup 生成 net8/net6 两个安装包（需本机已安装 Inno Setup 6）。
    8. 计算全部交付物 SHA256，写入 publish/SHA256SUMS.txt。
    9. 上述步骤全部成功后，才把版本、日期、类型和摘要登记到 version.json 的 released。

    前置校验失败时不会清空输出目录。开始发布后任一步骤失败都会立即中止且不登记 released；
    已清空或已生成的目录/压缩包不会自动回滚，重试前应检查输出。源码包使用系统临时目录暂存，
    所有仓库输入路径均由 scripts 所在位置相对解析，不依赖个人绝对路径或调用者当前目录。

.PARAMETER Version
    可选的版本预期值，只能与 version.json 的 current 一致；不一致会在清空输出前中止。

.PARAMETER Summary
    成功发布后写入 released 的摘要，默认“见 CHANGELOG.md”。

.PARAMETER Runtime
    dotnet publish 的运行时标识（RID），默认 win-x64，同时用于产物文件名。

.PARAMETER OutputDirectory
    输出目录，默认仓库 publish/。相对路径以仓库根目录为基准；也可显式指定独立绝对目录。
    每次执行都会清空该目录，因此不得传入需要保留内容的目录。

.PARAMETER DotnetPath
    dotnet 命令或可执行文件路径，默认 dotnet；仅用于发布，不修改 SDK 配置。

.PARAMETER Mode
    发布模式：Both（默认）、FrameworkDependent 或 SelfContained。

.PARAMETER NoZip
    跳过所有 ZIP 与 7z，仅保留解压后的程序目录。安装包仍可单独生成。

.PARAMETER Symbols
    额外收集 bin/Release 下的 PDB，生成独立符号包，不进入用户交付包。

.PARAMETER Installer
    生成 net8/net6 两个 Inno Setup 安装包；使用框架依赖产物作为安装源。

.PARAMETER InstallerOnly
    仅为当前版本补产安装包：不校验“版本是否已发布”、不清空输出、不重新打包 ZIP、不登记 released。
    适用于版本已发布但当时未安装 Inno Setup 的场景。

.PARAMETER InnoSetupPath
    ISCC.exe 路径；留空时依次检查 PATH、Program Files 与 Program Files (x86)。

.PARAMETER NoRegister
    只验证构建与打包，不写入 version.json.released；用于发布演练，不用于正式交付。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\publish.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Mode FrameworkDependent -Installer

.NOTES
    本脚本会执行真实发布并写入 released；仅做版本验证时请使用 scripts\version.ps1。
    安装包需要 Inno Setup 6；WPF 不做裁剪（PublishTrimmed=false），避免主题资源与控件样式被误删。
#>
[CmdletBinding()]
param(
    [string]$Version = "",

    [string]$Summary = "见 CHANGELOG.md",

    [string]$Runtime = "win-x64",

    [string]$OutputDirectory = "",

    [string]$DotnetPath = "dotnet",

    [ValidateSet("Both", "FrameworkDependent", "SelfContained")]
    [string]$Mode = "Both",

    [switch]$NoZip,

    [switch]$Symbols,

    [switch]$NoRegister,

    [switch]$Installer,

    [switch]$InstallerOnly,

    [string]$InnoSetupPath = ""
)

$ErrorActionPreference = "Stop"
$scriptRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($scriptRoot)) {
    $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
}

$root = Split-Path -Parent $scriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = "publish"
}

. (Join-Path $scriptRoot "version-lib.ps1")

# 先只读校验唯一版本源及其一致性，全部通过后才允许清空输出目录。
$state = Read-VersionState
$resolvedVersion = $state.current
if (-not [string]::IsNullOrWhiteSpace($Version) -and $Version -ne $resolvedVersion) {
    throw "命令行版本 $Version 与 version.json 的 current（$resolvedVersion）不一致；请先用 scripts/version.ps1 -Command bump 提升版本。"
}

# 发布前置校验：语义化格式、未重复发布、单调递增、构建属性同步、变更日志存在、项目内无版本覆盖。
if (-not $InstallerOnly) {
    $null = Assert-ReleaseReady $resolvedVersion
}
$Version = $resolvedVersion
Write-Host "发布前置校验通过，版本：$Version"

$masterProject = Join-Path $root "src\ModbusMasterApp\ModbusMasterApp.csproj"
$slaveProject = Join-Path $root "src\ModbusSlaveApp\ModbusSlaveApp.csproj"
$installerScript = Join-Path $root "installer\ModbusTool.iss"
$output = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
}
else {
    [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
}
$staging = Join-Path $output ".staging"
$buildTime = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
$utf8 = New-Object Text.UTF8Encoding($false)

# 产物命名规范（G13 起）：
#   安装版  Setup：<根>\Setup\ModbusTool-v<版本>-Setup-<tfm>.exe（仅框架依赖）
#   免安装  Portable：<根>\Portable\ModbusTool-v<版本>-Portable-<tfm>-fd|sc.<zip|7z>
#   源码包：<根>\ModbusTool-v<版本>-Source.zip
# tfm 变体通过 variant 增加 ShortName（如 net8/net6）；fd=框架依赖，sc=自包含。
$variants = @(
    [pscustomobject]@{ Name = "net8"; Framework = "net8.0-windows"; ShortName = "net8"; MinWindows = "10.0.17763"; RuntimeMajor = "8.0"; RuntimeName = ".NET 8 Desktop Runtime"; TargetOs = "Win10 1809+ / Win11" },
    [pscustomobject]@{ Name = "net6"; Framework = "net6.0-windows"; ShortName = "net6"; MinWindows = "6.1sp1"; RuntimeMajor = "6.0"; RuntimeName = ".NET 6 Desktop Runtime"; TargetOs = "Win7 SP1 ~ Win10" }
)

$modes = @(switch ($Mode) {
    "FrameworkDependent" { "framework-dependent" }
    "SelfContained" { "self-contained" }
    default { "framework-dependent", "self-contained" }
})
# 安装包始终基于框架依赖产物；只选自包含时会同时补建并打包框架依赖产物。
if ($Installer -and $modes -notcontains "framework-dependent") {
    $modes += "framework-dependent"
}

# 发布目录每次发布前清空，避免历史产物混入当前交付包；仅补产安装包时保留已有产物。
if (-not $InstallerOnly -and (Test-Path $output)) {
    Remove-Item -Path $output -Recurse -Force
}

$setupDirectory = Join-Path $output "Setup"
$portableDirectory = Join-Path $output "Portable"

# InstallerOnly 分支：补产安装包使用相同 Setup 目录与新命名。
if ($InstallerOnly) {
    $setupDirectory = Join-Path $output "Setup"
}

New-Item -ItemType Directory -Force -Path $output | Out-Null
if (-not $InstallerOnly) {
    New-Item -ItemType Directory -Force -Path $staging | Out-Null
}
$artifacts = New-Object System.Collections.Generic.List[string]
$variantAppDirectories = @{}

function Invoke-AppPublish {
    param(
        [string]$Project,
        [string]$Framework,
        [string]$AppDirectory,
        [bool]$SelfContained
    )

    $arguments = @(
        "publish", $Project,
        "-c", "Release",
        "-f", $Framework,
        "-r", $Runtime
    )
    if ($SelfContained) {
        $arguments += @(
            "--self-contained", "true",
            "-p:PublishSingleFile=true",
            "-p:IncludeNativeLibrariesForSelfExtract=true",
            # 压缩单文件内嵌程序集，显著降低自包含包体积；首次启动略慢，换取更小的分发体积。
            "-p:EnableCompressionInSingleFile=true"
        )
    }
    else {
        $arguments += @(
            "--self-contained", "false",
            "-p:PublishSingleFile=false"
        )
    }

    # WPF 不做裁剪：主题、控件样式与反射资源容易被裁剪误删，体积优先通过框架依赖模式控制。
    $arguments += @(
        "-p:PublishTrimmed=false",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "-p:Version=$Version",
        "-p:BuildTime=$buildTime",
        "-o", $AppDirectory
    )

    & $DotnetPath @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "发布失败：$Project ($Framework)"
    }
}

function Remove-PublishNoise {
    param([string]$AppDirectory)

    Get-ChildItem -Path $AppDirectory -File -Filter *.pdb -Recurse | Remove-Item -Force
    foreach ($child in Get-ChildItem -Path $AppDirectory -Directory) {
        # 仅删除 de/es/fr 这类语言资源目录，保留 zh-Hans；runtimes 等非语言目录不受影响。
        if ($child.Name -match '^[a-z]{2}(-[A-Za-z]{2,4})?$' -and $child.Name -ne "zh-Hans") {
            Remove-Item -Path $child.FullName -Recurse -Force
        }
    }

    # 单文件与框架依赖模式都会复制图标副本，运行期使用嵌入资源，可直接清理。
    if (Test-Path (Join-Path $AppDirectory "Assets")) {
        Remove-Item -Path (Join-Path $AppDirectory "Assets") -Recurse -Force
    }
}

function Copy-RuntimeDocuments {
    param([string]$AppDirectory)

    Copy-Item -Path (Join-Path $root "README.md") -Destination $AppDirectory -Force
    Copy-Item -Path (Join-Path $root "LICENSE") -Destination $AppDirectory -Force
    Copy-Item -Path (Join-Path $root "version.json") -Destination $AppDirectory -Force

    $helpTarget = Join-Path $AppDirectory "docs\help"
    robocopy (Join-Path $root "docs\help") $helpTarget /E /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "帮助文档复制失败，robocopy 退出码：$LASTEXITCODE。"
    }

    $global:LASTEXITCODE = 0

    # 用户手册随包提供；开发、兼容、构建与历史归档文档不进入发布包。
    $manualTarget = Join-Path $AppDirectory "docs\user"
    robocopy (Join-Path $root "docs\user") $manualTarget /E /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "用户手册复制失败，robocopy 退出码：$LASTEXITCODE。"
    }

    $global:LASTEXITCODE = 0

    # G11 起界面不再读取 about.json，此处仅保留可编辑模板，署名与 About 定稿保持一致。
    $aboutTemplate = [ordered]@{
        Author = "工控菜鸡刘"
        Description = "面向工业现场的 Modbus 主站 / 从站双独立调试程序，支持 Modbus TCP 与 Modbus RTU。"
        FeatureSummary = ""
        Copyright = ""
        SelfDevelopedNote = ""
        ModbusCoreNote = ""
    }
    [IO.File]::WriteAllText(
        (Join-Path $AppDirectory "about.json"),
        ($aboutTemplate | ConvertTo-Json -Depth 3),
        $utf8)
}

function Resolve-SevenZip {
    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($env:SEVENZIP_PATH)) {
        $candidates += $env:SEVENZIP_PATH
    }
    $candidates += @(
        "C:\Program Files\7-Zip\7z.exe",
        "C:\Program Files (x86)\7-Zip\7z.exe"
    )
    $command = Get-Command 7z -ErrorAction SilentlyContinue
    if ($command) {
        $candidates += $command.Source
    }

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    return $null
}

# 已发布版本补产安装包：必须在正式发布循环之前执行，避免重复构建与覆盖现有产物。
if ($InstallerOnly) {
    if (-not (Test-Path $installerScript)) {
        throw "缺少安装包脚本：$installerScript"
    }

    $iscc = $null
    if (-not [string]::IsNullOrWhiteSpace($InnoSetupPath)) {
        if (-not (Test-Path $InnoSetupPath)) {
            throw "指定的 Inno Setup 编译器不存在：$InnoSetupPath"
        }

        $iscc = $InnoSetupPath
    }

    if ([string]::IsNullOrWhiteSpace($iscc)) {
        $isccCommand = Get-Command ISCC -ErrorAction SilentlyContinue
        if ($isccCommand) {
            $iscc = $isccCommand.Source
        }
    }

    if ([string]::IsNullOrWhiteSpace($iscc)) {
        foreach ($candidate in @(
            "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
            "C:\Program Files\Inno Setup 6\ISCC.exe",
            (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
        )) {
            if (Test-Path $candidate) {
                $iscc = $candidate
                break
            }
        }
    }

    if ([string]::IsNullOrWhiteSpace($iscc)) {
        throw "未找到 Inno Setup 6 的 ISCC.exe。请安装 Inno Setup 6 后重试，或用 -InnoSetupPath 指定路径。"
    }

    $installerStaging = Join-Path $output ".installer-staging"
    if (Test-Path $installerStaging) {
        Remove-Item -Path $installerStaging -Recurse -Force
    }

    $setupPaths = New-Object System.Collections.Generic.List[string]
    New-Item -ItemType Directory -Force -Path $setupDirectory | Out-Null
    try {
        foreach ($variant in $variants) {
            $appDirectory = Join-Path $installerStaging $variant.Name
            New-Item -ItemType Directory -Force -Path $appDirectory | Out-Null
            Write-Host "补产安装包：发布 $($variant.Name) 框架依赖产物 ..."
            Invoke-AppPublish -Project $masterProject -Framework $variant.Framework -AppDirectory $appDirectory -SelfContained $false
            Invoke-AppPublish -Project $slaveProject -Framework $variant.Framework -AppDirectory $appDirectory -SelfContained $false
            Remove-PublishNoise -AppDirectory $appDirectory
            Copy-RuntimeDocuments -AppDirectory $appDirectory

            $outputBaseName = "ModbusTool-v$Version-Setup-$($variant.ShortName)"
            & $iscc `
                "/DAppVariant=$($variant.Name)" `
                "/DAppVersion=$Version" `
                "/DSourceDir=$appDirectory" `
                "/DOutputDir=$setupDirectory" `
                "/DOutputBaseFilename=$outputBaseName" `
                "/DMinWindows=$($variant.MinWindows)" `
                "/DRuntimeMajor=$($variant.RuntimeMajor)" `
                "/DRuntimeName=$($variant.RuntimeName)" `
                $installerScript
            if ($LASTEXITCODE -ne 0) {
                throw "Inno Setup 编译失败：$outputBaseName.exe"
            }

            $setupPath = Join-Path $setupDirectory "$outputBaseName.exe"
            if (-not (Test-Path $setupPath)) {
                throw "安装包未生成：$setupPath"
            }

            $setupPaths.Add($setupPath)
        }
    }
    finally {
        if (Test-Path $installerStaging) {
            Remove-Item -Path $installerStaging -Recurse -Force
        }
    }

    $hashFile = Join-Path $output "SHA256SUMS.txt"
    $hashLines = @()
    if (Test-Path $hashFile) {
        $hashLines = @(Get-Content $hashFile | Where-Object { $_ -notmatch '\s+ModbusTool-.*-Setup-.*\.exe$' })
    }

    foreach ($setupPath in $setupPaths) {
        $hash = Get-FileHash -Path $setupPath -Algorithm SHA256
        $item = Get-Item $setupPath
        $hashLines += ("{0}  {1}" -f $hash.Hash.ToLowerInvariant(), $item.Name)
        Write-Host ("  {0}  {1:N2} MB  {2}" -f $item.Name, ($item.Length / 1MB), $hash.Hash.ToLowerInvariant())
    }

    [IO.File]::WriteAllLines($hashFile, ($hashLines | Sort-Object { ($_ -split '\s{2}', 2)[1] }), $utf8)
    Write-Host "安装包补产完成，未修改 version.json。"
    return
}

$sevenZip = Resolve-SevenZip
if (-not $NoZip -and $null -eq $sevenZip) {
    Write-Host "未检测到 7-Zip，仅生成标准 ZIP；安装 7-Zip 后可额外获得固实压缩 .7z。"
}

foreach ($variant in $variants) {
    foreach ($modeName in $modes) {
        $selfContained = $modeName -eq "self-contained"
        # fd = 框架依赖，sc = 自包含（对外简称，用于 Portable 文件名）。
        $modeShort = if ($selfContained) { "sc" } else { "fd" }
        $appDirectory = Join-Path $staging "$($variant.Name)-$modeName"
        New-Item -ItemType Directory -Force -Path $appDirectory | Out-Null

        Write-Host "发布 $($variant.Name) / $modeName ..."
        Invoke-AppPublish -Project $masterProject -Framework $variant.Framework -AppDirectory $appDirectory -SelfContained $selfContained
        Invoke-AppPublish -Project $slaveProject -Framework $variant.Framework -AppDirectory $appDirectory -SelfContained $selfContained
        Remove-PublishNoise -AppDirectory $appDirectory
        Copy-RuntimeDocuments -AppDirectory $appDirectory

        if ($NoZip) {
            $finalDirectory = Join-Path $output "ModbusTool-v$Version-Portable-$($variant.ShortName)-$modeShort-$Runtime"
            Move-Item -Path $appDirectory -Destination $finalDirectory -Force
            if ($modeName -eq "framework-dependent") {
                $variantAppDirectories[$variant.Name] = $finalDirectory
            }

            Write-Host "程序目录：$finalDirectory"
            continue
        }

        if ($modeName -eq "framework-dependent") {
            $variantAppDirectories[$variant.Name] = $appDirectory
        }

        $baseName = "ModbusTool-v$Version-Portable-$($variant.ShortName)-$modeShort"
        New-Item -ItemType Directory -Force -Path $portableDirectory | Out-Null
        $zipPath = Join-Path $portableDirectory "$baseName.zip"
        Compress-Archive -Path (Join-Path $appDirectory "*") -DestinationPath $zipPath -CompressionLevel Optimal -Force
        $artifacts.Add($zipPath)

        if ($null -ne $sevenZip) {
            $sevenPath = Join-Path $portableDirectory "$baseName.7z"
            & $sevenZip a -t7z -mx=9 -m0=lzma2 -ms=on -mmt=on -bso0 -bsp0 $sevenPath (Join-Path $appDirectory "*") | Out-Null
            if ($LASTEXITCODE -ge 2) {
                throw "7-Zip 压缩失败：$sevenPath（退出码 $LASTEXITCODE）。"
            }

            $artifacts.Add($sevenPath)
        }
    }
}

if ($Symbols) {
    $symbolDirectory = Join-Path $staging "symbols"
    New-Item -ItemType Directory -Force -Path $symbolDirectory | Out-Null
    foreach ($variant in $variants) {
        $frameworkTarget = $variant.Framework
        $binRoot = Join-Path $root "src"
        $pdbs = Get-ChildItem -Path $binRoot -Recurse -File -Filter *.pdb |
            Where-Object { $_.FullName -match [regex]::Escape("\bin\Release\$frameworkTarget\") }
        foreach ($pdb in $pdbs) {
            $destination = Join-Path $symbolDirectory "$($variant.Name)\$($pdb.Name)"
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
            Copy-Item -Path $pdb.FullName -Destination $destination -Force
        }
    }

    $symbolZip = Join-Path $output "ModbusTool-$Version-symbols.zip"
    Compress-Archive -Path (Join-Path $symbolDirectory "*") -DestinationPath $symbolZip -CompressionLevel Optimal -Force
    $artifacts.Add($symbolZip)
}

function Resolve-Iscc {
    if (-not [string]::IsNullOrWhiteSpace($InnoSetupPath)) {
        if (Test-Path $InnoSetupPath) {
            return $InnoSetupPath
        }

        throw "指定的 Inno Setup 编译器不存在：$InnoSetupPath"
    }

    $command = Get-Command ISCC -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    foreach ($candidate in @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe",
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
    )) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    throw "未找到 Inno Setup 6 的 ISCC.exe。请安装 Inno Setup 6 后重试，或用 -InnoSetupPath 指定路径。"
}

if ($Installer) {
    if (-not (Test-Path $installerScript)) {
        throw "缺少安装包脚本：$installerScript"
    }

    $iscc = Resolve-Iscc
    foreach ($variant in $variants) {
        $sourceDirectory = $variantAppDirectories[$variant.Name]
        if ([string]::IsNullOrWhiteSpace($sourceDirectory) -or -not (Test-Path $sourceDirectory)) {
            throw "安装包源目录不存在：$($variant.Name)。安装包必须使用框架依赖产物。"
        }

        $outputBaseName = "ModbusTool-v$Version-Setup-$($variant.ShortName)"
        Write-Host "生成安装包 $outputBaseName.exe ..."
        & $iscc `
            "/DAppVariant=$($variant.Name)" `
            "/DAppVersion=$Version" `
            "/DSourceDir=$sourceDirectory" `
            "/DOutputDir=$setupDirectory" `
            "/DOutputBaseFilename=$outputBaseName" `
            "/DMinWindows=$($variant.MinWindows)" `
            "/DRuntimeMajor=$($variant.RuntimeMajor)" `
            "/DRuntimeName=$($variant.RuntimeName)" `
            $installerScript
        if ($LASTEXITCODE -ne 0) {
            throw "Inno Setup 编译失败：$outputBaseName.exe"
        }

        $setupPath = Join-Path $setupDirectory "$outputBaseName.exe"
        if (Test-Path $setupPath) {
            $artifacts.Add($setupPath)
        }
    }
}

if (-not $NoZip) {
    # 源码包：排除构建产物、发布目录、本地 IDE 缓存与 docs/archive 历史归档。
    $sourceZip = Join-Path $output "ModbusTool-v$Version-Source.zip"
    $sourceStage = Join-Path ([IO.Path]::GetTempPath()) "modbus-source-$([Guid]::NewGuid().ToString('N'))"
    robocopy $root $sourceStage /E /XD bin obj .trae .vs publish TestResults (Join-Path $root "docs\archive") /NFL /NDL /NJH /NJS /NP | Out-Null
    # robocopy 0–7 均表示复制完成；8 及以上为失败，必须中止且不能登记发布。
    if ($LASTEXITCODE -ge 8) {
        throw "源码暂存失败，robocopy 退出码：$LASTEXITCODE。"
    }
    # robocopy 的 0–7 是成功状态；归零避免外层 PowerShell 将正常发布误报为失败。
    $global:LASTEXITCODE = 0

    # 只向暂存副本预写同一条发布记录，确保源码 ZIP 自包含；真实版本文件仍在全部成功后登记。
    $releaseDate = Get-Date -Format "yyyy-MM-dd"
    $releaseRecord = [pscustomobject]@{
        version = $Version
        date = $releaseDate
        type = Get-ChangeType $Version @($state.released)
        summary = $Summary
    }
    $stagedVersion = [ordered]@{
        current = $Version
        released = @($releaseRecord) + @($state.released)
    }
    [IO.File]::WriteAllText(
        (Join-Path $sourceStage "version.json"),
        ($stagedVersion | ConvertTo-Json -Depth 8),
        $utf8)

    Compress-Archive -Path (Join-Path $sourceStage "*") -DestinationPath $sourceZip -CompressionLevel Optimal -Force
    Remove-Item -Path $sourceStage -Recurse -Force
    $artifacts.Add($sourceZip)
}

# 统一输出交付物大小与 SHA256，便于校验下载完整性；并生成 Release 展示文案。
if ($artifacts.Count -gt 0) {
    $hashLines = New-Object System.Collections.Generic.List[string]
    Write-Host ""
    Write-Host "交付物："
    foreach ($artifact in ($artifacts | Sort-Object)) {
        $hash = Get-FileHash -Path $artifact -Algorithm SHA256
        $item = Get-Item $artifact
        # SHA256SUMS.txt 中使用相对 publish/ 的路径，保留 Setup/Portable 分层。
        $relativeName = $item.FullName.Substring($output.Length).TrimStart('\').Replace('\', '/')
        $size = "{0:N2} MB" -f ($item.Length / 1MB)
        $hashLines.Add(("{0}  {1}" -f $hash.Hash.ToLowerInvariant(), $relativeName))
        Write-Host ("  {0}  {1}  {2}" -f $relativeName, $size, $hash.Hash.ToLowerInvariant())
    }

    $hashFile = Join-Path $output "SHA256SUMS.txt"
    [IO.File]::WriteAllLines($hashFile, $hashLines, $utf8)
    Write-Host "校验文件：$hashFile"

    # Release 展示文案：按安装版/免安装版分组，含适用系统与运行时依赖，发布时直接复制。
    $releaseNoteLines = New-Object System.Collections.Generic.List[string]
    $releaseNoteLines.Add("📦 安装版（Setup，支持快捷方式、mbwork 文件关联、控制面板卸载）")
    foreach ($variant in $variants) {
        $releaseNoteLines.Add("- ModbusTool-v$Version-Setup-$($variant.ShortName).exe｜适用于 $($variant.TargetOs)，需要 $($variant.RuntimeName)")
    }
    $releaseNoteLines.Add("")
    $releaseNoteLines.Add("📦 免安装版（Portable，解压直接运行）")
    $releaseNoteLines.Add("✅ 轻量版（框架依赖-fd，推荐，体积小）")
    foreach ($variant in $variants) {
        $releaseNoteLines.Add("- ModbusTool-v$Version-Portable-$($variant.ShortName)-fd.7z｜$($variant.TargetOs)，依赖.NET$($variant.RuntimeMajor)")
    }
    $releaseNoteLines.Add("")
    $releaseNoteLines.Add("✅ 离线版（自包含-sc，自带运行时，裸机使用）")
    foreach ($variant in $variants) {
        $releaseNoteLines.Add("- ModbusTool-v$Version-Portable-$($variant.ShortName)-sc.7z｜$($variant.TargetOs)，无需预装运行时")
    }
    $releaseNoteLines.Add("")
    $releaseNoteLines.Add("📜 源码包")
    if (-not $NoZip) {
        $releaseNoteLines.Add("- ModbusTool-v$Version-Source.zip")
    }
    $releaseNoteLines.Add("")
    $releaseNoteLines.Add("校验文件：SHA256SUMS.txt")

    $releaseNoteFile = Join-Path $output "RELEASE-COPY.txt"
    [IO.File]::WriteAllLines($releaseNoteFile, $releaseNoteLines, $utf8)
    Write-Host "Release 文案：$releaseNoteFile"
}

# 暂存目录只服务本次发布，完成后清理，保持 publish/ 只含交付物。
if (Test-Path $staging) {
    Remove-Item -Path $staging -Recurse -Force
}

# 所有发布与打包步骤成功后才写入版本历史，保证版本号与发布日志一一对应；失败时已生成产物不自动回滚。
if ($NoRegister) {
    Write-Host "已指定 -NoRegister：本次仅验证构建与打包，不写入 version.json.released。"
}
else {
    $record = Add-ReleaseRecord $Version $Summary
    Write-Host "已登记发布记录: $($record.version) ($($record.type), $($record.date))"
}
