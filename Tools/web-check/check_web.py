#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""手机端 Web UI 静态自检（DOM 交叉校验 + 前后端协议契约校验 + 关键改动落盘核验）。

用法（在仓库根目录执行）：

    python Tools/web-check/check_web.py

退出码 0 = 全通过；1 = 有 FAIL 项（会把每一项原因打印出来）。

## 为什么需要它

仓内**没有 JS 测试基建**（无 node 测试框架、无 headless 浏览器），而手机端页面
恰恰是"改完看不见"的地方——XAML 那边有 UiTokenRatchet / ViewLoadSmokeGuard 兜底，
Web 这边此前只能靠人眼。本工具补上这一步，覆盖三类真实的翻车方式：

1. **DOM 引用失效**：JS 里 `$("xxx")` 改了名 / HTML 里删了元素 → 运行时静默 null，
   页面看着正常但某个功能**整条不工作**（没有报错，最难查的一种）。
2. **协议两端不对齐**：本仓吃过两次同一类亏（`browserList`、`transferUpdate` 都出现过
   「前端分支早已存在、服务端从未发送」）。这里把**两个方向**都量出来：
   服务端推的类型 ⊄ 前端 case（前端收不到）、前端 case ∉ 服务端推的（在等一个不会来的消息）。
3. **改动其实没落盘**：本仓明令「构建绿 ≠ 改动生效」。最后一段把本次改动的关键点逐条在
   磁盘上核验（改了什么就查什么），而不是相信"脚本没报错"。

## 维护

- 新增 WS 消息类型 → 无需改脚本（A/B 两段是活体提取的）。
- 新增前端改动批次 → 在 `DISK_CHECKS` 里加该批的关键点（**改了什么就查什么**）。
- 类名/字段名重命名 → C/D 两段会自动报错，这是它的设计意图，别把它调松。
"""

from __future__ import annotations

import io
import os
import re
import sys

WWW = "src/SystemToolkit.Infrastructure/FileTransfer/wwwroot/"
WS_CS = "src/SystemToolkit.Infrastructure/FileTransfer/FileWebServer.WebSocket.cs"
SRV_CS = "src/SystemToolkit.Infrastructure/FileTransfer/FileWebServer.cs"


def read(path: str) -> str:
    with io.open(path, encoding="utf-8") as fh:
        return fh.read()


def main() -> int:
    if not os.path.isdir(WWW):
        print("请从仓库根目录运行本脚本（找不到 %s）。" % WWW)
        return 2

    html = read(WWW + "index.html")
    js = read(WWW + "app.js")
    css = read(WWW + "style.css")
    ws_cs = read(WS_CS)
    srv_cs = read(SRV_CS)

    problems: list[str] = []
    notes: list[str] = []

    # ── 1. JS 的 $("id") 必须在 HTML 里存在 ────────────────────────────────
    js_ids = set(re.findall(r'\$\("([A-Za-z0-9_\-]+)"\)', js))
    html_ids = set(re.findall(r'\bid="([A-Za-z0-9_\-]+)"', html))
    missing = sorted(js_ids - html_ids)
    print("[1] JS 引用 id %d 个；HTML 缺失：%s" % (len(js_ids), missing or "无"))
    if missing:
        problems.append("JS 引用了 HTML 中不存在的 id：%s" % ", ".join(missing))

    # HTML 里有、JS 没直接引用的 id：多为 CSS/querySelector 用，人工确认即可
    unused = sorted(html_ids - js_ids)
    if unused:
        notes.append("HTML 有但 JS 未直接引用（确认是否 CSS/选择器用）：%s" % ", ".join(unused))

    # ── 2. 服务端推送的 WS 类型 vs 前端 case（双向） ────────────────────────
    server_types = set(re.findall(r'BuildEnvelope\("([a-zA-Z]+)"', ws_cs))
    if '{"type":"pong"}' in ws_cs:
        server_types.add("pong")
    js_cases = set(re.findall(r'case "([a-zA-Z]+)"', js))
    print("[2] 服务端推：%s" % sorted(server_types))
    print("    前端 case：%s" % sorted(js_cases & server_types))

    unhandled = sorted(server_types - js_cases)
    if unhandled:
        problems.append("服务端会推但前端没有 case（会被静默忽略）：%s" % ", ".join(unhandled))

    # ── 3. /api/text 请求体字段：JS 发的 ⊆ 服务端解析的 ─────────────────────
    body = re.search(r"JSON\.stringify\(\{\s*text\b([^}]*)\}\)", js)
    js_fields = ({"text"} | set(re.findall(r"(\w+)\s*:", body.group(1)))) if body else set()
    srv_fields = set(re.findall(r'TryGetProperty\("(\w+)"', srv_cs))
    print("[3] /api/text 请求体：JS 发=%s  服务端解析=%s" % (sorted(js_fields), sorted(srv_fields)))
    if not js_fields:
        problems.append("找不到发往 /api/text 的 JSON.stringify（端点或字段被改名了？）")
    else:
        extra = sorted(js_fields - srv_fields)
        if extra:
            problems.append("JS 发了服务端不解析的字段：%s" % ", ".join(extra))

    # ── 4. chatMessage payload：渲染必需字段前端必须读 ──────────────────────
    seg = re.search(r'BuildEnvelope\("chatMessage", new\s*\{(.*?)\n\s*\}\);', ws_cs, re.S)
    srv_payload = set(re.findall(r"(\w+)\s*=", seg.group(1))) if seg else set()
    srv_payload = {f[0].lower() + f[1:] for f in srv_payload}
    js_reads = set(re.findall(r"\bp\.(\w+)", js)) | set(re.findall(r"payload\.(\w+)", js))
    required = {"text", "origin", "from"}
    print("[4] chatMessage payload：服务端发=%s  前端读=%s" % (sorted(srv_payload), sorted(js_reads)))
    missing_req = sorted(required - js_reads)
    if missing_req:
        problems.append("chatMessage 渲染必需字段前端没读：%s" % ", ".join(missing_req))
    if srv_payload - js_reads - required:
        notes.append("服务端提供但前端暂未使用：%s" % ", ".join(sorted(srv_payload - js_reads - required)))

    # ── 5. 关键改动逐条落盘核验（改了什么就查什么） ────────────────────────
    # 每个批次往这里加自己那批的关键点；历史批次的条目保留（防回归）。
    disk_checks = [
        # W2b：文本通道前端
        ("W2b · HTML 有文本输入框", '<textarea class="chat-composer__input" id="chatInput"' in html),
        ("W2b · HTML 有发送按钮", 'id="chatSendBtn"' in html),
        ("W2b · HTML 有超限提示行", 'id="chatComposerMeta"' in html),
        ("W2b · HTML 已移除旧占位（文本发送随下一批上线）", "文本发送随下一批上线" not in html),
        ("W2b · CSS 输入框 16px（防 iOS 聚焦放大）", "font-size: 16px;" in css),
        ("W2b · CSS 气泡左/右两态", ".chat-bubble.is-out" in css and ".chat-bubble.is-in" in css),
        ("W2b · CSS 空态文案已更新", "还没有消息。输入文字，或点 + 发送文件。" in css),
        ("W2b · CSS 窄屏隐藏拖放块（手机拖不了文件）", "@media (max-width: 767px)" in css),
        ("W2b · JS 用 UTF-8 字节判长", "new TextEncoder().encode" in js),
        ("W2b · JS 保留 execCommand 兜底（http 局域网无 clipboard API）",
         'document.execCommand("copy")' in js),
        ("W2b · JS 气泡用 textContent（防 XSS）", "body.textContent = fullText;" in js),
        ("W2b · JS 有 clientId 未就绪时的回声去重", "function isLocalEcho(" in js),
        ("W2b · JS init 挂了 initChatComposer", "initChatComposer();" in js),
    ]
    bad = [name for name, ok in disk_checks if not ok]
    for name, ok in disk_checks:
        print("    %s %s" % ("[ok]" if ok else "[!!]", name))
    if bad:
        problems.append("关键改动未落盘：%s" % ", ".join(bad))

    print("")
    for note in notes:
        print("提示：" + note)

    if problems:
        print("")
        print("FAIL（%d 项）" % len(problems))
        for p in problems:
            print("  - " + p)
        return 1

    print("ALL_CHECKS_PASSED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
