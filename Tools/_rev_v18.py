# -*- coding: utf-8 -*-
"""v18 批次判据反向验证（事务安全版）。

用法:
    python -u Tools/_rev_v18.py inject   # 全部锚点先校验 -> 再统一写入（要么全改要么不改）
    python -u Tools/_rev_v18.py restore  # 从备份还原全部（含 mtime 刷新）
    python -u Tools/_rev_v18.py status   # 断言现在的状态是"原始"还是"已注入"

🔴 安全设计（相对上一版的改进）：
  1. **两阶段提交**：先对全部 11 个锚点做唯一性校验（只读），任一不满足即**整体放弃、零写入**。
     上一版是"边校验边写"，锚点校验失败时前面的文件已被改。
  2. **备份先于任何写入**：备份全部目标文件成功后，才开始写入。
  3. **断点必须编译安全**：只允许移动语句 / 改 catch 类型 / 改字符串字面量 / 改 XAML 属性，
     **绝不允许改符号名**（CS0103 ⇒ 构建失败 ⇒ 测试没跑 ⇒ 与"守卫变红"长得一样）。
  4. **还原后必须回源核验**：跑 status + 残留扫描，再跑一次基线测试确认绿。
"""
import os
import shutil
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BK = os.path.join(ROOT, ".artifacts-qing", "_rev_backup")

# (标签, 相对路径, 原文, 替换为)  —— 每个锚点在目标文件内必须唯一
BREAKS = [
    (
        # 编译安全：给徽标 TextBlock 另加一个 Foreground 属性（XAML 重复设值合法，后者覆盖前者），
        # 于是 DataTrigger 那条路失效；断言块里的 DataTrigger/DynamicResource 仍在，故本断点靠
        # `Assert.DoesNotContain(...)` 之外的路径破——实测用「加 Foreground」会与 XAML local value 冲突。
        # 改用「把 DynamicResource 换成 StaticResource」：XAML 合法、语义正是"主题不跟随"的老毛病。
        "B1-FileBackup-Xaml-Badge",
        "src/SystemToolkit.Modules.FileBackup/FileBackupView.xaml",
        '<Setter Property="Foreground" Value="{DynamicResource Brush_SuccessText}"/>',
        '<Setter Property="Foreground" Value="{StaticResource Brush_SuccessText}"/>',
    ),
    (
        # 编译安全：把类型守卫从 if 条件摘出，改在 if 之后用 as + 判空（vm 声明仍合法）。
        # 断言串 `DataContext is not FileTransferViewModel` 从方法体消失 ⇒ 判据变红。
        "B2-FileTransfer-OnTabChecked-Guard",
        "src/SystemToolkit.Modules.FileTransfer/FileTransferView.xaml.cs",
        "            || DataContext is not FileTransferViewModel vm\n            || DesktopPanel is null || MobilePanel is null\n            || !int.TryParse(tag, out int index))\n        {\n            return;\n        }\n\n        vm.SelectedTabIndex = index;",
        "            || DesktopPanel is null || MobilePanel is null\n            || !int.TryParse(tag, out int index))\n        {\n            return;\n        }\n\n        var vm = (FileTransferViewModel)DataContext;\n        vm.SelectedTabIndex = index;",
    ),
    (
        # XAML 合法：把处理器换回 OnTabChecked（断言串 OnLogToggleChanged 消失）。
        "B3-FileTransfer-LogToggle-Wiring",
        "src/SystemToolkit.Modules.FileTransfer/FileTransferView.xaml",
        'Checked="OnLogToggleChanged" Unchecked="OnLogToggleChanged"',
        'Checked="OnTabChecked" Unchecked="OnTabChecked"',
    ),
    (
        # 编译安全：把 PickFiles 调用提到 try 之上（合法语句）。
        "B4-FileTransfer-SendSelected-PickInsideTry",
        "src/SystemToolkit.Modules.FileTransfer/FileTransferDesktopViewModel.cs",
        "        try\n        {\n            if (SelectedDevice is null)\n            {\n                _log(\"[互传] ⚠️ 请先选择目标设备\");\n                return;\n            }\n\n            IReadOnlyList<string>? files = PickFiles?.Invoke();",
        "        IReadOnlyList<string>? files = PickFiles?.Invoke();\n        try\n        {\n            if (SelectedDevice is null)\n            {\n                _log(\"[互传] ⚠️ 请先选择目标设备\");\n                return;\n            }\n",
    ),
    (
        # 编译安全：OCE 分流换成 TimeoutException（类型存在）。
        "B5-FileTransfer-Mobile-OCE",
        "src/SystemToolkit.Modules.FileTransfer/FileTransferMobileViewModel.cs",
        "catch (OperationCanceledException)\n        {\n            // 🟡 v18-🟡-2（2026-09-16）：取消/超时不当成",
        "catch (TimeoutException)\n        {\n            // 🟡 v18-🟡-2（2026-09-16）：取消/超时不当成",
    ),
    (
        # 编译安全：删掉文件日志那一行。
        "B6-FileTransfer-Mobile-FileLog",
        "src/SystemToolkit.Modules.FileTransfer/FileTransferMobileViewModel.cs",
        "            _log(\"[手机] ❌ Web 服务启动失败：\" + ex.Message);\n            _logger?.Error(\"[手机] Web 服务启动失败\", ex);",
        "            _log(\"[手机] ❌ Web 服务启动失败：\" + ex.Message);",
    ),
    (
        # 编译安全：把取路径那行提到 try 之上（path 已提前声明）。
        "B7-AppManager-ExportTry",
        "src/SystemToolkit.Modules.AppManager/AppManagerViewModel.cs",
        "        try\n        {\n            path = PickReportPath?.Invoke();",
        "        path = PickReportPath?.Invoke();\n        try\n        {\n",
    ),
    (
        # 编译安全：消费点改为先调 SetIgnore 再让 saved 恒 true（文案分支塌成一支）。
        "B8-AppManager-IgnoreSaved",
        "src/SystemToolkit.Modules.AppManager/AppManagerViewModel.cs",
        "        bool saved = SetIgnore(vm, ignored: true);",
        "        SetIgnore(vm, ignored: true);\n        bool saved = true;",
    ),
    (
        # 编译安全：catch 类型换成 InvalidOperationException。
        "B9-FileBackup-BackupCatch",
        "src/SystemToolkit.Modules.FileBackup/FileBackupViewModel.Backup.cs",
        "        catch (Exception ex)\n        {\n            Log(\"[备份] ❌ 备份命令异常：\" + ex.Message);",
        "        catch (InvalidOperationException ex)\n        {\n            Log(\"[备份] ❌ 备份命令异常：\" + ex.Message);",
    ),
    (
        # 编译安全：改确认文案里的字面量（断言串消失）。
        "B10-FileBackup-SaveRule-Try",
        "src/SystemToolkit.Modules.FileBackup/FileBackupViewModel.cs",
        'ConfirmRequest?.Invoke("保存为草稿"',
        'ConfirmRequest?.Invoke("保存为草稿X"',
    ),
    (
        # csproj 非法元素名（MSBuild 忽略未知属性，不报错）。
        "B11-FileTransfer-InternalsVisibleTo",
        "src/SystemToolkit.Modules.FileTransfer/SystemToolkit.Modules.FileTransfer.csproj",
        '<InternalsVisibleTo Include="SystemToolkit.Tests" />',
        '<InternalsVisibleTo0 Include="SystemToolkit.Tests" />',
    ),
]


def detect_newline(p):
    with open(p, "rb") as f:
        raw = f.read()
    return "\r\n" if b"\r\n" in raw else "\n"


def _load(rel):
    p = os.path.join(ROOT, rel.replace("/", os.sep))
    if not os.path.exists(p):
        return p, None
    with open(p, "r", encoding="utf-8-sig", newline="") as f:
        return p, f.read()


def inject():
    # ── 阶段一：全体校验（只读，零副作用）───────────────────────────
    plan = []
    errors = []
    for tag, rel, old, new in BREAKS:
        p, src = _load(rel)
        if src is None:
            errors.append(f"{tag}: 文件不存在 {rel}")
            continue
        nl = detect_newline(p)
        o, n = old.replace("\n", nl), new.replace("\n", nl)
        cnt = src.count(o)
        if cnt != 1:
            errors.append(f"{tag}: 锚点 count={cnt}（应为 1） {old[:50]!r}")
            continue
        # 预先确认替换后目标串不会误增
        if src.count(n) != 0 and n not in o:
            errors.append(f"{tag}: 替换后串已存在，会把状态搞混")
            continue
        plan.append((tag, p, o, n, nl))

    if errors:
        print("❌ 校验失败，**未写入任何文件**：")
        for e in errors:
            print("   ·", e)
        return False

    # ── 阶段二：先备份，后统一写入 ────────────────────────────────
    os.makedirs(BK, exist_ok=True)
    try:
        for tag, p, o, n, nl in plan:
            rel = os.path.relpath(p, ROOT).replace(os.sep, "__")
            b = os.path.join(BK, rel)
            if not os.path.exists(b):
                shutil.copy2(p, b)
    except Exception as e:
        print(f"❌ 备份阶段失败，**未写入任何文件**：{e}")
        return False

    written = 0
    for tag, p, o, n, nl in plan:
        _, src = _load(os.path.relpath(p, ROOT))
        if src is None or src.count(o) != 1:
            print(f"   !! 写入时锚点失配，跳过 {tag}")
            continue
        with open(p, "w", encoding="utf-8-sig", newline="") as f:
            f.write(src.replace(o, n, 1))
        written += 1
        print(f"[注入] {tag}  (nl={'CRLF' if nl == chr(13)+chr(10) else 'LF'})")
    print(f"\n注入完成: {written}/{len(BREAKS)}")
    return written == len(BREAKS)


def restore():
    if not os.path.isdir(BK) or not os.listdir(BK):
        print("⚠️ 备份目录为空——无法还原（请回源核对，不要重跑 inject）")
        return False
    n = 0
    for f in sorted(os.listdir(BK)):
        rel = f.replace("__", os.sep)
        dst = os.path.join(ROOT, rel)
        if not os.path.exists(dst):
            print(f"[跳过] 目标不存在 {rel}")
            continue
        shutil.copy2(os.path.join(BK, f), dst)
        os.utime(dst, None)  # 🔴 触碰 mtime，防 MSBuild 增量跳过（跑旧 dll）
        n += 1
        print(f"[还原] {rel}")
    print(f"\n还原完成: {n} 个文件（mtime 已刷新）")
    return True


def status():
    """判「注入态 vs 原始态」。
    🔴 不能用 `new in src` 判注入——当替换是**删行**时，`new` 是 `old` 的**子串**
    （B6 实测：new = old 去掉一行），于是原始态被误判成"已注入"（假阳性）。
    正解：仍以 `old` 的计数为准，`old==1` ⇒ 原始；`old==0` ⇒ 已注入（或异常）。
    """
    ok = True
    for tag, rel, old, new in BREAKS:
        p, src = _load(rel)
        if src is None:
            print(f"{tag}: 文件缺失")
            ok = False
            continue
        nl = detect_newline(p)
        a = src.count(old.replace("\n", nl))
        if a == 1:
            print(f"{tag}: ✅原始")
        elif a == 0:
            print(f"{tag}: ❗已注入")
            ok = False
        else:
            print(f"{tag}: ??异常(old={a}，锚点不唯一)")
            ok = False
    print("\n全部处于原始态" if ok else "\n⚠️ 存在非原始态断点")
    return ok


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "status"
    if cmd == "inject":
        sys.exit(0 if inject() else 1)
    if cmd == "restore":
        sys.exit(0 if restore() else 1)
    sys.exit(0 if status() else 1)
