# OUI 表生成器（`Tools/oui-gen`）

生成 `src/SystemToolkit.Core/Network/LanScan/Data/oui-ieee.csv`——网络管理模块「局域网扫描」
用于把 MAC 前三字节翻成厂商名的**官方数据层**。

## 为什么这不是"引入第三方代码"

本工具只做一件事：把 **IEEE 官方公开注册表**（`standards-oui.ieee.org/oui/oui.csv`）从 CSV
转成紧凑文本。**不引入任何第三方项目的代码**，也不是从别人的仓库镜像拿数据——
属于落地计划里的 **🔄 换数据源**，不触碰 ADR-002 §5.1 的代码许可证红线。
数据集本身是 IEEE 的公开发布物（注册表的客观事实性数据），登记见 **ADR-002 §5.3**。

> ⚠️ 若将来要用**第三方镜像**（各类 GitHub 上的 oui.csv 副本）代替官方端点，须重新走
> ADR-002 §5.2 五步流程并在 §5.3 更新登记——**镜像的许可证不等于数据的来源合法性**。

## 用法

```bash
# 直接下载（需能访问 standards-oui.ieee.org）
python Tools/oui-gen/generate_oui.py

# 走本地代理（本沙箱实测：不经代理会 418 / 卡死）
python Tools/oui-gen/generate_oui.py --proxy http://127.0.0.1:7890

# 离线复跑：用已下载的官方 CSV 重新生成（校验生成逻辑是否可复现）
python Tools/oui-gen/generate_oui.py --from-file oui.csv
```

脚本会打印来源、UTC 抓取时间、官方 CSV 与生成文件的字节数与 SHA-256，**请把这几行抄回本文件**（见下"快照档案"）。

## 输出格式

```
286FB9|Nokia Shanghai Bell Co., Ltd.
38E2CA|Katun Corporation
```

- 每行 `<6 位十六进制前缀>|<Organization Name>`，UTF-8 **无 BOM**，LF 换行。
- **只按前缀做稳定排序**：同前缀内保留 IEEE 自身的次序。
  理由：IEEE 原表**不是**按前缀有序的，且表内有 2 个前缀重复（`080030`×3、`0001C8`×2）。
  若按"前缀 + 厂商名"排序，等于把同前缀内的先后按字母重排——那是**替 IEEE 做取舍**。
  首版生成器就是这么写的，结果 `080030` 变成了 `CERN` 打头；已改为稳定排序，C# 侧固定"**取首条**"。
- 不含 `Organization Address`（用不上，而且它是原始体积的大头：3.66 MB → 1.16 MB）。
- 厂商名**原样采用**，不做裁剪或改写（改写会产出"看起来像查错了"的名字）。

## C# 侧怎么用

`OuiTable` 是**两层**：**精选层**（人工核实的中文/短名，优先）→ **官方层**（本文件，兜底）。
精选层压过官方层是刻意的：官方名中位 21 字符、最长 93，直接铺到 UI 列会溢出；中文名官方表也给不出。
优先级由 `LanScanTests.Oui_CuratedLayer_OutranksIeeeLayer` 锁死。

## 快照档案（每次重生成后更新）

| 项 | 值 |
|---|---|
| 抓取日期（UTC） | **2026-09-13** |
| 来源 | `https://standards-oui.ieee.org/oui/oui.csv`（经 `http://127.0.0.1:7890` 代理，直接访问返回 418 / 超时） |
| 官方 CSV 体积 | 3,835,855 字节 |
| 官方 CSV SHA-256 | `136ff7de9bb3a7e4c737d6088ad08eee359885d346b83b304d2fba2f822ad0ae` |
| 数据行 / 唯一前缀 | 40,133 / 40,130 |
| 生成体积 | 1,220,147 字节（1.16 MB） |
| 生成 SHA-256 | `b350d7a08ba649bda393bb8e98bb35a1c2d8fa348cb2dc4fcf34fbcbd3f9318c` |

## 加载成本（认领时用一次性探针实测，故不做裁剪）

| 项 | 实测 |
|---|---|
| `File.ReadAllLines` 40,133 行 | 8 ms |
| 解析入 `Dictionary`（40,130 条） | 3 ms，托管分配 5.4 MB |
| 200,000 次查找 | 9 ms |
| 强制 GC 后常驻 | 18.2 MB |

对比过的另外两种形态（都弃用）：

- **生成 `.cs` 字典初始化器**：源码约 1.98 MB（对比紧凑文本 1.16 MB），拖慢编译并撑大程序集 → 弃。
- **裁剪为"常见前缀子集"**：报告给的后备方案；加载只花 11 ms，**没必要**裁剪 → 弃。
