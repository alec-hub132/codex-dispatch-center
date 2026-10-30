# Codex Dispatch Center

Windows 上的 Codex 多窗口调度工具，提供共享软件测试排队、资源许可，以及由主管模型规划的模型交接协议。

## 它解决什么问题

多个 Codex 窗口可以同时写代码和编译，但同时操作同一个 SolidWorks 实例容易互相干扰。该工具通过一个本机服务统一管理使用顺序：先申请、再接单、获准后操作，完成清理后释放。不同软件可以并行，不需要把整个开发任务串行化。

模型调度是另一个独立模块：由用户配置主管模型，由主管判断是否把限定任务交给其他模型，任务结束后沿原周期返回主管。可以单独使用排队、单独使用模型调度，或同时使用。

## 功能与当前状态

| 功能 | 状态 |
| --- | --- |
| 按软件标识排队、持久化许可、接单及清理释放 | 已实现 |
| 两组独立 MCP 工具与模块开关 | 已实现 |
| 原聊天单次自动接续 | 单聊天虚拟资源链路已验证；多聊天仍需验收 |
| 主管配置、执行模型排除、交接与返回协议、运行记录核验 | 已实现；完整无人值守往返仍需验收 |
| 无活动窗口自动清理 | 仅活动检测与状态机核心，后台及界面尚未接入，当前不可用 |
| SolidWorks 文档/视图/选择恢复 | 历史实验功能，日常流程停用 |

这是开发中的源码快照，不包含可直接安装的正式发行包。源码中的能力标志与实际验收状态应一起判断，发送回执不等于模型切换成功或资源已清理。

## 运行环境

- Windows 10/11，x64。
- .NET SDK 10.0.301 或可兼容的同系列补丁，项目目标为 .NET 8；运行界面需要 .NET 8 Desktop Runtime。
- Python 3.11 及以上，Node.js 22 及以上。
- 已安装且登录的 Codex 桌面应用及 CLI。
- 完整构建需要本机 SolidWorks SDK 的 `SolidWorks.Interop.sldworks.dll`。程序不附带商业 SDK 或 SolidWorks。

## 从源码构建

```powershell
git clone https://github.com/alec-hub132/codex-dispatch-center.git
cd codex-dispatch-center
# 如果 SDK 自动定位失败，可指定本机安装位置：
# $env:SOLIDWORKS_INTEROP_PATH = 'C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist\SolidWorks.Interop.sldworks.dll'
./scripts/Build-Test.ps1 -Configuration Release
./scripts/Start-Desktop.ps1 -Configuration Release
```

构建脚本依次编译服务、MCP 桥和界面，运行核心、连接、进程及离屏界面检查。测试使用隔离实例与临时目录；真实 SolidWorks 功能仍需人工验收，模拟测试不代表插件通过。

构建成功后可运行 `./scripts/Package-Desktop.ps1` 生成本机候选包。候选包可能含本机环境信息，公开发布前应另行检查。

## 接入 Codex

在程序的“工具管理”中启用所需模块，再到“Codex 连接”完成连接检查。也可以使用：

```powershell
./scripts/Connect-Scheduler.ps1 -Action Inspect
./scripts/Connect-Scheduler.ps1 -Action Connect
```

工具会登记 `codex-queue` 和 `codex-model-dispatch` 两个本机 MCP 入口。首次登记或工具清单变化后，需要重载相关 Codex 窗口。连接安装器会备份自有配置；若发现并发修改或其他程序的同名配置，会停止覆盖。

默认运行数据存放在 `%LOCALAPPDATA%\CodexDispatch`，服务和界面均支持 `--data` 指定其他目录。`CODEX_HOME` 指向用户自己的 Codex 配置目录；本仓库不提供个人账号、API 密钥或现成聊天授权。

## 测试排队流程

1. 读取 `get_module_settings`，通过 `request_resource` 申请对应资源。
2. `Queued` 表示排队；`Offered` 仅为短期预留。使用原令牌、当前 `offerId` 和 `generation` 调用 `accept_resource`。
3. 只有 `Granted` 后才执行用户已授权的操作。
4. 本段测试完成后停止自建操作并清理，调用 `release_resource(cleanupConfirmed=true)`，回读 `Released` 和 `cleanupStatus=OK`。

资源标识：

- `solidworks`：SolidWorks MCP/COM 操作，仅锁该软件。
- `solidworks-ui`：同时占用 SolidWorks 与鼠标键盘。
- `app:editor`：其他软件的稳定标识；同软件各窗口使用同一标识。
- `app-ui:editor`：该软件及桌面输入。
- `desktop`：共享鼠标键盘输入。

普通编译和不冲突的后台检查不需要共享软件许可。固定 `submit_test` 套件自行管理资源，不要重复申请一层许可。

## 模型调度流程

用户在界面配置主管、推理强度及不参与调配的模型。主管提交执行模型建议和限定任务；已授权原聊天执行一次交接，随后核对真实轮次上下文。完成、失败或取消后均应沿原周期返回主管，不重复发送交接，也不覆盖用户手动选择。

本功能依赖 Codex 当前版本的工具和桌面接口。部分接续实验使用版本限定的本机 IPC，接口变化可能导致不可用；不会提供静默无限重试或绕过授权的保证。

## 安全与边界

- 这是合作式调度，不是进程沙箱，无法阻止未接入的脚本绕过许可操作软件。
- 资源许可不额外授权启动/关闭 SolidWorks、保存原工程或部署插件。默认复用已有宿主，新开独立实例需用户明确授权。
- 许可过期或清理不明时保留隔离，时间过去、窗口回复结束或低 CPU 均不代表清理成功。
- 许可令牌、聊天授权、SQLite 数据库、诊断记录及私有接续计划只应保留在本机，不要提交到仓库。
- 本仓库排除了开发者的运行日志、聊天编号、本机路径、配置备份、访问令牌、快捷方式及构建包。

## 项目结构

```text
src/Dispatch.Core        调度、持久化、许可和交接协议
src/Dispatch.Daemon      本机后台服务与执行器管理
src/Dispatch.McpBridge   STDIO MCP 桥
src/Dispatch.Desktop     WPF 管理界面
src/Runner.*             模拟与只读 SolidWorks 执行器
scripts/                通用构建、连接及接续脚本
tests/                  自动化检查
```

故障信息写入运行数据目录的 `logs/bug.jsonl`。报告问题前请先删除日志中的聊天内容、文档路径、令牌及个人信息，参见 [安全与隐私说明](SECURITY.md)。

## 许可

本次公开源码尚未选择开源许可证。若需再分发或将代码用于其他产品，请先与仓库所有者确认许可。
