"""MES 数据备份（跨平台，Windows/Linux 通用）。

用法：
  python scripts/backup.py --provider sqlite
  python scripts/backup.py --provider mysql --password *** --database mes_server_power
  python scripts/backup.py --provider mysql --container mes-mysql --password ***   # 走 docker exec，机器没装 mysql 客户端也能备份

环境变量 MES_DB_PASSWORD 可替代 --password，避免密码进命令行历史。
保留策略：默认保留 30 天，只清理本脚本产出的 mes_* 文件。
"""
import argparse
import gzip
import os
import shutil
import sqlite3
import subprocess
import sys
from datetime import datetime, timedelta
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_SQLITE = ROOT / "backend" / "MesApi" / "mes.db"


def backup_sqlite(src: Path, out_dir: Path) -> Path:
    """用 SQLite 在线备份 API，而不是直接 copy 文件。

    WAL 模式下未 checkpoint 的数据在 mes.db-wal 里，直接复制主文件会丢最近几分钟的写入。
    backup() 会先拿到一致性快照，服务运行中也可安全备份。
    """
    if not src.exists():
        raise SystemExit(f"找不到 SQLite 数据库：{src}")

    target = out_dir / f"mes_sqlite_{datetime.now():%Y%m%d_%H%M%S}.db"
    source = sqlite3.connect(f"file:{src}?mode=ro", uri=True)
    try:
        dest = sqlite3.connect(target)
        try:
            source.backup(dest)
        finally:
            dest.close()
    finally:
        source.close()
    return target


def backup_mysql(out_dir: Path, database: str, user: str, password: str, container: str | None) -> Path:
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    target = out_dir / f"mes_mysql_{stamp}.sql.gz"
    env = {**os.environ, "MYSQL_PWD": password}

    if container:
        cmd = [
            "docker", "exec", "-e", f"MYSQL_PWD={password}", container,
            "mysqldump", "--single-transaction", "--quick", "--routines", "--triggers", "--events",
            "-u", user, database,
        ]
    else:
        if not shutil.which("mysqldump"):
            raise SystemExit("未找到 mysqldump，也没有指定 --container。请安装 MySQL 客户端或使用 docker 方式。")
        cmd = [
            "mysqldump", "--single-transaction", "--quick", "--routines", "--triggers", "--events",
            "-u", user, database,
        ]

    proc = subprocess.run(cmd, env=env, capture_output=True)
    if proc.returncode != 0:
        raise SystemExit(f"mysqldump 失败：{proc.stderr.decode('utf-8', 'replace')[:500]}")

    with gzip.open(target, "wb") as f:
        f.write(proc.stdout)
    return target


def cleanup(out_dir: Path, keep_days: int) -> None:
    cutoff = datetime.now() - timedelta(days=keep_days)
    for f in out_dir.iterdir():
        if not f.is_file():
            continue
        if not (f.name.startswith("mes_sqlite_") or f.name.startswith("mes_mysql_")):
            continue
        if datetime.fromtimestamp(f.stat().st_mtime) < cutoff:
            f.unlink()
            print(f"清理过期备份：{f.name}")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--provider", choices=["mysql", "sqlite"], default="mysql")
    ap.add_argument("--out", default=str(ROOT / "backups"))
    ap.add_argument("--keep-days", type=int, default=30)
    ap.add_argument("--database", default="mes_server_power")
    ap.add_argument("--user", default="mes")
    ap.add_argument("--password", default=os.environ.get("MES_DB_PASSWORD", ""))
    ap.add_argument("--container", default="mes-mysql")
    ap.add_argument("--sqlite-file", default=str(DEFAULT_SQLITE))
    ap.add_argument("--no-docker", action="store_true", help="强制使用本机 mysqldump")
    args = ap.parse_args()

    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)

    if args.provider == "sqlite":
        target = backup_sqlite(Path(args.sqlite_file), out_dir)
    else:
        if not args.password:
            raise SystemExit("缺少数据库密码：设置 MES_DB_PASSWORD 或用 --password 传入")
        container = None if args.no_docker or not shutil.which("docker") else args.container
        target = backup_mysql(out_dir, args.database, args.user, args.password, container)

    size_mb = round(target.stat().st_size / 1024 / 1024, 2)
    print(f"已备份：{target}（{size_mb} MB）")
    cleanup(out_dir, args.keep_days)
    return 0


if __name__ == "__main__":
    sys.exit(main())
