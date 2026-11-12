# Codex Dispatch Center

Windows 上的 Codex 多窗口协作工具。它协调多个聊天对 SolidWorks 等共享软件的使用，并提供由主管模型决定的模型交接流程，让不冲突的开发工作可以同时进行。

当前源码版本：**0.21.0**。这是持续开发中的源码项目，不包含 SolidWorks、商业 SDK 或已安装的正式发行包。

## 能做什么

### 测试排队

- 为 SolidWorks、其他指定软件或共享桌面输入分别排队；不同软件及不占用共享资源的编译、分析可并行进行。
- 以窗口分组查看任务，区分等待、运行和已完成状态；支持人工调整等待顺序及管理许可。管理操作仍受资源占用和清理状态约束。
- 每段测试申请资源、接单并获准后执行；测试和清理结束立即释放，后续代码修改不必占用软件。
- 经用户单独授权，可以唤回原聊天继续排队任务；也可单独开启无活动清理，由原聊天确认自建资源已清理后释放许可。无法确认时保留隔离。

### 模型调度

- 模型调度与测试排队是两个独立模块，可分别启用。
- 用户配置主管模型、推理强度和不参与调配的模型；由主管判断是否将限定任务交给执行模型，完成或失败后按原周期返回主管。
- 交接需要用户授权，并核对真实运行记录；消息发送成功不等于切换已经生效。运行中可记录进度并请求主管接管。

### SolidWorks 管理

- 可从运行中的 SolidWorks 获取程序路径，提供启动、关闭以及错误提示检测入口。
- 可单独开启崩溃自动处理，默认关闭。确认异常并满足条件时，定向处理已识别的提示、隔离识别出的自动恢复副本，再尝试启动一个新实例；不删除原工程。
- 已针对本机中文“插件加载性能”通知完成一次实际定向关闭。不同版本、语言和错误窗口的识别范围不同，未知提示会留下日志并等待处理。

## 已完成与验证边界

已实现本机服务、Windows 桌面界面、两组独立 MCP 工具、许可接单与清理释放、原聊天接续、无活动清理、模型交接协议及 SolidWorks 管理。单聊天虚拟资源自动接续、模型切换往返、SolidWorks 只读连接和部分实际界面操作已有验证记录。

模拟测试不能代替真实软件验收。不同版本的 SolidWorks 崩溃弹窗、恢复提示和多聊天复杂场景仍需实际验证；模型完整无人值守调度也不能仅凭消息回执认定成功。重启宿主不代表旧测试成功、旧许可已清理，也不会自动重跑业务任务。

## 运行环境

- Windows 10/11 x64；.NET SDK 10.0.301（或兼容补丁），项目目标为 .NET 8，桌面运行需要 .NET 8 Desktop Runtime。
- Python 3.11+、Node.js 22+，以及已安装并登录的 Codex 桌面应用和 CLI。
- 构建 SolidWorks 执行器需要本机 SolidWorks SDK 的 `SolidWorks.Interop.sldworks.dll`。仓库不提供商业软件或 SDK。

## 构建和连接

```powershell
git clone https://github.com/alec-hub132/codex-dispatch-center.git
cd codex-dispatch-center
# 如果无法自动定位 SDK，可指定本机互操作 DLL：
# $env:SOLIDWORKS_INTEROP_PATH = 'C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist\SolidWorks.Interop.sldworks.dll'
./scripts/Build-Test.ps1 -Configuration Release
./scripts/Start-Desktop.ps1 -Configuration Release
```

在界面的“工具管理”启用所需模块，在“Codex 连接”完成登记。也可运行 `./scripts/Connect-Scheduler.ps1 -Action Inspect` 和 `./scripts/Connect-Scheduler.ps1 -Action Connect`。首次登记或工具清单更新后，可能需要重新加载 Codex。默认运行数据保存在 `%LOCALAPPDATA%\CodexDispatch`。

## 排队使用原则

1. 申请与实际操作对应的资源：`solidworks` 仅锁 SolidWorks；`solidworks-ui` 同时锁桌面输入；`app:名称`、`app-ui:名称` 和 `desktop` 用于其他软件或共享输入。
2. `Queued` 是等待，`Offered` 是预留；必须用原令牌、当前预留编号和代次接单，取得 `Granted` 后才能操作。
3. 停止操作并清理自建资源后释放，回读 `Released` 和 `cleanupStatus=OK`。清理不确定时保持隔离。

这是合作式调度，无法拦截未接入的工具。许可本身不授权关闭用户软件、保存原工程或部署插件。普通测试默认连接现有 SolidWorks；自动崩溃处理需用户单独开启。

## 隐私与诊断

程序记录本机诊断日志以便排错。请勿提交访问令牌、许可令牌、聊天授权、数据库、用户文件或本机日志。公开源码不包含个人运行数据、私有配置和构建产物。

## 项目结构

```text
src/Dispatch.Core        队列、许可、模型交接和状态管理
src/Dispatch.Daemon      本机后台服务
src/Dispatch.McpBridge   Codex MCP 接口
src/Dispatch.Desktop     Windows 桌面界面
src/Runner.*             测试执行器
scripts/                 构建、连接和运行脚本
tests/                   自动化检查
```
