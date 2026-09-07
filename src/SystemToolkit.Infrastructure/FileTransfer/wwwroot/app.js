/* ============================================================
   app.js —— 文件互传 Web UI 逻辑
   纯原生 JS，无框架依赖
   API 同源（页面 origin 即为后端地址）
   ============================================================ */
(function () {
    "use strict";

    /* ============ 配置 ============ */
    // API 基地址：当前页面 origin（同源访问）
    const API_BASE = window.location.origin;
    // 访问令牌：优先用本机已保存的（扫码配对后写入 localStorage）。
    // URL 上只允许出现**短期配对码**（?c=...），不再放长期令牌——二维码容易被截图外泄。
    const TOKEN_STORAGE_KEY = "stk_web_token";
    let TOKEN = localStorage.getItem(TOKEN_STORAGE_KEY) || "";
    // 兼容旧的直接带令牌的链接（如电脑端"打开网页"按钮）
    const TOKEN_FROM_URL = new URLSearchParams(window.location.search).get("t") || "";
    if (TOKEN_FROM_URL) TOKEN = TOKEN_FROM_URL;
    // 单文件上传上限（与服务端 FileWebServer.UploadLimitBytes 一致）：10GB
    // 2026-09-02 由 30MB 上调：上传已改为原始 body 流式直写，不再受 multipart 缓冲限制。
    const UPLOAD_MAX_BYTES = 10 * 1024 * 1024 * 1024;
    // 上传端点：分块上传（查已传偏移 / 逐块追加），文件名与元信息走查询参数
    const UPLOAD_STATUS_ENDPOINT = "/api/files/upload-status";
    const UPLOAD_CHUNK_ENDPOINT = "/api/files/upload-chunk";
    // 分块大小：与服务端 ChunkSizeBytes 一致（8MB）
    const UPLOAD_CHUNK_BYTES = 8 * 1024 * 1024;
    // WebSocket 地址：ws(s)://当前主机/ws（带令牌）。
    // 必须是函数而非常量——配对是异步的，TOKEN 在页面初始化时才会拿到（扫码前为空）。
    const wsUrl = () => (window.location.protocol === "https:" ? "wss://" : "ws://")
        + window.location.host + "/ws"
        + (TOKEN ? "?t=" + encodeURIComponent(TOKEN) : "");

    // 设备列表自动刷新间隔
    const DEVICE_REFRESH_MS = 5000;
    // WebSocket 重连间隔
    const WS_RECONNECT_MS = 3000;
    // 心跳间隔
    const WS_HEARTBEAT_MS = 25000;

    /* ============ DOM 缓存 ============ */
    const $ = (id) => document.getElementById(id);
    const dom = {
        connDot: $("connDot"),
        connText: $("connText"),
        hostInfo: $("hostInfo"),
        breadcrumb: $("breadcrumb"),
        fileList: $("fileList"),
        uploadZone: $("uploadZone"),
        pickBtn: $("pickBtn"),
        fileInput: $("fileInput"),
        pickDirBtn: $("pickDirBtn"),
        dirInput: $("dirInput"),
        uploadList: $("uploadList"),
        multiToggleBtn: $("multiToggleBtn"),
        multiBar: $("multiBar"),
        multiCount: $("multiCount"),
        multiZipBtn: $("multiZipBtn"),
        multiExitBtn: $("multiExitBtn"),
        deviceList: $("deviceList"),
        deviceCount: $("deviceCount"),
        toast: $("toast"),
        tabs: Array.from(document.querySelectorAll(".tab-bar__item")),
        panels: Array.from(document.querySelectorAll(".tab-panel")),
    };

    /* ============ 应用状态 ============ */
    const state = {
        // 文件浏览：当前相对路径
        currentPath: "",
        // 多选模式（打包下载）
        multiSelect: false,
        // 选中项：相对路径 → 字节大小（目录记 0），用于打包下载与体积预估
        selected: new Map(),
        // 路径历史栈（支持后退）
        history: [],
        // 已发现的设备（WebSocket 推送累积）
        devices: [],
        // 在线浏览器（WebSocket 推送累积）
        browsers: [],
        // WebSocket 实例
        ws: null,
        // WebSocket 是否为用户主动关闭
        wsClosedByUser: false,
        // 心跳定时器
        heartbeatTimer: null,
        // 重连定时器
        reconnectTimer: null,
        // 设备刷新定时器
        deviceTimer: null,
        // 当前激活的标签
        activeTab: "browse",
    };

    /* ============================================================
       工具函数
       ============================================================ */

    /**
     * 友好显示文件大小
     * @param {number} bytes 字节数
     * @returns {string} 如 "1.23 MB"
     */
    function formatFileSize(bytes) {
        if (bytes == null || isNaN(bytes)) return "—";
        if (bytes < 0) return "—";
        if (bytes === 0) return "0 B";
        const units = ["B", "KB", "MB", "GB", "TB", "PB"];
        const i = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
        const v = bytes / Math.pow(1024, i);
        // B 整数显示，其余保留 1-2 位
        const str = i === 0 ? v.toFixed(0) : v.toFixed(v < 10 ? 2 : 1);
        return str + " " + units[i];
    }

    /**
     * 友好时间显示
     * @param {number|string|Date} ts 时间戳 / ISO 字符串 / Date
     * @returns {string} 如 "2026-09-01 12:34" 或 "3 分钟前"
     */
    function formatTime(ts) {
        if (!ts) return "—";
        let d;
        try {
            d = (ts instanceof Date) ? ts : new Date(ts);
        } catch (e) {
            return "—";
        }
        if (isNaN(d.getTime())) return "—";

        const now = new Date();
        const diffMs = now.getTime() - d.getTime();
        const diffMin = Math.floor(diffMs / 60000);

        // 未来或 1 分钟内：刚刚
        if (diffMs < 60000 && diffMs > -60000) {
            return "刚刚";
        }
        // 1 小时内：X 分钟前
        if (diffMin > 0 && diffMin < 60) {
            return diffMin + " 分钟前";
        }
        // 24 小时内：X 小时前
        const diffHour = Math.floor(diffMin / 60);
        if (diffHour > 0 && diffHour < 24) {
            return diffHour + " 小时前";
        }
        // 超过 1 天：YYYY-MM-DD HH:mm
        const pad = (n) => String(n).padStart(2, "0");
        return d.getFullYear() + "-"
            + pad(d.getMonth() + 1) + "-"
            + pad(d.getDate()) + " "
            + pad(d.getHours()) + ":"
            + pad(d.getMinutes());
    }

    /**
     * HTML 转义（XSS 防护）
     */
    function escapeHtml(str) {
        if (str == null) return "";
        return String(str)
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;")
            .replace(/'/g, "&#39;");
    }

    /**
     * URL 安全白名单：仅接受 http/https 协议 URL，防止 javascript:/data: 等协议注入
     */
    function safeWebUrl(url) {
        if (typeof url !== "string" || !url) return "";
        try {
            var u = new URL(url);
            if (u.protocol === "http:" || u.protocol === "https:") return url;
        } catch (_) { /* 非 URL 字符串 */ }
        return "";
    }

    /**
     * URL 拼接（带 query 参数）
     */
    function buildUrl(path, params) {
        const url = new URL(path, API_BASE);
        if (TOKEN) url.searchParams.set("t", TOKEN);
        if (params) {
            Object.keys(params).forEach((k) => {
                if (params[k] != null && params[k] !== "") {
                    url.searchParams.set(k, params[k]);
                }
            });
        }
        return url.toString();
    }

    /**
     * Toast 提示
     */
    let toastTimer = null;
    function showToast(msg, type) {
        const el = dom.toast;
        el.textContent = msg;
        el.className = "toast is-show" + (type ? " is-" + type : "");
        clearTimeout(toastTimer);
        toastTimer = setTimeout(() => {
            el.className = "toast" + (type ? " is-" + type : "");
        }, 2400);
    }

    /* ============================================================
       SVG 图标（按文件类型返回）
       ============================================================ */
    const ICONS = {
        folder: '<svg width="32" height="32" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M3 7a2 2 0 012-2h4l2 2h8a2 2 0 012 2v8a2 2 0 01-2 2H5a2 2 0 01-2-2V7z"/></svg>',
        file: '<svg width="32" height="32" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M14 3H7a2 2 0 00-2 2v14a2 2 0 002 2h10a2 2 0 002-2V8z"/><path d="M14 3v5h5"/></svg>',
        image: '<svg width="32" height="32" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="16" rx="2"/><circle cx="9" cy="10" r="2"/><path d="M3 17l5-4 4 3 4-4 5 5"/></svg>',
        video: '<svg width="32" height="32" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="5" width="14" height="14" rx="2"/><path d="M17 9l4-2v10l-4-2z"/></svg>',
        audio: '<svg width="32" height="32" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M9 18V6l10-2v12"/><circle cx="6" cy="18" r="3"/><circle cx="16" cy="16" r="3"/></svg>',
        doc: '<svg width="32" height="32" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M14 3H7a2 2 0 00-2 2v14a2 2 0 002 2h10a2 2 0 002-2V8z"/><path d="M14 3v5h5"/><path d="M9 13h6M9 17h4"/></svg>',
        archive: '<svg width="32" height="32" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M21 8l-9-5-9 5 9 5 9-5z"/><path d="M3 8v8l9 5 9-5V8"/></svg>',
        chevron: '<svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="9 6 15 12 9 18"/></svg>',
    };

    /**
     * 按扩展名选择图标
     */
    function iconForFile(name, isDir) {
        if (isDir) return ICONS.folder;
        const ext = (name.split(".").pop() || "").toLowerCase();
        if (["jpg", "jpeg", "png", "gif", "bmp", "webp", "svg"].includes(ext)) return ICONS.image;
        if (["mp4", "mkv", "avi", "mov", "webm"].includes(ext)) return ICONS.video;
        if (["mp3", "wav", "flac", "ogg", "m4a"].includes(ext)) return ICONS.audio;
        if (["txt", "md", "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx"].includes(ext)) return ICONS.doc;
        if (["zip", "rar", "7z", "tar", "gz"].includes(ext)) return ICONS.archive;
        return ICONS.file;
    }

    /* ============================================================
       标签页切换
       ============================================================ */
    function switchTab(name) {
        state.activeTab = name;
        dom.tabs.forEach((t) => {
            const active = t.dataset.tab === name;
            t.classList.toggle("is-active", active);
            t.setAttribute("aria-selected", active ? "true" : "false");
        });
        dom.panels.forEach((p) => {
            p.classList.toggle("is-active", p.dataset.tab === name);
        });
    }

    /* ============================================================
       文件浏览
       ============================================================ */

    /**
     * 统一处理「令牌已失效」：清掉本地存的失效令牌并给出明确指引。
     * 【第四轮审查 R5 修复】电脑端服务重启会换新令牌，手机书签里的旧令牌全部 401——
     * 此前只显示"加载失败：HTTP 401"，用户不知道该干什么。
     */
    function handleAuthFailure() {
        if (TOKEN) {
            localStorage.removeItem(TOKEN_STORAGE_KEY);
            TOKEN = "";
        }
        showToast("访问令牌已失效（电脑端重启过 Web 服务），请重新扫码", "error");
    }

    /**
     * 拉取指定路径下的文件列表
     * @param {string} relPath 相对路径
     */
    async function fetchFiles(relPath) {
        const listEl = dom.fileList;
        listEl.innerHTML = renderSkeleton(6);

        try {
            const url = buildUrl("/api/files", { path: relPath || "" });
            const resp = await fetch(url, { cache: "no-store" });
            if (resp.status === 401) {
                handleAuthFailure();
                throw new Error("未授权（请重新扫码）");
            }
            if (!resp.ok) {
                throw new Error("HTTP " + resp.status + " " + resp.statusText);
            }
            const data = await resp.json();
            renderFileList(Array.isArray(data) ? data : []);
        } catch (err) {
            listEl.innerHTML = "";
            const empty = document.createElement("div");
            empty.className = "empty-state";
            empty.innerHTML = '<div class="empty-state__icon" aria-hidden="true">'
                + '<svg width="48" height="48" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"><path d="M12 9v4M12 17h.01M10.3 3.9L1.8 18a2 2 0 001.7 3h17a2 2 0 001.7-3L13.7 3.9a2 2 0 00-3.4 0z"/></svg>'
                + '</div><div class="empty-state__text">加载失败：' + escapeHtml(err.message) + "</div>";
            listEl.appendChild(empty);
        }
    }

    /**
     * 渲染骨架屏（加载占位）
     */
    function renderSkeleton(n) {
        let html = "";
        for (let i = 0; i < n; i++) {
            html += '<div class="skeleton-item"></div>';
        }
        return html;
    }

    /**
     * 渲染文件列表
     * @param {Array} files FileShareEntry 数组
     */
    function renderFileList(files) {
        const listEl = dom.fileList;
        listEl.innerHTML = "";

        // 排序：目录优先，再按名称
        files.sort((a, b) => {
            if (a.isDirectory !== b.isDirectory) return a.isDirectory ? -1 : 1;
            return a.name.localeCompare(b.name, "zh-CN");
        });

        if (files.length === 0) {
            const empty = document.createElement("div");
            empty.className = "empty-state";
            empty.innerHTML = '<div class="empty-state__text">此目录为空</div>';
            listEl.appendChild(empty);
            return;
        }

        const frag = document.createDocumentFragment();
        files.forEach((f) => {
            const item = document.createElement("div");
            item.className = "file-item";
            item.setAttribute("role", "listitem");
            item.tabIndex = 0;
            const iconClass = f.isDirectory ? "file-item__icon is-dir" : "file-item__icon";
            const meta = f.isDirectory
                ? '<span>' + (f.itemCount ? f.itemCount + ' 项' : '目录') + '</span>'
                : '<span>' + formatFileSize(f.size) + '</span>';
            const time = formatTime(f.lastModified);
            const relPath = f.relativePath || f.name;

            item.innerHTML =
                (state.multiSelect
                    ? '<label class="file-item__check"><input type="checkbox" '
                        + (state.selected.has(relPath) ? 'checked' : '')
                        + ' aria-label="选择 ' + escapeHtml(f.name) + '"></label>'
                    : '')
                + '<span class="' + iconClass + '" aria-hidden="true">'
                + iconForFile(f.name, f.isDirectory)
                + '</span>'
                + '<div class="file-item__body">'
                + '  <div class="file-item__name" title="' + escapeHtml(f.name) + '">'
                + escapeHtml(f.name) + '</div>'
                + '  <div class="file-item__meta">' + meta + '<span>' + time + '</span></div>'
                + '</div>'
                + (f.isDirectory
                    ? '<span class="file-item__chevron" aria-hidden="true">' + ICONS.chevron + '</span>'
                    : '');

            const checkbox = item.querySelector('input[type="checkbox"]');
            if (checkbox) {
                // 勾选不能连带触发「进入目录 / 下载」
                checkbox.addEventListener("click", (e) => e.stopPropagation());
                checkbox.addEventListener("change", () => {
                    if (checkbox.checked) state.selected.set(relPath, f.size || 0);
                    else state.selected.delete(relPath);
                    updateMultiSelectBar();
                });
            }

            // 点击 / 回车：目录进入，文件下载
            const handleActivate = () => {
                if (state.multiSelect) {
                    if (checkbox) {
                        checkbox.checked = !checkbox.checked;
                        checkbox.dispatchEvent(new Event("change"));
                    }
                    return;
                }
                if (f.isDirectory) {
                    enterDirectory(relPath);
                } else {
                    downloadFile(f);
                }
            };
            item.addEventListener("click", handleActivate);
            item.addEventListener("keydown", (e) => {
                if (e.key === "Enter" || e.key === " ") {
                    e.preventDefault();
                    handleActivate();
                }
            });
            frag.appendChild(item);
        });
        listEl.appendChild(frag);
        updateMultiSelectBar();
    }

    /**
     * 多选工具条：全选 / 下载所选 / 退出
     */
    function updateMultiSelectBar() {
        const bar = dom.multiBar;
        if (!bar) return;
        bar.hidden = !state.multiSelect;
        const countEl = dom.multiCount;
        if (countEl) countEl.textContent = String(state.selected.size);
        const zipBtn = dom.multiZipBtn;
        if (zipBtn) zipBtn.disabled = state.selected.size === 0;
    }

    function toggleMultiSelect(on) {
        state.multiSelect = !!on;
        if (!state.multiSelect) state.selected.clear();
        if (dom.multiToggleBtn) dom.multiToggleBtn.textContent = state.multiSelect ? "退出多选" : "多选";
        updateMultiSelectBar();
        fetchFiles(state.currentPath);
    }

    function downloadSelectedAsZip() {
        if (state.selected.size === 0) return;
        const paths = Array.from(state.selected.keys()).join("|");
        let total = 0;
        state.selected.forEach((size) => { total += size; });
        // 大体积打包会长时间占用连接且失败即前功尽弃，先给用户一个明确预期
        if (total > 2 * 1024 * 1024 * 1024) {
            showToast("所选超过 2GB，建议逐个下载或分批次打包", "error");
            return;
        }
        showToast("正在打包 " + state.selected.size + " 项…", "info");
        window.location.href = buildUrl("/api/files/download-zip", { paths: paths });
    }

    /**
     * 进入子目录
     */
    function enterDirectory(relPath) {
        if (state.currentPath) {
            state.history.push(state.currentPath);
        }
        state.currentPath = relPath || "";
        renderBreadcrumb();
        fetchFiles(state.currentPath);
    }

    /**
     * 后退到上一层
     */
    function goBack() {
        if (state.history.length === 0) {
            state.currentPath = "";
        } else {
            state.currentPath = state.history.pop();
        }
        renderBreadcrumb();
        fetchFiles(state.currentPath);
    }

    /**
     * 跳转到面包屑指定层级
     */
    function goToPath(relPath) {
        state.history = state.history.slice(0, state.history.indexOf(relPath) + 1);
        state.currentPath = relPath || "";
        renderBreadcrumb();
        fetchFiles(state.currentPath);
    }

    /**
     * 渲染面包屑导航
     */
    function renderBreadcrumb() {
        const bc = dom.breadcrumb;
        bc.innerHTML = "";

        // 根目录
        const root = document.createElement("button");
        root.className = "breadcrumb__item" + (state.currentPath === "" ? " is-current" : "");
        root.textContent = "共享根目录";
        root.addEventListener("click", () => {
            state.history.push(state.currentPath);
            state.currentPath = "";
            renderBreadcrumb();
            fetchFiles("");
        });
        bc.appendChild(root);

        // 拆分当前路径为多级
        if (state.currentPath) {
            const parts = state.currentPath.split(/[\/\\]/).filter(Boolean);
            let acc = "";
            parts.forEach((p, i) => {
                acc = acc ? acc + "/" + p : p;
                const isLast = i === parts.length - 1;

                const sep = document.createElement("span");
                sep.className = "breadcrumb__sep";
                sep.textContent = "/";
                bc.appendChild(sep);

                const item = document.createElement("button");
                item.className = "breadcrumb__item" + (isLast ? " is-current" : "");
                item.textContent = p;
                if (!isLast) {
                    const target = acc;
                    item.addEventListener("click", () => goToPath(target));
                }
                bc.appendChild(item);
            });
        }
    }

    /**
     * 下载文件
     */
    function downloadFile(file) {
        const params = new URLSearchParams({ path: file.relativePath || file.name });
        if (TOKEN) params.set("t", TOKEN);
        const url = API_BASE + "/api/files/download?" + params.toString();
        // 利用 a 标签触发下载，避免 fetch 大文件占内存
        const a = document.createElement("a");
        a.href = url;
        a.download = file.name || "";
        // 不追加到 DOM 也能在大多数浏览器触发下载
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        showToast("开始下载：" + file.name, "info");
    }

    /* ============================================================
       上传
       ============================================================ */

    /**
     * 上传单个文件：分块 + 断点续传
     *
     * 流程：查询已传偏移 → 从断点处逐块上传 → 最后一块写完由服务端定稿（算 SHA-256 + 原子改名）。
     * 中断后重新选择同一个文件即可续传：服务端按「文件名+大小+修改时间」指纹识别同一任务，
     * 已传的部分不会重来。用 XHR 而非 fetch 是为了拿 upload progress 事件。
     *
     * @param {File} file
     */
    async function uploadFile(file) {
        // 客户端预检：与服务端上限对齐，避免大文件白传一趟
        if (file.size > UPLOAD_MAX_BYTES) {
            showToast(file.name + "：超过 " + formatFileSize(UPLOAD_MAX_BYTES) + " 上传上限", "error");
            return;
        }

        const taskId = "up-" + Date.now() + "-" + Math.random().toString(36).slice(2, 8);
        const taskEl = createUploadTaskElement(taskId, file);
        dom.uploadList.appendChild(taskEl);

        const statusEl = taskEl.querySelector(".upload-task__status");
        const barEl = taskEl.querySelector(".progress__bar");
        const mtime = file.lastModified || 0;
        // 目录上传时带相对路径（photos/2024/a.jpg），普通上传退化成纯文件名
        const relName = file.webkitRelativePath || file.name;
        const meta = { name: relName, size: file.size, mtime: mtime };

        const setProgress = (loaded) => {
            const pct = file.size > 0 ? Math.round((loaded / file.size) * 100) : 100;
            barEl.style.width = pct + "%";
            statusEl.textContent = pct + "%";
            barEl.classList.toggle("is-active", pct < 100);
        };
        const fail = (msg) => {
            barEl.classList.remove("is-active");
            statusEl.textContent = "失败";
            statusEl.className = "upload-task__status is-fail";
            showToast(file.name + "：" + msg, "error");
        };

        try {
            // ① 查询已传偏移（断点续传的起点）
            let offset = 0;
            try {
                const resp = await fetch(buildUrl(UPLOAD_STATUS_ENDPOINT, meta));
                if (resp.ok) {
                    const st = await resp.json();
                    offset = Math.min(st.received || 0, file.size);
                    if (offset > 0) {
                        setProgress(offset);
                        showToast(file.name + "：从 " + formatFileSize(offset) + " 处继续上传", "info");
                    }
                }
            } catch (e) {
                /* 查询失败按从 0 开始处理，首块写入时服务端会校验偏移 */
            }

            // ② 逐块上传
            while (offset < file.size) {
                const end = Math.min(offset + UPLOAD_CHUNK_BYTES, file.size);
                const blob = file.slice(offset, end);
                const chunkBase = offset;

                const ok = await postChunkWithRetry(blob, meta, chunkBase, (loaded) => {
                    setProgress(chunkBase + loaded);
                }, fail);
                if (!ok) return;

                offset = end;
            }

            // ③ 完成
            barEl.classList.remove("is-active");
            barEl.style.width = "100%";
            statusEl.textContent = "完成";
            statusEl.className = "upload-task__status is-done";
            if (state.activeTab === "browse") {
                fetchFiles(state.currentPath);
            }
        } catch (e) {
            // 兜底：作为 forEach 回调时返回的 Promise 无人接管，异常必须自己吃掉
            fail("上传异常：" + ((e && e.message) ? e.message : String(e)));
        }
    }

    /**
     * 上传单个分块（失败自动重试，指数退避）
     * @param {Blob} blob 本块数据
     * @param {{name:string,size:number,mtime:number}} meta
     * @param {number} offset 本块在文件中的起始偏移
     * @param {(loaded:number)=>void} onProgress
     * @param {(msg:string)=>void} onFail
     * @returns {Promise<boolean>} 是否成功（false 表示已放弃并提示过用户）
     */
    function postChunkWithRetry(blob, meta, offset, onProgress, onFail) {
        const maxRetry = 3;
        return new Promise((resolve) => {
            let attempt = 0;
            const send = () => {
                const xhr = new XMLHttpRequest();
                xhr.open("POST", buildUrl(UPLOAD_CHUNK_ENDPOINT,
                    { name: meta.name, size: meta.size, mtime: meta.mtime, offset: offset }), true);
                xhr.setRequestHeader("Content-Type", "application/octet-stream");

                xhr.upload.addEventListener("progress", (e) => {
                    if (e.lengthComputable) onProgress(e.loaded);
                });

                xhr.addEventListener("load", () => {
                    if (xhr.status >= 200 && xhr.status < 300) {
                        let j = null;
                        try { j = JSON.parse(xhr.responseText); } catch (e) { /* 忽略 */ }
                        if (j && j.done) {
                            showToast("上传完成：" + meta.name
                                + (j.hash ? "（校验 " + String(j.hash).slice(0, 12) + "…）" : ""), "success");
                        }
                        resolve(true);
                        return;
                    }

                    let msg = "上传失败";
                    try {
                        const j = JSON.parse(xhr.responseText);
                        if (j && j.error) msg = j.error;
                    } catch (e) { /* 忽略 */ }
                    if (msg === "上传失败") {
                        if (xhr.status === 413) msg = "文件超过 " + formatFileSize(UPLOAD_MAX_BYTES) + " 上限";
                        else if (xhr.status === 507) msg = "电脑磁盘空间不足";
                        else if (xhr.status === 401) msg = "令牌已失效，请重新扫码";
                        else if (xhr.status === 409) msg = "进度不一致，请重新选择该文件续传";
                    }

                    // 409（偏移不匹配）与 4xx 属业务拒绝，重试无意义
                    const retryable = xhr.status >= 500 || xhr.status === 0 || xhr.status === 429;
                    if (retryable && attempt < maxRetry) {
                        attempt++;
                        setTimeout(send, 500 * attempt);
                        return;
                    }
                    onFail(msg + (attempt > 0 ? "（已重试 " + attempt + " 次）" : "")
                        + "。已传部分已保留，重新选择该文件可续传。");
                    resolve(false);
                });

                xhr.addEventListener("error", () => {
                    if (attempt < maxRetry) {
                        attempt++;
                        setTimeout(send, 500 * attempt);
                        return;
                    }
                    onFail("网络错误。已传部分已保留，重新选择该文件可续传。");
                    resolve(false);
                });

                xhr.addEventListener("abort", () => {
                    onFail("已取消（已传部分保留，可续传）");
                    resolve(false);
                });

                // 直接发 Blob：浏览器按流式读取，不会把整块读进内存
                xhr.send(blob);
            };
            send();
        });
    }

    /**
     * 创建上传任务 DOM
     */
    function createUploadTaskElement(taskId, file) {
        const el = document.createElement("div");
        el.className = "upload-task";
        el.dataset.taskId = taskId;
        el.innerHTML =
            '<div class="upload-task__head">'
            + '  <div class="upload-task__name" title="' + escapeHtml(file.name) + '">'
            + escapeHtml(file.name) + '</div>'
            + '  <div class="upload-task__status">等待中</div>'
            + '</div>'
            + '<div class="progress"><div class="progress__bar"></div></div>'
            + '<div style="font-size:var(--fs-caption);color:var(--text-muted);margin-top:4px;">'
            + formatFileSize(file.size) + '</div>';
        return el;
    }

    /**
     * 处理文件选择
     */
    function handleFiles(files) {
        if (!files || files.length === 0) return;
        Array.from(files).forEach(uploadFile);
    }

    /**
     * 初始化上传区域事件
     */
    function initUploadZone() {
        // 点击选择
        dom.pickBtn.addEventListener("click", (e) => {
            e.stopPropagation();
            dom.fileInput.click();
        });
        dom.uploadZone.addEventListener("click", () => {
            dom.fileInput.click();
        });
        dom.fileInput.addEventListener("change", (e) => {
            handleFiles(e.target.files);
            e.target.value = ""; // 允许重复选择同一文件
        });

        // 多选打包下载
        dom.multiToggleBtn.addEventListener("click", () => {
            toggleMultiSelect(!state.multiSelect);
        });
        dom.multiExitBtn.addEventListener("click", () => toggleMultiSelect(false));
        dom.multiZipBtn.addEventListener("click", downloadSelectedAsZip);

        // 目录上传：webkitdirectory 让每个 File 带 webkitRelativePath（如 photos/2024/a.jpg），
        // 服务端据此重建目录结构（先做安全相对路径校验，拒绝 ../ 与绝对路径）
        dom.pickDirBtn.addEventListener("click", (e) => {
            e.stopPropagation();
            dom.dirInput.click();
        });
        dom.dirInput.addEventListener("change", (e) => {
            handleFiles(e.target.files);
            e.target.value = "";
        });

        // 键盘可访问
        dom.uploadZone.addEventListener("keydown", (e) => {
            if (e.key === "Enter" || e.key === " ") {
                e.preventDefault();
                dom.fileInput.click();
            }
        });

        // 拖放
        ["dragenter", "dragover"].forEach((evt) => {
            dom.uploadZone.addEventListener(evt, (e) => {
                e.preventDefault();
                e.stopPropagation();
                dom.uploadZone.classList.add("is-dragover");
            });
        });
        ["dragleave", "drop"].forEach((evt) => {
            dom.uploadZone.addEventListener(evt, (e) => {
                e.preventDefault();
                e.stopPropagation();
                // dragleave 在移到子元素时也会触发，检查是否真的离开
                if (evt === "dragleave" && e.target !== dom.uploadZone) return;
                dom.uploadZone.classList.remove("is-dragover");
            });
        });
        dom.uploadZone.addEventListener("drop", (e) => {
            const dt = e.dataTransfer;
            if (dt && dt.files) {
                handleFiles(dt.files);
            }
        });

        // 全局阻止默认拖放（避免浏览器打开文件）
        window.addEventListener("dragover", (e) => e.preventDefault());
        window.addEventListener("drop", (e) => e.preventDefault());
    }

    /* ============================================================
       设备列表
       ============================================================ */

    /**
     * 拉取设备列表
     */
    async function fetchDevices() {
        try {
            const resp = await fetch(buildUrl("/api/devices"), { cache: "no-store" });
            if (!resp.ok) throw new Error("HTTP " + resp.status);
            const data = await resp.json();
            state.devices = Array.isArray(data) ? data : [];
            renderDevices();
        } catch (err) {
            // 静默失败，由 WebSocket 兜底；首次失败给个 toast
            if (state.devices.length === 0) {
                showToast("设备列表获取失败", "error");
            }
        }
    }

    /**
     * 渲染设备列表
     */
    function renderDevices() {
        const listEl = dom.deviceList;
        listEl.innerHTML = "";
        var totalCount = state.devices.length + state.browsers.length;
        dom.deviceCount.textContent = String(totalCount);

        if (totalCount === 0) {
            const empty = document.createElement("div");
            empty.className = "empty-state";
            empty.innerHTML = '<div class="empty-state__icon" aria-hidden="true">'
                + '<svg width="48" height="48" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"><path d="M2 12a15 15 0 0120 0M5.5 15.5a10 10 0 0113 0M9 19a5 5 0 016 0"/><line x1="12" y1="20" x2="12" y2="22"/></svg>'
                + '</div><div class="empty-state__text">暂无设备或浏览器在线</div>';
            listEl.appendChild(empty);
            return;
        }

        const frag = document.createDocumentFragment();

        // ===== 局域网设备分区 =====
        if (state.devices.length > 0) {
            var devHeader = document.createElement("div");
            devHeader.style.cssText = "font-size:var(--fs-caption);color:var(--text-muted);margin:12px 0 6px;text-transform:uppercase;letter-spacing:0.5px;";
            devHeader.textContent = "局域网设备（" + state.devices.length + "）";
            frag.appendChild(devHeader);
        }

        state.devices.forEach((dev) => {
            const online = dev.isOnline !== false; // 默认当作在线
            const card = document.createElement("div");
            card.className = "device-card";
            card.setAttribute("role", "listitem");
            if (dev.isLocal) card.classList.add("is-local");

            const initial = (dev.name || "?").charAt(0).toUpperCase();
            card.innerHTML =
                '<div class="device-card__avatar" aria-hidden="true">' + escapeHtml(initial) + '</div>'
                + '<div class="device-card__body">'
                + '  <div class="device-card__name">' + escapeHtml(dev.name || "未知设备")
                + (dev.isLocal ? ' <span style="color:var(--accent);font-size:var(--fs-caption);">本机</span>' : '')
                + '</div>'
                + '  <div class="device-card__addr">' + escapeHtml(dev.displayAddress || (dev.ipAddress + ":" + dev.transferPort)) + '</div>'
                + '</div>'
                + '<div class="device-card__status">'
                + '  <span class="status-dot ' + (online ? "is-online" : "is-offline") + '"></span>'
                + (online ? "在线" : "离线")
                + '</div>'
                + (online && safeWebUrl(dev.webUrl)
                    ? '<a class="device-card__open" href="' + escapeHtml(safeWebUrl(dev.webUrl)) + '" target="_blank" rel="noopener">打开</a>'
                    : '');

            frag.appendChild(card);
        });

        // ===== 在线浏览器分区 =====
        if (state.browsers.length > 0) {
            var brHeader = document.createElement("div");
            brHeader.style.cssText = "font-size:var(--fs-caption);color:var(--text-muted);margin:16px 0 6px;text-transform:uppercase;letter-spacing:0.5px;";
            brHeader.textContent = "在线浏览器（" + state.browsers.length + "）";
            frag.appendChild(brHeader);
        }

        state.browsers.forEach((br) => {
            const card = document.createElement("div");
            card.className = "device-card";
            card.setAttribute("role", "listitem");
            card.style.borderLeft = "3px solid var(--accent)";

            card.innerHTML =
                '<div class="device-card__avatar" aria-hidden="true" style="background:var(--accent);color:var(--text-on-accent);">'
                + '<svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><circle cx="12" cy="12" r="4"/><line x1="21.17" y1="8" x2="12" y2="8"/><line x1="3.95" y1="6.06" x2="8.54" y2="14"/><line x1="10.88" y1="21.94" x2="15.46" y2="14"/></svg>'
                + '</div>'
                + '<div class="device-card__body">'
                + '  <div class="device-card__name">浏览器 <span style="color:var(--text-muted);font-size:var(--fs-caption);">' + escapeHtml(br.ipAddress || "") + '</span></div>'
                + '  <div class="device-card__addr">连接于 ' + formatTime(br.connectedAt) + '</div>'
                + '</div>'
                + '<div class="device-card__status">'
                + '  <span class="status-dot is-online"></span>在线'
                + '</div>';

            frag.appendChild(card);
        });

        listEl.appendChild(frag);
    }

    /* ============================================================
       WebSocket 状态推送
       ============================================================ */

    function connectWs() {
        if (state.ws && (state.ws.readyState === WebSocket.OPEN
            || state.ws.readyState === WebSocket.CONNECTING)) {
            return;
        }
        try {
            const ws = new WebSocket(wsUrl());
            state.ws = ws;
            state.wsClosedByUser = false;

            ws.addEventListener("open", () => {
                setConnStatus(true);
                startHeartbeat();
                showToast("已连接到服务器", "success");
            });

            ws.addEventListener("message", (e) => {
                try {
                    const msg = JSON.parse(e.data);
                    handleWsMessage(msg);
                } catch (err) {
                    // 非 JSON 消息或心跳响应，忽略
                }
            });

            ws.addEventListener("close", () => {
                stopHeartbeat();
                setConnStatus(false);
                state.ws = null;
                if (!state.wsClosedByUser) {
                    // 自动重连
                    scheduleReconnect();
                }
            });

            ws.addEventListener("error", () => {
                // error 后通常会跟 close，这里只更新状态
                setConnStatus(false);
            });
        } catch (e) {
            setConnStatus(false);
            scheduleReconnect();
        }
    }

    function disconnectWs() {
        state.wsClosedByUser = true;
        stopHeartbeat();
        clearTimeout(state.reconnectTimer);
        if (state.ws) {
            try { state.ws.close(); } catch (e) { /* 忽略 */ }
            state.ws = null;
        }
        setConnStatus(false);
    }

    function scheduleReconnect() {
        clearTimeout(state.reconnectTimer);
        state.reconnectTimer = setTimeout(() => {
            connectWs();
        }, WS_RECONNECT_MS);
    }

    function startHeartbeat() {
        stopHeartbeat();
        state.heartbeatTimer = setInterval(() => {
            if (state.ws && state.ws.readyState === WebSocket.OPEN) {
                try {
                    state.ws.send(JSON.stringify({ type: "ping", ts: Date.now() }));
                } catch (e) { /* 忽略 */ }
            }
        }, WS_HEARTBEAT_MS);
    }

    function stopHeartbeat() {
        clearInterval(state.heartbeatTimer);
        state.heartbeatTimer = null;
    }

    /**
     * 处理 WebSocket 推送消息
     * 约定消息体：{ type, payload }
     */
    function handleWsMessage(msg) {
        if (!msg || typeof msg !== "object") return;
        switch (msg.type) {
            case "pong":
                // 心跳响应，无操作
                break;
            case "deviceChange": {
                // 设备上线/离线/更新
                const dev = msg.payload;
                if (!dev || !dev.deviceId) break;
                const idx = state.devices.findIndex((d) => d.deviceId === dev.deviceId);
                if (msg.changeType === "Offline" || dev.isOnline === false) {
                    if (idx >= 0) {
                        state.devices[idx] = { ...state.devices[idx], ...dev, isOnline: false };
                    }
                } else {
                    if (idx >= 0) {
                        state.devices[idx] = { ...state.devices[idx], ...dev, isOnline: true };
                    } else {
                        state.devices.push({ ...dev, isOnline: true });
                    }
                }
                renderDevices();
                break;
            }
            case "deviceList": {
                // 全量设备列表
                if (Array.isArray(msg.payload)) {
                    state.devices = msg.payload;
                    renderDevices();
                }
                break;
            }
            case "browserList": {
                // 全量在线浏览器列表
                if (Array.isArray(msg.payload)) {
                    state.browsers = msg.payload;
                    renderDevices();
                }
                break;
            }
            case "transferUpdate": {
                // 传输状态更新（可选展示）
                // 当前 UI 不直接展示传输任务，仅 toast 提示
                const t = msg.payload;
                if (t && t.status === "Completed" && t.fileName) {
                    showToast("接收完成：" + t.fileName, "success");
                }
                break;
            }
            case "serverInfo": {
                // 服务器信息推送
                if (msg.payload && msg.payload.host) {
                    dom.hostInfo.textContent = msg.payload.host;
                }
                break;
            }
            default:
                // 未知类型，静默忽略
                break;
        }
    }

    function setConnStatus(online) {
        dom.connDot.className = "status-bar__dot " + (online ? "is-online" : "is-offline");
        dom.connText.textContent = online ? "已连接" : "未连接";
    }

    /* ============================================================
       初始化
       ============================================================ */

    function initTabs() {
        dom.tabs.forEach((tab) => {
            tab.addEventListener("click", () => switchTab(tab.dataset.tab));
        });
    }

    function initHostInfo() {
        // 显示当前访问的主机地址
        dom.hostInfo.textContent = window.location.host;
    }

    function startDeviceRefresh() {
        // 首次立即拉取
        fetchDevices();
        state.deviceTimer = setInterval(fetchDevices, DEVICE_REFRESH_MS);
    }

    function stopDeviceRefresh() {
        clearInterval(state.deviceTimer);
        state.deviceTimer = null;
    }

    /**
     * 页面可见性变化：隐藏时暂停轮询，节省流量
     */
    function initVisibilityHandler() {
        document.addEventListener("visibilitychange", () => {
            if (document.hidden) {
                stopDeviceRefresh();
            } else {
                if (state.activeTab === "devices" || state.activeTab === "browse") {
                    startDeviceRefresh();
                    fetchFiles(state.currentPath);
                }
                // 【2026-09-02 修复】手机锁屏/切后台时浏览器会杀掉 WebSocket，而 onclose 的
                // 定时重连在后台可能被冻结、回前台也不可靠——回前台时主动检查并重连。
                // connectWs 内部有 OPEN/CONNECTING 防重入，重复调用安全。
                if (!state.ws || state.ws.readyState === WebSocket.CLOSED
                    || state.ws.readyState === WebSocket.CLOSING) {
                    state.ws = null;
                    connectWs();
                }
            }
        });
    }

    /**
     * 扫码配对：URL 上带 ?c= 时，用短期配对码换取长期令牌并存入 localStorage，
     * 然后把配对码从地址栏抹掉（避免它被存进浏览器历史/分享出去）。
     * @returns {Promise<boolean>} 是否已持有可用令牌
     */
    async function tryPair() {
        const params = new URLSearchParams(window.location.search);
        const code = params.get("c");

        if (code) {
            try {
                const resp = await fetch(API_BASE + "/api/pair", {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({ code: code }),
                });
                if (resp.ok) {
                    const data = await resp.json();
                    TOKEN = data.token || "";
                    localStorage.setItem(TOKEN_STORAGE_KEY, TOKEN);
                } else {
                    TOKEN = "";
                }
            } catch (e) {
                TOKEN = "";
            }
            // 无论成功与否都清掉地址栏里的配对码
            params.delete("c");
            const query = params.toString();
            window.history.replaceState(null, "", window.location.pathname + (query ? "?" + query : ""));
        }

        return !!TOKEN;
    }

    async function init() {
        // 必须先配对再初始化：WS_URL / API 都依赖 TOKEN
        const paired = await tryPair();
        if (!paired) {
            showToast("未配对或配对码已过期，请重新扫描电脑上的二维码", "error");
        }

        initTabs();
        initHostInfo();
        renderBreadcrumb();
        initUploadZone();
        initVisibilityHandler();

        // 加载初始文件列表
        fetchFiles("");

        // 启动设备轮询
        startDeviceRefresh();

        // 连接 WebSocket
        connectWs();

        // 页面卸载时断开
        window.addEventListener("beforeunload", () => {
            disconnectWs();
            stopDeviceRefresh();
        });
    }

    // DOM 就绪后启动
    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }
})();
