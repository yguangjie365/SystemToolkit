#!/usr/bin/env python3
"""从 IEEE 官方 OUI 注册表生成 SystemToolkit 内置的紧凑 OUI 表。

用法::

    python generate_oui.py                       # 直接下载（需能访问 standards-oui.ieee.org）
    python generate_oui.py --proxy http://127.0.0.1:7890
    python generate_oui.py --from-file oui.csv   # 用已下载的官方 CSV（离线复跑）

输出：``src/SystemToolkit.Core/Network/LanScan/Data/oui-ieee.csv``
格式：每行 ``<6位十六进制前缀>|<Organization Name>``，UTF-8 无 BOM，**只按前缀稳定排序**。

为什么是"只按前缀稳定排序"：IEEE 原表并非按前缀有序，且表内有 2 个前缀重复
（``080030``×3、``0001C8``×2）。若按"前缀+厂商名"排序，会把同前缀内的先后按字母重排，
等于**替 IEEE 做了取舍**；稳定排序则保留 IEEE 自身的次序，C# 侧再固定"取首条"。
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import io
import sys
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

SOURCE_URL = "https://standards-oui.ieee.org/oui/oui.csv"
DEFAULT_OUT = Path(__file__).resolve().parents[2] / "src" / "SystemToolkit.Core" / "Network" / "LanScan" / "Data" / "oui-ieee.csv"

HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
        "(KHTML, like Gecko) Chrome/126.0 Safari/537.36"
    ),
    "Accept": "text/csv,application/csv,text/plain,*/*",
    "Accept-Language": "en-US,en;q=0.9",
    "Referer": "https://standards-oui.ieee.org/",
}


def fetch(url: str, proxy: str | None, timeout: int) -> bytes:
    if proxy:
        opener = urllib.request.build_opener(
            urllib.request.ProxyHandler({"http": proxy, "https": proxy})
        )
    else:
        opener = urllib.request.build_opener()
    request = urllib.request.Request(url, headers=HEADERS)
    with opener.open(request, timeout=timeout) as response:
        return response.read()


def build(raw: bytes) -> tuple[bytes, int, int]:
    text = raw.decode("utf-8-sig", errors="replace")
    rows = list(csv.reader(io.StringIO(text)))
    data = [r for r in rows[1:] if len(r) >= 3 and r[1]]
    if not data:
        raise SystemExit("官方 CSV 解析结果为空——格式可能已变更，停止生成")

    # Python 的 sorted 是稳定排序：同前缀内保留 IEEE 原始次序
    ordered = sorted(data, key=lambda r: r[1])
    out = "".join(f"{r[1]}|{r[2]}\n" for r in ordered).encode("utf-8")
    return out, len(data), len({r[1] for r in data})


def main() -> int:
    parser = argparse.ArgumentParser(description="生成 SystemToolkit 内置 OUI 表")
    parser.add_argument("--proxy", default=None, help="HTTP(S) 代理，如 http://127.0.0.1:7890")
    parser.add_argument("--from-file", default=None, help="改用已下载的官方 CSV，跳过网络")
    parser.add_argument("--out", default=str(DEFAULT_OUT), help="输出路径")
    parser.add_argument("--timeout", type=int, default=180, help="下载超时（秒）")
    args = parser.parse_args()

    if args.from_file:
        raw = Path(args.from_file).read_bytes()
        origin = f"file:{args.from_file}"
    else:
        print(f"下载 {SOURCE_URL} …", flush=True)
        raw = fetch(SOURCE_URL, args.proxy, args.timeout)
        origin = SOURCE_URL

    out, row_count, prefix_count = build(raw)
    out_path = Path(args.out)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_bytes(out)

    print(f"来源           : {origin}")
    print(f"抓取时间(UTC)  : {datetime.now(timezone.utc).strftime('%Y-%m-%d %H:%M')}")
    print(f"官方 CSV        : {len(raw):,} 字节  sha256={hashlib.sha256(raw).hexdigest()}")
    print(f"数据行/唯一前缀 : {row_count:,} / {prefix_count:,}")
    print(f"生成文件        : {out_path}")
    print(f"生成体积        : {len(out):,} 字节  sha256={hashlib.sha256(out).hexdigest()}")
    duplicates = row_count - prefix_count
    print(f"重复前缀        : {duplicates} 条（C# 侧固定取首条）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
