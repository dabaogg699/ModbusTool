# © 2026 工控菜鸡刘 · ModbusTool · MIT License
# 版本管理共享函数：由 scripts/version.ps1 与 scripts/publish.ps1 共同点源加载。
# version.json 的 current 是唯一版本源，released 是发布历史；其他文件只能从 current 同步或接受校验。
# 所有仓库路径均由本脚本所在 scripts 目录向上解析，不依赖调用者当前目录，也不包含个人绝对路径。
# 共享函数遇到格式、文件或一致性错误时抛出异常，由调用脚本立即中止，不继续后续写入或发布步骤。
if (-not $PSScriptRoot) {
    throw "version-lib.ps1 必须通过脚本点源方式加载。"
}

$script:VersionRepoRoot = Split-Path -Parent $PSScriptRoot
$script:VersionFile = Join-Path $script:VersionRepoRoot "version.json"
$script:BuildPropsFile = Join-Path $script:VersionRepoRoot "Directory.Build.props"
$script:ChangelogFile = Join-Path $script:VersionRepoRoot "CHANGELOG.md"

# 严格 SemVer 2.0.0：核心版本不接受前导零；预发布/构建标识按点分段且不得为空；
# 纯数字预发布标识不接受前导零。示例：1.1.4、1.1.4-beta.1、1.1.4-beta+build.2。
$script:SemVerPattern = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$'

function Get-RepoRoot {
    return $script:VersionRepoRoot
}

function Test-SemVer([string]$Version) {
    if ([string]::IsNullOrWhiteSpace($Version)) {
        return $false
    }

    return [regex]::IsMatch($Version, $script:SemVerPattern)
}

function Read-VersionState {
    if (-not (Test-Path $script:VersionFile)) {
        throw "缺少版本文件：$script:VersionFile"
    }

    $state = Get-Content $script:VersionFile -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not (Test-SemVer $state.current)) {
        throw "version.json 的 current 不是合法语义化版本：$($state.current)"
    }

    return $state
}

function Write-VersionState($Current, $Released) {
    $payload = [ordered]@{
        current = $Current
        released = @($Released)
    }
    $json = $payload | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($script:VersionFile, $json, (New-Object Text.UTF8Encoding($false)))
}

function ConvertTo-CoreVersion([string]$Version) {
    return [version]($Version.Split('-')[0].Split('+')[0])
}

function Get-SemVerPrerelease([string]$Version) {
    # 必须先移除构建元数据，避免把“+build”误算为预发布标识。
    $withoutBuild = $Version.Split('+')[0]
    $separator = $withoutBuild.IndexOf('-')
    if ($separator -lt 0) {
        return ""
    }

    return $withoutBuild.Substring($separator + 1)
}

function Compare-SemVer([string]$Left, [string]$Right) {
    if (-not (Test-SemVer $Left) -or -not (Test-SemVer $Right)) {
        throw "只能比较合法 SemVer 版本：'$Left'、'$Right'。"
    }

    $leftCore = ConvertTo-CoreVersion $Left
    $rightCore = ConvertTo-CoreVersion $Right
    $result = $leftCore.CompareTo($rightCore)
    if ($result -ne 0) {
        return $result
    }

    $leftPre = Get-SemVerPrerelease $Left
    $rightPre = Get-SemVerPrerelease $Right
    if ($leftPre -ceq $rightPre) {
        # 构建元数据不影响 SemVer 优先级。
        return 0
    }

    # 同一核心版本下，稳定版高于任何预发布版。
    if ([string]::IsNullOrEmpty($leftPre)) {
        return 1
    }

    if ([string]::IsNullOrEmpty($rightPre)) {
        return -1
    }

    # 逐段比较预发布标识：数字段按数值且低于非数字段；非数字段按 ASCII 顺序。
    $leftIdentifiers = @($leftPre.Split('.'))
    $rightIdentifiers = @($rightPre.Split('.'))
    $sharedCount = [Math]::Min($leftIdentifiers.Count, $rightIdentifiers.Count)
    for ($index = 0; $index -lt $sharedCount; $index++) {
        $leftIdentifier = $leftIdentifiers[$index]
        $rightIdentifier = $rightIdentifiers[$index]
        if ($leftIdentifier -ceq $rightIdentifier) {
            continue
        }

        $leftIsNumeric = [regex]::IsMatch($leftIdentifier, '^[0-9]+$')
        $rightIsNumeric = [regex]::IsMatch($rightIdentifier, '^[0-9]+$')
        if ($leftIsNumeric -and $rightIsNumeric) {
            # 合法数字标识没有前导零，先比长度再比字典序，避免大整数溢出。
            if ($leftIdentifier.Length -ne $rightIdentifier.Length) {
                return $(if ($leftIdentifier.Length -lt $rightIdentifier.Length) { -1 } else { 1 })
            }

            $result = [string]::CompareOrdinal($leftIdentifier, $rightIdentifier)
            return $(if ($result -lt 0) { -1 } else { 1 })
        }

        if ($leftIsNumeric -ne $rightIsNumeric) {
            return $(if ($leftIsNumeric) { -1 } else { 1 })
        }

        $result = [string]::CompareOrdinal($leftIdentifier, $rightIdentifier)
        return $(if ($result -lt 0) { -1 } else { 1 })
    }

    if ($leftIdentifiers.Count -eq $rightIdentifiers.Count) {
        return 0
    }

    # 公共标识完全相同时，标识数量更多的版本优先级更高。
    return $(if ($leftIdentifiers.Count -lt $rightIdentifiers.Count) { -1 } else { 1 })
}

function Get-ChangelogVersionPattern([string]$Version) {
    return "^##\s*\[" + [regex]::Escape($Version) + "\]"
}

function Assert-ChangelogHasVersion([string]$Version) {
    if (-not (Test-Path $script:ChangelogFile)) {
        throw "缺少 CHANGELOG.md，无法建立版本与发布日志的对应关系。"
    }

    $content = Get-Content $script:ChangelogFile -Raw -Encoding UTF8
    if (-not [regex]::IsMatch($content, (Get-ChangelogVersionPattern $Version), "Multiline")) {
        throw "CHANGELOG.md 缺少版本 $Version 的条目，请先补充 ## [$Version] 小节。"
    }
}

function Get-BuildVersionMap([string]$Version) {
    # 现有体系只要求 props 中存在 Version；其余节点若未显式声明，继续沿用 .NET SDK 的现有规则。
    # 若仓库后来显式声明这些节点，则同步/校验采用同一组确定值，防止预发布后遗留旧版本。
    $core = ConvertTo-CoreVersion $Version
    $coreText = "{0}.{1}.{2}" -f $core.Major, $core.Minor, $core.Build
    $numericVersion = "$coreText.0"
    return [ordered]@{
        Version             = $Version
        VersionPrefix       = $coreText
        AssemblyVersion     = $numericVersion
        FileVersion         = $numericVersion
        InformationalVersion = $Version
    }
}

function Assert-BuildPropsVersion([string]$Version) {
    if (-not (Test-Path $script:BuildPropsFile)) {
        throw "缺少 Directory.Build.props，无法保证开发构建与发布版本一致。"
    }

    $content = Get-Content $script:BuildPropsFile -Raw -Encoding UTF8
    $values = Get-BuildVersionMap $Version
    foreach ($name in $values.Keys) {
        $nodePattern = "<$name(?:\s[^>]*)?>(?<value>[^<]*)</$name>"
        $nodes = [regex]::Matches($content, $nodePattern)
        if ($nodes.Count -eq 0) {
            if ($name -eq "Version") {
                throw "Directory.Build.props 缺少 Version，或与 version.json 不一致，请执行 scripts/version.ps1 -Command sync 同步。"
            }

            # 可缺省节点不强制增加重复配置，保持现有 Version-only 文件结构。
            continue
        }

        if ($nodes.Count -ne 1) {
            throw "Directory.Build.props 存在多个 $name 节点，请合并为一个后再同步。"
        }

        $actual = $nodes[0].Groups["value"].Value.Trim()
        if ($actual -cne $values[$name]) {
            if ($name -eq "Version") {
                throw "Directory.Build.props 的 Version 与 version.json 不一致，请执行 scripts/version.ps1 -Command sync 同步。"
            }

            throw "Directory.Build.props 的 $name 应为 $($values[$name])，实际为 $actual，请执行 sync 同步。"
        }
    }
}

function Assert-NoProjectVersionOverride {
    $projects = Get-ChildItem (Join-Path $script:VersionRepoRoot "src") -Recurse -Filter *.csproj -File
    foreach ($project in $projects) {
        $content = Get-Content $project.FullName -Raw -Encoding UTF8
        # 禁止 csproj 以任何形式声明 Version（包括带 Condition 的节点），但不误伤 VersionPrefix 等其他节点。
        if ([regex]::IsMatch($content, '<Version(?:\s[^>]*)?>')) {
            throw "项目 $($project.Name) 内存在 <Version> 覆盖，请删除并统一由 Directory.Build.props 管理。"
        }
    }
}

function Get-ChangeType([string]$Version, $Released) {
    $previous = @($Released | ForEach-Object { $_.version } | Where-Object { Test-SemVer $_ })
    if ($previous.Count -eq 0) {
        return "initial"
    }

    $highest = $previous[0]
    foreach ($item in $previous) {
        if ((Compare-SemVer $item $highest) -gt 0) {
            $highest = $item
        }
    }

    $currentCore = ConvertTo-CoreVersion $Version
    $previousCore = ConvertTo-CoreVersion $highest
    if ($currentCore.Major -ne $previousCore.Major) {
        return "major"
    }

    if ($currentCore.Minor -ne $previousCore.Minor) {
        return "minor"
    }

    return "patch"
}

function Assert-ReleaseReady([string]$Version) {
    if (-not (Test-SemVer $Version)) {
        throw "版本号 '$Version' 不符合 SemVer 2.0.0（MAJOR.MINOR.PATCH[-prerelease][+build]）。"
    }

    $state = Read-VersionState
    $released = @($state.released)
    foreach ($item in $released) {
        if ($item.version -eq $Version) {
            throw "版本 $Version 已发布过，禁止重复发布；请先执行 scripts/version.ps1 -Command bump 提升版本号。"
        }

        if ((Compare-SemVer $Version $item.version) -le 0) {
            throw "版本 $Version 必须大于已发布版本 $($item.version)。"
        }
    }

    if ($state.current -ne $Version) {
        throw "version.json 的 current 为 $($state.current)，与发布版本 $Version 不一致。"
    }

    Assert-BuildPropsVersion $Version
    Assert-ChangelogHasVersion $Version
    Assert-NoProjectVersionOverride
    return $state
}

function Set-BuildPropsVersion([string]$Version) {
    if (-not (Test-SemVer $Version)) {
        throw "不能同步非法 SemVer 版本：$Version"
    }

    if (-not (Test-Path $script:BuildPropsFile)) {
        throw "缺少 Directory.Build.props。"
    }

    $content = Get-Content $script:BuildPropsFile -Raw -Encoding UTF8
    $updated = $content
    $values = Get-BuildVersionMap $Version
    foreach ($name in $values.Keys) {
        # Version 必须存在且保留完整预发布后缀/构建元数据；其余节点仅在原本显式存在时更新。
        # 未显式声明时保持 Version-only 配置；显式节点按核心版本、四位数字版本和完整信息版本
        # 同步，避免预发布切换后 VersionPrefix/AssemblyVersion/FileVersion/InformationalVersion 残留旧值。
        $nodePattern = "(?<open><$name(?:\s[^>]*)?>)(?<value>[^<]*)(?<close></$name>)"
        $nodes = [regex]::Matches($updated, $nodePattern)
        if ($nodes.Count -eq 0) {
            if ($name -eq "Version") {
                throw "Directory.Build.props 缺少唯一的 <Version> 节点，已中止同步。"
            }

            continue
        }

        if ($nodes.Count -ne 1) {
            throw "Directory.Build.props 存在多个 $name 节点，已中止同步。"
        }

        $node = $nodes[0]
        $replacement = $node.Groups["open"].Value + $values[$name] + $node.Groups["close"].Value
        $updated = $updated.Remove($node.Index, $node.Length).Insert($node.Index, $replacement)
    }

    if ($updated -ne $content) {
        [IO.File]::WriteAllText($script:BuildPropsFile, $updated, (New-Object Text.UTF8Encoding($false)))
    }
}

function Add-ChangelogSection([string]$Version, [string]$Part, [string]$Summary) {
    if (-not (Test-Path $script:ChangelogFile)) {
        throw "缺少 CHANGELOG.md。"
    }

    $content = Get-Content $script:ChangelogFile -Raw -Encoding UTF8
    if ([regex]::IsMatch($content, (Get-ChangelogVersionPattern $Version), "Multiline")) {
        # 允许在 bump 前按提交模板预写完整发布说明；保留原文，避免版本文件已写入后才因重复章节中止。
        Write-Host "CHANGELOG.md 已存在版本 $Version 的条目，保留预写发布说明。"
        return
    }

    $heading = switch ($Part) {
        "major" { "### 破坏性变更" }
        "minor" { "### 新增 / 优化" }
        default { "### 修复" }
    }
    $body = if ([string]::IsNullOrWhiteSpace($Summary)) { "- TODO: 补充本版本变更说明。" } else { "- $Summary" }
    $section = "## [$Version] - $(Get-Date -Format 'yyyy-MM-dd')`r`n`r`n$heading`r`n`r`n$body`r`n`r`n"

    $match = [regex]::Match($content, "^## \[", "Multiline")
    if ($match.Success) {
        $updated = $content.Substring(0, $match.Index) + $section + $content.Substring($match.Index)
    }
    else {
        $updated = $content.TrimEnd() + "`r`n`r`n" + $section
    }

    [IO.File]::WriteAllText($script:ChangelogFile, $updated, (New-Object Text.UTF8Encoding($false)))
}

function Add-ReleaseRecord([string]$Version, [string]$Summary, [string]$Date = "") {
    $state = Read-VersionState
    $released = @($state.released)
    $type = Get-ChangeType $Version $released
    if ([string]::IsNullOrWhiteSpace($Date)) {
        $Date = Get-Date -Format "yyyy-MM-dd"
    }
    $entry = [pscustomobject]@{
        version = $Version
        date = $Date
        type = $type
        summary = $Summary
    }
    $updated = @($entry) + $released
    Write-VersionState $state.current $updated
    return $entry
}
