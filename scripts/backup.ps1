param(
    [ValidateSet('mysql', 'sqlite')]
    [string]$Provider = 'mysql',
    [string]$OutDir = (Join-Path $PSScriptRoot '..\backups'),
    [int]$KeepDays = 30,
    [string]$MySqlContainer = 'mes-mysql',
    [string]$Database = 'mes_server_power',
    [string]$DbUser = 'mes',
    [string]$DbPassword = $env:MES_DB_PASSWORD,
    [string]$SqliteFile = (Join-Path $PSScriptRoot '..\backend\MesApi\mes.db')
)

$ErrorActionPreference = 'Stop'
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Get-MySqlDumpCommand {
    $client = Get-Command mysqldump -ErrorAction SilentlyContinue
    if ($client) { return $client.Source }
    $docker = Get-Command docker -ErrorAction SilentlyContinue
    if ($docker) { return 'docker' }
    throw '未找到 mysqldump，也没有 docker。请安装 MySQL 客户端，或在 docker 环境执行。'
}

try {
    if ($Provider -eq 'sqlite') {
        if (-not (Test-Path $SqliteFile)) { throw "找不到 SQLite 数据库：$SqliteFile" }
        $target = Join-Path $OutDir "mes_sqlite_$stamp.db"
        Copy-Item $SqliteFile $target -Force
        Write-Host "已备份 SQLite：$target"
    }
    else {
        if ([string]::IsNullOrWhiteSpace($DbPassword)) {
            throw '缺少数据库密码。请设置环境变量 MES_DB_PASSWORD 或传 -DbPassword，避免把密码写进脚本。'
        }

        $target = Join-Path $OutDir "mes_mysql_$stamp.sql.gz"
        $tool = Get-MySqlDumpCommand

        if ($tool -eq 'docker') {
            # 走容器内的 mysqldump，避免现场机器没装客户端；密码经环境变量传入，不出现在命令行历史
            $tmp = Join-Path $OutDir "mes_mysql_$stamp.sql"
            docker exec -e "MYSQL_PWD=$DbPassword" $MySqlContainer `
                mysqldump --single-transaction --quick --routines --triggers --events `
                -u $DbUser $Database > $tmp
            if ($LASTEXITCODE -ne 0) { throw "mysqldump 失败（exit $LASTEXITCODE）" }
            Compress-Archive -Path $tmp -DestinationPath "$tmp.zip" -Force
            Remove-Item $tmp -Force
            $target = "$tmp.zip"
        }
        else {
            $tmp = Join-Path $OutDir "mes_mysql_$stamp.sql"
            $env:MYSQL_PWD = $DbPassword
            & $tool --single-transaction --quick --routines --triggers --events `
                -u $DbUser $Database > $tmp
            Remove-Item Env:\MYSQL_PWD -ErrorAction SilentlyContinue
            if ($LASTEXITCODE -ne 0) { throw "mysqldump 失败（exit $LASTEXITCODE）" }
            Compress-Archive -Path $tmp -DestinationPath "$tmp.zip" -Force
            Remove-Item $tmp -Force
            $target = "$tmp.zip"
        }

        $sizeMb = [math]::Round((Get-Item $target).Length / 1MB, 2)
        Write-Host "已备份 MySQL：$target（$sizeMb MB）"
    }

    # 过期清理：只删本脚本产出的文件，绝不遍历目录乱删
    $cutoff = (Get-Date).AddDays(-$KeepDays)
    Get-ChildItem $OutDir -File |
        Where-Object { $_.Name -like 'mes_sqlite_*' -or $_.Name -like 'mes_mysql_*' } |
        Where-Object { $_.LastWriteTime -lt $cutoff } |
        ForEach-Object {
            Remove-Item $_.FullName -Force
            Write-Host "清理过期备份：$($_.Name)"
        }
}
catch {
    Write-Error "备份失败：$($_.Exception.Message)"
    exit 1
}
