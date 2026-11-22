# © 2026 工控菜鸡刘 · ModbusTool · MIT License
<#
.SYNOPSIS
    查看、推进、同步或校验 ModbusTool 版本。

.DESCRIPTION
    version.json 的 current 是唯一版本源，released 保存已经登记的历史版本。
    Directory.Build.props 只是构建输入的同步副本，CHANGELOG.md 提供对应版本的变更说明；
    两者都不能替代 version.json。所有仓库路径均相对 scripts 所在位置解析，可从任意工作目录调用。

    show     只读显示 current 与 released，不修改任何文件。
    bump     按 major/minor/patch 计算下一个稳定版本，依次更新 version.json、
             Directory.Build.props 和 CHANGELOG.md；Part 缺省时为 patch。
             对 1.1.4-beta 这类预发布版本执行 patch，会得到 1.1.4，而不是 1.1.5。
    sync     将 current 的完整 SemVer（含预发布后缀和构建元数据）同步到构建属性。
    validate 校验 SemVer、构建属性同步、CHANGELOG 条目以及 csproj 无 Version 覆盖，
             并显示 current 是否尚未发布；本命令只读，不代表已经执行过测试或发布。

    遇到读取、格式、校验或写入错误时立即中止，不继续执行后续步骤。bump 的多文件写入
    不是事务操作，若中途失败，应先检查三个文件的状态再重试。本脚本不会执行正式发布。

.PARAMETER Command
    必填操作：show、bump、sync 或 validate。

.PARAMETER Part
    bump 使用的递增级别：major、minor 或 patch；默认 patch，其他命令忽略此参数。

.PARAMETER Summary
    bump 写入 CHANGELOG 新条目的单行摘要；留空时写入待补充占位文字。

.PARAMETER Version
    兼容保留参数，不用于直接覆盖版本。为避免绕过唯一版本源，请通过 bump 修改 current。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\version.ps1 -Command show

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\version.ps1 -Command bump -Part patch -Summary "修复说明"

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\version.ps1 -Command sync
    powershell -ExecutionPolicy Bypass -File scripts\version.ps1 -Command validate
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("show", "bump", "sync", "validate")]
    [string]$Command,

    [ValidateSet("major", "minor", "patch")]
    [string]$Part = "patch",

    [string]$Summary = "",

    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "version-lib.ps1")

switch ($Command) {
    "show" {
        $state = Read-VersionState
        Write-Host "当前开发版本: $($state.current)"
        Write-Host "已发布版本:"
        if (@($state.released).Count -eq 0) {
            Write-Host "  (无)"
        }
        else {
            foreach ($item in $state.released) {
                Write-Host ("  {0}  {1}  {2}  {3}" -f $item.version, $item.date, $item.type, $item.summary)
            }
        }
    }

    "bump" {
        # Part 已有 patch 默认值；稳定版递增对应段，预发布版的默认 patch 转为同核心稳定版。
        $state = Read-VersionState
        $core = ConvertTo-CoreVersion $state.current
        $isPrerelease = -not [string]::IsNullOrEmpty((Get-SemVerPrerelease $state.current))
        switch ($Part) {
            "major" { $next = "{0}.0.0" -f ($core.Major + 1) }
            "minor" { $next = "{0}.{1}.0" -f $core.Major, ($core.Minor + 1) }
            default {
                if ($isPrerelease) {
                    # patch 对预发布版执行“转正”：去掉预发布/构建后缀，保持核心版本不变。
                    $next = "{0}.{1}.{2}" -f $core.Major, $core.Minor, $core.Build
                }
                else {
                    $next = "{0}.{1}.{2}" -f $core.Major, $core.Minor, ($core.Build + 1)
                }
            }
        }

        # 任一已发布版本不低于候选版本都在写入前中止，避免重复或倒退发布。
        foreach ($item in @($state.released)) {
            if ((Compare-SemVer $next $item.version) -le 0) {
                throw "自动计算的版本 $next 未大于已发布版本 $($item.version)，请检查 version.json 历史记录。"
            }
        }

        # 三处写入全部完成后才提示成功；任何异常都会停止后续写入。
        Write-VersionState $next @($state.released)
        Set-BuildPropsVersion $next
        Add-ChangelogSection $next $Part $Summary
        Write-Host "版本已提升: $($state.current) -> $next ($Part)"
        Write-Host "已同步 Directory.Build.props 并确认 CHANGELOG.md 条目，请完善变更说明后执行发布。"
    }

    "sync" {
        # Version 始终保存完整 SemVer；显式存在的其他版本节点也按共享规则同步。
        $state = Read-VersionState
        Set-BuildPropsVersion $state.current
        Write-Host "Directory.Build.props 已同步为 $($state.current)。"
    }

    "validate" {
        # 全部检查只读；任一 Assert 失败都会抛出异常并停止后续检查。
        $state = Read-VersionState
        $current = $state.current
        if (-not (Test-SemVer $current)) {
            throw "版本号 '$current' 不符合语义化版本规范。"
        }

        Write-Host "版本格式: PASS ($current)"
        Assert-BuildPropsVersion $current
        Write-Host "构建版本同步: PASS"
        Assert-ChangelogHasVersion $current
        Write-Host "变更日志条目: PASS"
        Assert-NoProjectVersionOverride
        Write-Host "项目内无版本覆盖: PASS"

        $released = @($state.released | Where-Object { $_.version -eq $current })
        if ($released.Count -gt 0) {
            Write-Host "发布就绪: NO —— $current 已发布，需先执行 bump 提升版本。"
        }
        else {
            Write-Host "发布就绪: YES —— 可执行 scripts/publish.ps1。"
        }
    }
}
