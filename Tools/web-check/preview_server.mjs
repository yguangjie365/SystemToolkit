/**
 * 手机端页面**本地预览**服务（不引第三方依赖，纯 node 内置模块）。
 *
 * 用途：真机验收前的"看一眼"。手机端页面改完，仓内没有 headless 浏览器、
 * 也没有 JS 测试基建，肉眼看不到效果 —— 这个服务把 wwwroot 静态服务起来，
 * 并把页面初始化要用的几个端点 mock 成"已配对 + 空数据"，于是能直接看到
 * 对话页的真实布局（底部输入框、气泡区、Tab）。
 *
 * 🔴 它**不是**真机模拟：
 *   - `/ws` 直接拒绝升级 → 页面停在"未连接"，但静态布局完整可看；
 *   - 手机上的安全区、字体、iOS 聚焦放大等只有真机能验；
 *   - 剪贴板只剩 `execCommand` 路径（localhost 属安全上下文，走的是异步 API）。
 *   真机验收仍须扫码访问电脑上真实运行的服务。
 *
 * 用法（仓库根目录）：
 *     node Tools/web-check/preview_server.mjs            # 默认 8730
 *     node Tools/web-check/preview_server.mjs 9000       # 指定端口
 * 然后浏览器打开打印出来的 http://127.0.0.1:<port>/?c=preview
 */

import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const WWW = path.resolve(HERE, "../../src/SystemToolkit.Infrastructure/FileTransfer/wwwroot");
const PORT = Number(process.argv[2] || 8730);

const MIME = {
    ".html": "text/html; charset=utf-8",
    ".js": "application/javascript; charset=utf-8",
    ".css": "text/css; charset=utf-8",
    ".svg": "image/svg+xml",
    ".json": "application/json; charset=utf-8",
};

function sendJson(res, body, status = 200) {
    const text = JSON.stringify(body);
    res.writeHead(status, { "Content-Type": MIME[".json"], "Cache-Control": "no-store" });
    res.end(text);
}

const server = http.createServer((req, res) => {
    const url = new URL(req.url, `http://127.0.0.1:${PORT}`);
    const route = url.pathname;

    // 页面初始化用到的端点：mock 成"已配对 + 空数据"
    if (route === "/api/pair" && req.method === "POST") {
        return sendJson(res, { token: "preview-token", remembered: false, expiresAtUtcMs: 0 });
    }
    if (route === "/api/cert-info") {
        return sendJson(res, { https: false, fingerprint: "", subject: "", notAfterUtcMs: 0 });
    }
    if (route === "/api/files") {
        return sendJson(res, []);
    }
    if (route === "/api/devices") {
        return sendJson(res, []);
    }
    if (route === "/api/sessions/forget") {
        return sendJson(res, { forgotten: true });
    }
    // 实时推送：拒绝升级（页面停在"未连接"，但布局完整）
    if (route === "/ws") {
        req.socket.destroy();
        return;
    }

    // 静态外壳
    const name = route === "/" ? "index.html" : route.replace(/^\//, "");
    const file = path.join(WWW, name);
    if (!file.startsWith(WWW)) {
        res.writeHead(403).end("forbidden");
        return;
    }
    fs.readFile(file, (err, data) => {
        if (err) {
            res.writeHead(404, { "Content-Type": "text/plain; charset=utf-8" });
            res.end("not found: " + name);
            return;
        }
        res.writeHead(200, {
            "Content-Type": MIME[path.extname(file)] || "application/octet-stream",
            "Cache-Control": "no-store",
        });
        res.end(data);
    });
});

// 只绑回环：这是本机预览工具，不对外提供服务
server.listen(PORT, "127.0.0.1", () => {
    console.log(`预览服务已启动：http://127.0.0.1:${PORT}/?c=preview`);
    console.log(`静态目录：${WWW}`);
});
