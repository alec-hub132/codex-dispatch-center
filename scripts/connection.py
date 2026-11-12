"""连接安装器。默认只检测；注册仅在隔离配置中由 Codex CLI 生成。"""
import argparse
import base64
import contextlib
import hashlib
import json
import os
import pathlib
import shutil
import subprocess
import sys
import tomllib
import uuid
from datetime import datetime, timezone

NAME = 'codex-dispatch'
BEGIN = '<!-- BEGIN CODEX_DISPATCH_RULES v1 -->'
END = '<!-- END CODEX_DISPATCH_RULES v1 -->'
MCP_BEGIN = b'# BEGIN CODEX_DISPATCH_MCP v1\n'
MCP_END = b'# END CODEX_DISPATCH_MCP v1\n'
RULE = (BEGIN + '\n## Codex 调度工具（资源许可与测试排队）\n'
        '- 临时 SolidWorks 或桌面测试先调用 request_resource：resource=solidworks（MCP/COM，仅锁SW）、solidworks-ui（同时占用鼠标键盘）、app:稳定软件标识（仅该软件）、app-ui:标识（该软件及鼠标键盘）或desktop（共享桌面输入），说明purpose。同一软件各窗口沿用同一标识，不同软件允许并行；普通编译和独立后台测试不排队。Queued 时不能操作宿主；get_resource 返回 Granted 后由原窗口执行已授权测试。\n'
        '- 完成测试后先停止相关操作并清理，再携带原 token、当前 generation 调用 release_resource(cleanupConfirmed=true)。未确认清理、过期或服务中断时不得自行解锁或绕过队列。\n'
        '- 固定套件通过 list_test_suites、submit_test、wait_test、get_test_report 执行，它们自身管理资源，不要重复套一层资源许可。\n'
        '- 使用许可只仲裁资源，不扩大用户授权，也不代表测试通过。原始工程保存、部署、关闭软件仍遵守用户要求。\n'
        '- 报告 simulationOnly 区分模拟与真实检查；只读连接成功不代表插件功能验收。\n'
        '- sessionId 使用当前会话的稳定关联标签；不得声称工具已验证真实窗口身份。\n'
        '- 模型建议由模型判断。已授权聊天须先由用户配置主管模型和强度，再由实际主管通过 request_model_switch 提交 plannerModel、plannerEffort、completionCriteria，选择执行模型和限定任务；执行模型不得自行开启下一委派。未配置主管时不要假设默认值。\n'
        '- 交接只允许当前真实聊天及人类已明确授权范围；核对 Execution.threadId 后先 record_model_handoff(sending)，再用内置 send_message_to_thread 至多发送一次并记录 sent。工具回执不代表模型生效；须取得用户原始确认或真实运行记录证据，不能照抄计划伪造回读。token 不得公开。\n'
        '- 执行完成、失败、取消或无法继续，均须登记 work_done/work_failed/work_cancelled/work_timeout，再按同周期返回计划登记 return_sending、发送一次、登记 return_sent，并核验主管生效。返回不明时停止新工作、不自动重发。用户主动改变设置时登记 user_override，不能覆盖用户新选择。\n'
        '- 委派前读取 get_health.modelExclusions 中当前真实聊天的排除名单，不选择被排除的执行模型；排除项覆盖该模型的全部强度，仅影响新委派，已有任务仍须返回已配置主管。\n'
        '- 当前切换结果 UNSUPPORTED 表示未实际切换，不得宣称已经生效。\n' + END)
RULE = RULE.replace(END,
        '- 排队与模型调度是独立模块，先读取get_module_settings；关闭某模块不影响另一模块，关闭排队不代表可绕过共享资源许可。已有资源仍需清理，已有模型周期仍须返回主管。\n'
        '- 执行中需要主管接管时，调用request_supervisor_takeover保存已完成、剩余、运行操作和资源关联编号；取得同周期返回计划后交回主管，不能因切换重复操作、终止进程或释放许可。进度说明不得包含令牌。\n' + END)
RULE = RULE.replace(END,
        '- 交接发送后优先调用verify_model_runtime由服务读取本聊天实际轮次上下文，核对模型、强度和本方向发送时间。VERIFIED才继续；SOURCE_UNAVAILABLE或AWAITING_MATCH不能当作生效，不自动重发，也不要求用户逐次切换选择器。\n' + END)

RULE = RULE.replace(END,
        '- 自动续跑：仅当前真实聊天且具有人类交接授权时，发送一次交接并登记sent/return_sent后调用arm_model_continuation，沿同周期登记一次续跑；WAITING不代表下一轮已启动或模型生效，下一轮先verify_model_runtime。已有登记、结果不明或失败均不重复启动，不重发原交接，不再要求用户输入继续。用户新输入或手动设置优先。\n' + END)
RULE = RULE.replace(END,
        '- 顺序占用：当前采用窗口完成操作并清理释放后，下一个窗口按队列使用的方式。不自动捕获、让出或还原SolidWorks现场，不调用capture_workspace、yield_resource、resume_resource、restore_workspace。已有不确定占用仍须由原窗口确认停止和清理后释放，不绕过隔离。\n' + END)
RULE = RULE.replace(END,
        '- 原聊天接续：仅用户在排队工具单独授权的真实聊天，request_resource携带resumeThreadId、当前实际sourceTurnId及不含令牌的remainingWork，原子绑定且必须接单。未授权不启用自动唤醒，模型授权不能替代。Offered只是预留，必须accept_resource返回Granted才操作；Queued/WaitingForClient可结束本轮等后台原聊天接续，不能据通知重建申请或重做测试。\n'
        '- 接续接单：唤醒后先用原token读取get_queue_binding/get_resource；已过期的offer不得使用。WaitingForClient可用retry_resource原许可及当前实际sourceTurnId排回队尾，最多三次接单机会。同一代次只发一次；送达不明不重发。新用户任务/停止或授权改变优先，不能覆盖；Granted之后仍由原窗口停止清理并释放，不能按未接单规则解锁。\n' + END)


RULE = RULE.replace(END,
        '- 接单默认：MCP新申请默认requireAcceptance=true，即使没有启用自动唤醒也需accept_resource；Offered不是Granted。不要为了省一步接单设置false。聊天结束前，尚未接单的原任务保留等待；已Granted必须完成清理回执，不能把回复结束当作释放。\n'
        '- 宿主复用：SolidWorks测试默认只连接已运行实例，禁止连接失败后循环新建实例或自动重启。新开独立实例须有人类明确授权；资源许可本身不授权启动、关闭软件。测试脚本必须在finally停止自有操作并清理自建资源，仅读写本段必要内容；本段宿主测试完成后立即release_resource并回读Released/cleanupStatus=OK，再继续编译或汇报，不等整项开发完成。不得关闭用户原有实例/文档，也不得把新实例遗留给下一窗口。\n' + END)

RULE = RULE.replace(END,
        '- 无活动清理：新request_resource提供ownerThreadId真实聊天编号，不能仅靠sessionId别名识别。只有用户独立启用清理功能后，后台才按无活动门限自动唤回原窗口一次；清理轮仅读取原许可、停止自建测试并清理自建资源、确认释放和回读，不重跑原测试，不关闭用户SW或聊天，不切模型。消息送达不等于清理成功。\n' + END)

RULE = RULE.replace(END,
        '- 操作段释放：许可只覆盖一段宿主测试及其清理，不覆盖随后代码修复、普通编译、分析或汇报。可执行脚本测试优先使用包内scripts/Run-ResourceStep.py，传原许可私有计划和纯测试命令；测试程序在finally实际停止自建操作并清理后，按执行器提供的本次stepId/lease/generation返回清理回执，执行器立即release并回读。测试失败但已清理也应释放，修复可与下一窗口测试并行；复测用新的requestId排队，不复用Released许可或抢在已有等待者前。直接MCP测试同样须在开始改代码前显式清理释放。进程退出、开始编译或模型思考不构成清理证明，缺少有效回执保持隔离；不自动还原现场、不关闭用户宿主。\n' + END)


class ConnectionError(RuntimeError):
    pass


def discover_cli(explicit=None):
    """资源管理器启动时 PATH 可能尚未包含桌面应用安装的 CLI。"""
    candidates = []
    if explicit:
        candidates.append(pathlib.Path(explicit).expanduser())
    else:
        from_path = shutil.which('codex')
        if from_path:
            candidates.append(pathlib.Path(from_path))
        local = os.environ.get('LOCALAPPDATA')
        if local:
            root = pathlib.Path(local) / 'OpenAI/Codex/bin'
            if root.is_dir():
                candidates.extend(sorted(root.glob('*/codex.exe'), key=lambda p: p.stat().st_mtime_ns, reverse=True)[:12])
    for candidate in dict.fromkeys(candidates):
        if not candidate.is_file():
            continue
        try:
            result = subprocess.run([str(candidate), '--version'], capture_output=True, timeout=5,
                                    creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
            if result.returncode == 0 and result.stdout.decode('utf-8', errors='replace').strip().startswith('codex-cli '):
                return str(candidate.resolve())
        except (OSError, subprocess.TimeoutExpired):
            continue
    raise ConnectionError('未找到可用的 Codex CLI。已检查 PATH 与本用户 Codex 安装目录；请检查 Codex 是否安装完整。')


def resolve_home(explicit=None, process_home=None, user_home=None):
    if explicit:
        return pathlib.Path(explicit).expanduser().resolve()
    values = [pathlib.Path(v).expanduser().resolve() for v in (process_home, user_home) if v and v.strip()]
    if len(values) == 2 and os.path.normcase(str(values[0])) != os.path.normcase(str(values[1])):
        raise ConnectionError('进程与用户 CODEX_HOME 不一致，请使用 -TargetHome 明确指定。')
    return values[0] if values else pathlib.Path.home() / '.codex'


def parse(data):
    try:
        return tomllib.loads((data or b'').decode('utf-8-sig'))
    except (ValueError, UnicodeError) as ex:
        raise ConnectionError('配置 TOML 无法解析，未修改原文件。') from ex


def read(path):
    return path.read_bytes() if path.exists() else None


def atomic(path, expected, content):
    if read(path) != expected:
        raise ConnectionError('文件已被其他窗口修改，停止覆盖：' + path.name)
    if content is None:
        path.unlink(missing_ok=True)
        return
    temporary = path.with_name(path.name + '.' + uuid.uuid4().hex + '.tmp')
    try:
        with temporary.open('xb') as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        if read(path) != expected:
            raise ConnectionError('写入前检测到并发修改：' + path.name)
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def block(data):
    text = (data or b'').decode('utf-8-sig')
    if text.count(BEGIN) != text.count(END) or text.count(BEGIN) > 1:
        raise ConnectionError('规则片段标记不完整或重复，停止修改。')
    if BEGIN not in text:
        return None
    a, b = text.index(BEGIN), text.index(END)
    if b < a:
        raise ConnectionError('规则片段顺序无效。')
    return text[a:b + len(END)]


def without_entry(config, name=NAME):
    result = parse(config)
    servers = result.get('mcp_servers', {})
    servers.pop(name, None)
    if not servers:
        result.pop('mcp_servers', None)
    return result


def encode(value):
    return None if value is None else base64.b64encode(value).decode('ascii')


def decode(value):
    return None if value is None else base64.b64decode(value)


class Installer:
    def __init__(self, target_home, cli, bridge, backup_root, instance='default', module='all'):
        if module not in ('all', 'queue', 'models'):
            raise ConnectionError('未知工具模块。')
        self.module = module
        self.name = {'all': NAME, 'queue': 'codex-queue', 'models': 'codex-model-dispatch'}[module]
        tag = {'all': 'CODEX_DISPATCH', 'queue': 'CODEX_QUEUE', 'models': 'CODEX_MODELS'}[module]
        self.begin = '<!-- BEGIN ' + tag + '_RULES v1 -->'
        self.end = '<!-- END ' + tag + '_RULES v1 -->'
        self.mcp_begin = ('# BEGIN ' + tag + '_MCP v1\n').encode()
        self.mcp_end = ('# END ' + tag + '_MCP v1\n').encode()
        self.home = pathlib.Path(target_home).resolve()
        self.cli = str(pathlib.Path(cli).resolve())
        self.bridge = pathlib.Path(bridge).resolve()
        self.dotnet = shutil.which('dotnet')
        self.lock_root = pathlib.Path(backup_root).resolve() / hashlib.sha256(str(self.home).encode()).hexdigest()[:20]
        self.backups = self.lock_root if module == 'all' else self.lock_root / module
        self.receipt = self.backups / 'receipt.json'
        self.instance = instance

    @property
    def rule(self):
        if self.module == 'all':
            return RULE
        queue_prefixes = ('- 临时 SolidWorks', '- 完成测试', '- 固定套件', '- 使用许可', '- 报告 simulationOnly', '- 顺序占用', '- 原聊天接续', '- 接续接单', '- 接单默认', '- 宿主复用', '- 无活动清理', '- 操作段释放')
        lines = [line for line in RULE.splitlines()[2:-1]
                 if line.startswith('- sessionId') or
                 (line.startswith(queue_prefixes) if self.module == 'queue' else not line.startswith(queue_prefixes))]
        title = '测试排队工具' if self.module == 'queue' else '模型调度工具'
        return self.begin + '\n## ' + title + '\n' + '\n'.join(lines) + '\n' + self.end

    def rule_block(self, data):
        if self.module == 'all':
            return block(data)
        text = (data or b'').decode('utf-8-sig')
        if text.count(self.begin) != text.count(self.end) or text.count(self.begin) > 1:
            raise ConnectionError('模块规则标记不完整或重复，停止修改。')
        if self.begin not in text:
            return None
        a, b = text.index(self.begin), text.index(self.end)
        if b < a:
            raise ConnectionError('模块规则顺序无效。')
        return text[a:b + len(self.end)]

    def without_entry(self, config):
        return without_entry(config, self.name)

    def entry(self):
        if not self.bridge.is_file() or not self.dotnet:
            raise ConnectionError('未找到编译后的 MCP 桥或 dotnet，请先编译 Release。')
        args = [str(self.bridge)] + (['--instance', self.instance] if self.instance != 'default' else [])
        if self.module != 'all':
            args += ['--module', self.module]
        return {'command': str(pathlib.Path(self.dotnet).resolve()), 'args': args}

    def rules_path(self):
        override = self.home / 'AGENTS.override.md'
        return override if read(override) and override.read_text(encoding='utf-8-sig').strip() else self.home / 'AGENTS.md'

    def cli_run(self, home, *args):
        env = dict(os.environ, CODEX_HOME=str(home))
        result = subprocess.run([self.cli, 'mcp', *args], env=env, capture_output=True, timeout=20,
                                creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
        if result.returncode:
            raise ConnectionError('Codex CLI 注册校验失败（退出码 %d），原配置不会被直接覆盖。' % result.returncode)
        return result.stdout.decode('utf-8-sig')

    @contextlib.contextmanager
    def lock(self):
        self.backups.mkdir(parents=True, exist_ok=True)
        with (self.lock_root / 'installer.lock').open('a+b') as f:
            f.seek(0)
            if os.name == 'nt':
                import msvcrt
                try:
                    msvcrt.locking(f.fileno(), msvcrt.LK_NBLCK, 1)
                except OSError as ex:
                    raise ConnectionError('另一个连接操作正在执行，请等待完成。') from ex
            else:
                import fcntl
                fcntl.flock(f, fcntl.LOCK_EX | fcntl.LOCK_NB)
            try:
                yield
            finally:
                f.seek(0)
                if os.name == 'nt':
                    msvcrt.locking(f.fileno(), msvcrt.LK_UNLCK, 1)

    def inspect(self):
        config = read(self.home / 'config.toml')
        entry = parse(config).get('mcp_servers', {}).get(self.name)
        expected = self.entry()
        matches = bool(entry and entry.get('command') == expected['command'] and entry.get('args', []) == expected['args']
                       and entry.get('enabled', True) and not (set(entry) - {'command', 'args', 'enabled', 'startup_timeout_sec', 'tool_timeout_sec'}))
        rules = read(self.rules_path())
        return {'status': ('CLIENT_RELOAD_REQUIRED' if self.rule_block(rules) == self.rule else 'MCP_REGISTERED') if matches else 'NOT_CONNECTED', 'targetHome': str(self.home),
                'mcpRegistered': matches, 'rulesInstalled': self.rule_block(rules) == self.rule, 'rulesPath': str(self.rules_path()),
                'desktopSessionsVerified': False, 'bridgePath': str(self.bridge)}

    def probe(self, instance='default'):
        messages = [
            {'jsonrpc': '2.0', 'id': 1, 'method': 'initialize', 'params': {'protocolVersion': '2025-06-18'}},
            {'jsonrpc': '2.0', 'id': 2, 'method': 'tools/list'},
            {'jsonrpc': '2.0', 'id': 3, 'method': 'tools/call', 'params': {'name': 'get_health', 'arguments': {}}}
        ]
        self.entry()
        result = subprocess.run([self.dotnet, str(self.bridge), '--instance', instance] + (['--module', self.module] if self.module != 'all' else []),
                                input=''.join(json.dumps(v) + '\n' for v in messages), text=True, encoding='utf-8',
                                capture_output=True, timeout=10,
                                creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
        try:
            replies = {v['id']: v for v in map(json.loads, result.stdout.splitlines())}
            tools = replies[2]['result']['tools']
            health = json.loads(replies[3]['result']['content'][0]['text'])
            return {'bridgeReachable': result.returncode == 0 and replies[1]['result']['serverInfo']['name'] == self.name,
                    'toolCount': len(tools), 'daemonOnline': health.get('daemonRunning', False),
                    'serviceStatus': 'ONLINE' if health.get('daemonRunning') else health.get('code', 'UNKNOWN')}
        except (ValueError, KeyError, TypeError):
            raise ConnectionError('MCP 桥初始化或工具握手失败。')

    def staged(self, config, folder, operation):
        if operation != 'add':
            raise ConnectionError('移除注册必须根据安装记录移除自有片段。')
        folder.mkdir(parents=True)
        # 空配置生成，避免 CLI 的迁移逻辑重写用户原有服务器。
        entry = self.entry()
        self.cli_run(folder, 'add', self.name, '--', entry['command'], *entry['args'])
        result = json.loads(self.cli_run(folder, 'get', self.name, '--json'))
        transport = result.get('transport', result)
        if transport.get('command') != entry['command'] or transport.get('args') != entry['args']:
            raise ConnectionError('CLI 返回的桥路径与待安装路径不一致。')
        fragment = read(folder / 'config.toml')
        if not fragment or self.without_entry(fragment):
            raise ConnectionError('CLI 生成了自有注册以外的配置，停止安装。')
        if config and (self.mcp_begin in config or self.mcp_end in config):
            raise ConnectionError('配置含有遗留或不完整的注册标记，请先检查恢复记录。')
        suffix = (b'\n\n' if config else b'') + self.mcp_begin + fragment.rstrip(b'\r\n') + b'\n' + self.mcp_end
        after = (config or b'') + suffix
        if self.without_entry(config) != self.without_entry(after):
            raise ConnectionError('注册片段合并改变了无关配置，停止安装。')
        return after

    def save_receipt(self, value):
        atomic(self.receipt, read(self.receipt), json.dumps(value, ensure_ascii=False, indent=2).encode('utf-8'))
        self.audit(value['state'])

    def audit(self, state, message=None):
        # 不记录配置正文、规则正文或 CLI 输出，备份单独保存在本机。
        with (self.backups / 'connection-bug.jsonl').open('a', encoding='utf-8') as log:
            log.write(json.dumps({'time': datetime.now(timezone.utc).isoformat(), 'state': state,
                                  'targetHome': str(self.home), 'message': message}, ensure_ascii=False) + '\n')

    def transaction(self, changes, receipt, checkpoint):
        previous_receipt = json.loads(self.receipt.read_text(encoding='utf-8')) if self.receipt.exists() else None
        # 只保留一次回滚目标，避免重试记录无限嵌套。
        if previous_receipt:
            previous_receipt.pop('rollbackReceipt', None)
        journal = dict(receipt, state='PREPARED', rollbackReceipt=previous_receipt,
                       changes=[{'path': str(p), 'before': encode(a), 'after': encode(b)} for p, a, b in changes])
        self.save_receipt(journal)  # 写前日志，崩溃后能够检查每个文件是否真正落盘。
        try:
            for index, (path, before, after) in enumerate(changes):
                atomic(path, before, after)
                checkpoint('config-written' if index == 0 else 'rules-written')
            journal['state'] = receipt['state']
            self.save_receipt(journal)
        except BaseException:
            self.rollback(journal)
            raise

    def rollback(self, journal):
        conflict = False
        for item in reversed(journal['changes']):
            path, before, after = pathlib.Path(item['path']), decode(item['before']), decode(item['after'])
            current = read(path)
            if current == before:
                continue
            if current != after:
                conflict = True
                continue
            try:
                atomic(path, after, before)
            except (OSError, ConnectionError):
                conflict = True
        journal['state'] = 'RECOVERY_REQUIRED' if conflict else 'ROLLED_BACK'
        if not conflict and journal.get('rollbackReceipt'):
            previous = journal['rollbackReceipt']
            self.save_receipt(previous)
            journal['state'] = previous['state']
        else:
            self.save_receipt(journal)

    def connect(self, checkpoint=lambda _: None):
        with self.lock():
            self.home.mkdir(parents=True, exist_ok=True)
            previous = json.loads(self.receipt.read_text(encoding='utf-8')) if self.receipt.exists() else None
            if previous and previous['state'] in ('PREPARED', 'RECOVERY_REQUIRED'):
                raise ConnectionError('存在未完成事务，请先执行 Recover 并检查备份。')
            status = self.inspect()
            if previous and previous['state'] == 'INSTALLED':
                if status['mcpRegistered'] and status['rulesInstalled'] and previous['rulesPath'] == status['rulesPath']:
                    return status
                raise ConnectionError('安装后的注册或规则已变化，停止覆盖。')
            config_path = self.home / 'config.toml'
            before = read(config_path)
            existing = parse(before).get('mcp_servers', {}).get(self.name)
            if existing is not None and not status['mcpRegistered']:
                raise ConnectionError('同名 MCP 指向其他程序或具有不同设置，停止覆盖。')
            rules_path = self.rules_path()
            rules_before = read(rules_path)
            if self.rule_block(rules_before) is not None:
                raise ConnectionError('发现没有安装记录的受管规则，停止覆盖。')
            folder = self.backups / uuid.uuid4().hex
            folder.mkdir()
            if before is not None:
                (folder / 'config.before.toml').write_bytes(before)
            if rules_before is not None:
                (folder / 'rules.before.md').write_bytes(rules_before)
            after = before if existing is not None else self.staged(before, folder / 'staging', 'add')
            separator = b'\n\n' if rules_before else b''
            rules_after = (rules_before or b'') + separator + self.rule.encode('utf-8') + b'\n'
            receipt = {'state': 'INSTALLED', 'createdRegistration': existing is None, 'rulesPath': str(rules_path),
                       'rulesBefore': encode(rules_before), 'rulesAfter': encode(rules_after), 'backupPath': str(folder),
                       'configSuffix': encode(after[len(before or b''):] if existing is None else None)}
            self.transaction([(config_path, before, after), (rules_path, rules_before, rules_after)], receipt, checkpoint)
            return self.inspect()

    def disconnect(self):
        with self.lock():
            if not self.receipt.exists():
                raise ConnectionError('没有本安装器的连接记录，不能解除其他程序的注册。')
            receipt = json.loads(self.receipt.read_text(encoding='utf-8'))
            if receipt['state'] == 'DISCONNECTED':
                return {'status': 'DISCONNECTED'}
            if receipt['state'] != 'INSTALLED':
                raise ConnectionError('安装记录不是已连接状态，请先检查或恢复。')
            if not self.inspect()['mcpRegistered']:
                raise ConnectionError('注册已经改变，停止删除。')
            path = self.home / 'config.toml'
            before = read(path)
            after = before
            if receipt['createdRegistration']:
                suffix = decode(receipt.get('configSuffix'))
                if suffix:
                    if not before or before.count(suffix) != 1:
                        raise ConnectionError('自有注册片段已经改变，停止自动删除。')
                    after = before.replace(suffix, b'', 1)
                else:
                    # 兼容旧安装记录：只在整个配置仍等于安装时内容时恢复原字节。
                    old_change = receipt['changes'][0]
                    if before != decode(old_change['after']):
                        raise ConnectionError('旧版本注册已被修改，请核对备份后恢复。')
                    after = decode(old_change['before'])
                if self.without_entry(before) != self.without_entry(after):
                    raise ConnectionError('解除连接将影响其他设置，停止操作。')
            rules_path = pathlib.Path(receipt['rulesPath'])
            rules_before = read(rules_path)
            if self.rule_block(rules_before) != self.rule:
                raise ConnectionError('受管规则已被修改，停止自动删除。')
            original_rules = decode(receipt['rulesBefore'])
            original_install = (original_rules or b'') + (b'\n\n' if original_rules else b'') + self.rule.encode('utf-8') + b'\n'
            # Upgrade的rulesAfter可能包含后来新增的其他模块规则，不能整文件恢复旧快照。
            if rules_before == decode(receipt['rulesAfter']) and rules_before == original_install:
                rules_after = decode(receipt['rulesBefore'])
            else:
                rules_after = rules_before.replace(self.rule.encode('utf-8'), b'', 1)
            self.transaction([(path, before, after), (rules_path, rules_before, rules_after)], dict(receipt, state='DISCONNECTED'), lambda _: None)
            return {'status': 'DISCONNECTED', 'targetHome': str(self.home)}

    def recover(self):
        with self.lock():
            if not self.receipt.exists():
                raise ConnectionError('没有恢复记录。')
            value = json.loads(self.receipt.read_text(encoding='utf-8'))
            if value['state'] not in ('PREPARED', 'RECOVERY_REQUIRED'):
                raise ConnectionError('当前没有待恢复事务。')
            self.rollback(value)
            return {'status': value['state'], 'receiptPath': str(self.receipt)}

    def upgrade(self):
        with self.lock():
            if not self.receipt.exists():
                raise ConnectionError('没有已安装记录，请先连接。')
            receipt = json.loads(self.receipt.read_text(encoding='utf-8'))
            if receipt['state'] != 'INSTALLED' or not self.inspect()['mcpRegistered']:
                raise ConnectionError('注册不匹配或有待恢复事务，停止规则更新。')
            rules_path = pathlib.Path(receipt['rulesPath'])
            if self.rules_path() != rules_path:
                raise ConnectionError('生效规则文件已改变，停止自动更新。')
            before = read(rules_path)
            expected = self.rule_block(decode(receipt['rulesAfter']))
            if not expected or self.rule_block(before) != expected:
                raise ConnectionError('受管规则已被修改，停止覆盖。')
            if expected == self.rule:
                return self.inspect()
            after = before.replace(expected.encode('utf-8'), self.rule.encode('utf-8'), 1)
            config_path = self.home / 'config.toml'
            config = read(config_path)
            updated = dict(receipt, rulesAfter=encode(after))
            self.transaction([(config_path, config, config), (rules_path, before, after)], updated, lambda _: None)
            return self.inspect()


def module_installers(installer):
    return [Installer(installer.home, installer.cli, installer.bridge, installer.lock_root.parent,
                      instance=installer.instance, module=module) for module in ('queue', 'models')]


def inspect_modules(installer):
    profiles = module_installers(installer)
    states = [dict(p.inspect(), module=p.module, serverName=p.name) for p in profiles]
    registered = [s for s in states if s['mcpRegistered']]
    legacy = installer.inspect()
    result = dict(legacy)
    if registered:
        result.update(mcpRegistered=True, rulesInstalled=all(s['rulesInstalled'] for s in registered),
                      status='CLIENT_RELOAD_REQUIRED')
    result['modules'] = states
    result['legacyRegistered'] = legacy['mcpRegistered']
    # 同一进程实现、同一服务，握手选择一个实际登记的入口即可。
    probe = next((p for p, state in zip(profiles, states) if state['mcpRegistered']), installer)
    result.update(probe.probe(instance=installer.instance))
    return result


def connect_modules(installer):
    for profile in module_installers(installer):
        # 先接好新入口，最后才移除有登记凭据的旧兼容入口。
        profile.connect()
    if installer.inspect()['mcpRegistered'] and installer.receipt.exists():
        installer.upgrade()
        installer.disconnect()
    return inspect_modules(installer)


def disconnect_modules(installer):
    for profile in module_installers(installer):
        if profile.inspect()['mcpRegistered']:
            profile.disconnect()
    if installer.inspect()['mcpRegistered']:
        installer.disconnect()
    return inspect_modules(installer)


def main():
    parser = argparse.ArgumentParser(description='Codex 调度工具连接安装器')
    parser.add_argument('--action', choices=['Inspect', 'Connect', 'Disconnect', 'Recover', 'Upgrade', 'Split', 'InspectModules', 'DisconnectModules'], default='Inspect')
    parser.add_argument('--home')
    parser.add_argument('--user-home')
    parser.add_argument('--instance', default='default')
    parser.add_argument('--module', choices=['all', 'queue', 'models'], default='all')
    parser.add_argument('--cli')
    parser.add_argument('--bridge', type=pathlib.Path, default=pathlib.Path(__file__).resolve().parents[1] / 'src/Dispatch.McpBridge/bin/Release/net8.0/Dispatch.McpBridge.dll')
    parser.add_argument('--backups', type=pathlib.Path, default=pathlib.Path(os.environ.get('LOCALAPPDATA', pathlib.Path.home())) / 'CodexDispatch' / 'connections')
    args = parser.parse_args()
    cli = discover_cli(args.cli)
    home = resolve_home(args.home, os.environ.get('CODEX_HOME'), args.user_home)
    installer = Installer(home, cli, args.bridge, args.backups, instance=args.instance, module=args.module)
    if args.action in ('Split', 'InspectModules', 'DisconnectModules'):
        if args.module != 'all':
            raise ConnectionError('组合接入操作不能指定单个模块。')
        result = {'Split': connect_modules, 'InspectModules': inspect_modules, 'DisconnectModules': disconnect_modules}[args.action](installer)
    else:
        result = getattr(installer, args.action.lower())()
    if args.action in ('Inspect', 'Connect'):
        try:
            result.update(installer.probe(instance=args.instance))
        except (ConnectionError, subprocess.TimeoutExpired):
            result.update(bridgeReachable=False, daemonOnline=False, serviceStatus='BRIDGE_CHECK_FAILED')
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    try:
        main()
    except Exception as ex:
        print(json.dumps({'status': 'FAILED', 'message': str(ex)}, ensure_ascii=False))
        sys.exit(1)
