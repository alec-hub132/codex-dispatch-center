"""真实 STDIO MCP → Named Pipe → SQLite → 独立模拟进程。仅保留本轮证据。"""
import concurrent.futures
import json
import os
import pathlib
import queue
import subprocess
import threading
import time
import unittest
import uuid
import sys
import importlib.util
import shutil
import hashlib
from datetime import datetime, timezone

sys.stdout.reconfigure(encoding='utf-8')
sys.stderr.reconfigure(encoding='utf-8')

ROOT = pathlib.Path(__file__).resolve().parents[1]
CONFIG = os.environ.get('DISPATCH_CONFIGURATION', 'Debug')
DAEMON = ROOT / f'src/Dispatch.Daemon/bin/{CONFIG}/net8.0/Dispatch.Daemon.dll'
BRIDGE = ROOT / f'src/Dispatch.McpBridge/bin/{CONFIG}/net8.0/Dispatch.McpBridge.dll'


class Client:
    def __init__(self, instance, command=None):
        self.proc = subprocess.Popen(command or ['dotnet', str(BRIDGE), '--instance', instance], stdin=subprocess.PIPE,
                                     stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, encoding='utf-8')
        self.lines = queue.Queue()
        self.seq = 0
        threading.Thread(target=lambda: [self.lines.put(line) for line in self.proc.stdout], daemon=True).start()
        reply = self.rpc('initialize', {'protocolVersion': '2025-06-18', 'capabilities': {},
                                       'clientInfo': {'name': '调度集成测试', 'version': '1'}})
        assert 'result' in reply, reply
        self.proc.stdin.write(json.dumps({'jsonrpc': '2.0', 'method': 'notifications/initialized'}) + '\n')
        self.proc.stdin.flush()

    def rpc(self, method, params=None):
        self.seq += 1
        self.proc.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': self.seq, 'method': method, 'params': params or {}}) + '\n')
        self.proc.stdin.flush()
        result = json.loads(self.lines.get(timeout=25))
        assert result['id'] == self.seq, result
        return result

    def tool(self, name, args=None, allow_error=False):
        result = self.rpc('tools/call', {'name': name, 'arguments': args or {}})
        assert 'result' in result, result
        result = result['result']
        if not allow_error:
            assert not result.get('isError'), result
        return json.loads(result['content'][0]['text'])

    def close(self):
        if self.proc.poll() is None:
            self.proc.stdin.close()
            try:
                self.proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.proc.kill()
                self.proc.wait()
        self.proc.stdout.close()


class IntegrationTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(DAEMON.exists(), '调度服务尚未实现或编译')
        self.instance = 'test-' + uuid.uuid4().hex
        self.data = ROOT / 'artifacts/integration' / self.instance
        self.data.mkdir(parents=True)
        self.clients = []
        self.daemon = None
        self.log = open(self.data / 'daemon.log', 'a', encoding='utf-8')
        self.start_daemon()

    def client(self):
        c = Client(self.instance)
        self.clients.append(c)
        return c

    def test_module_bridges_expose_only_their_own_tools_and_disabled_gate_is_independent(self):
        queue_client = Client(self.instance, ['dotnet', str(BRIDGE), '--instance', self.instance, '--module', 'queue'])
        model_client = Client(self.instance, ['dotnet', str(BRIDGE), '--instance', self.instance, '--module', 'models'])
        self.clients += [queue_client, model_client]
        queue_tools = {t['name'] for t in queue_client.rpc('tools/list')['result']['tools']}
        model_tools = {t['name'] for t in model_client.rpc('tools/list')['result']['tools']}
        self.assertEqual({'get_health', 'get_module_settings'}, queue_tools & model_tools)
        self.assertIn('request_resource', queue_tools)
        self.assertFalse({'begin_queue_wake_send','set_queue_resume_consent','get_queue_resumption_settings','get_idle_cleanup_settings','set_idle_cleanup_settings','begin_idle_cleanup_send','set_idle_cleanup_owner','manual_release_resource','get_queue_owners','manual_dispatch_test','list_queue_changes'} & (queue_tools | model_tools))
        self.assertIn('error',queue_client.rpc('tools/call',{'name':'manual_release_resource','arguments':{}}))
        self.assertFalse({'set_host_recovery_settings','get_host_recovery_settings','recheck_host_recovery'} & (queue_tools | model_tools))
        self.assertTrue({'accept_resource','retry_resource','get_queue_binding'} <= queue_tools)
        self.assertFalse({'accept_resource','retry_resource','get_queue_binding'} & model_tools)
        self.assertTrue({'capture_workspace', 'yield_resource', 'resume_resource', 'restore_workspace', 'get_resource_events'} <= queue_tools)
        self.assertIn('request_supervisor_takeover', model_tools)
        self.assertIn('arm_model_continuation', model_tools)
        self.assertNotIn('arm_model_continuation', queue_tools)
        self.assertEqual('NOT_FOUND', model_client.tool('arm_model_continuation', {'decisionId':'unknown','token':'wrong'}, allow_error=True)['code'])
        self.assertIn('error', queue_client.rpc('tools/call', {'name': 'request_model_switch', 'arguments': {}}))
        self.assertIn('error', model_client.rpc('tools/call', {'name': 'submit_test', 'arguments': {}}))
        settings = queue_client.tool('get_module_settings')
        self.private_call('set_module_settings', dict(settings, queueEnabled=False))
        self.assertEqual('QUEUE_DISABLED', queue_client.tool('submit_test', {'requestId': 'off', 'sessionId': 'test', 'suiteId': 'mock-sw'}, allow_error=True)['code'])
        request = {'requestId': 'model', 'sessionId': 'test', 'model': 'worker', 'effort': 'medium', 'reason': '独立性验证', 'handoff': '只读任务'}
        self.assertEqual('UNSUPPORTED', model_client.tool('request_model_switch', request)['status'])
        settings = model_client.tool('get_module_settings')
        self.private_call('set_module_settings', dict(settings, queueEnabled=True, modelsEnabled=False))
        self.assertEqual('MODELS_DISABLED', model_client.tool('request_model_switch', dict(request, requestId='disabled'), allow_error=True)['code'])
        job = self.submit(queue_client, 'queue-only')
        self.assertEqual('Passed', self.finish(queue_client, job['id'])['state'])

    def test_oversized_input_keeps_next_mcp_request_and_responses_do_not_merge(self):
        c = self.client()
        c.seq += 1
        expected_id = c.seq
        valid = json.dumps({'jsonrpc': '2.0', 'id': expected_id, 'method': 'ping'})
        c.proc.stdin.write('x' * (128 * 1024 + 20000) + '\n' + valid + '\n')
        c.proc.stdin.flush()
        rejected = json.loads(c.lines.get(timeout=25))
        self.assertEqual(-32602, rejected['error']['code'])
        accepted = json.loads(c.lines.get(timeout=25))
        self.assertEqual(expected_id, accepted['id'])
        self.assertEqual({}, accepted['result'])
        self.assertTrue(c.tool('get_health')['daemonRunning'])

    def test_host_recovery_toggle_is_private_persistent_and_isolated_from_real_host(self):
        c = self.client()
        original = c.tool('get_module_settings')
        settings = self.private_call('get_host_recovery_settings', {})['settings']
        self.assertFalse(settings['enabled'])
        self.private_call('set_host_recovery_settings', dict(settings, enabled=True))
        self.assertTrue(c.tool('get_health')['solidWorksCrashRecoveryEnabled'])
        self.daemon.kill()
        self.daemon.wait()
        self.start_daemon()
        self.assertTrue(self.private_call('get_host_recovery_settings', {})['settings']['enabled'])
        self.assertIsNone(self.private_call('get_host_recovery_settings', {})['incident'])
        self.assertEqual(original, c.tool('get_module_settings'))
        saved = self.private_call('get_host_recovery_settings', {})['settings']
        self.private_call('set_host_recovery_settings', dict(saved, enabled=False))
        self.assertFalse(c.tool('get_health')['solidWorksCrashRecoveryEnabled'])

    def test_resource_step_releases_before_offline_fixes_and_retest_joins_tail(self):
        c=self.client();settings=c.tool('get_module_settings');self.private_call('set_module_settings',dict(settings,modelsEnabled=False))
        def request(label):return c.tool('request_resource',dict(requestId=label,sessionId=label,resource='app:step-fixture',purpose='纯虚拟操作段验证，不操作软件',durationSeconds=90))
        def offered(p):
            key=dict(leaseId=p['job']['id'],token=p['token'])
            for _ in range(60):
                current=c.tool('get_resource',key)
                if current['status']=='Offered':return current
                time.sleep(.05)
            self.fail('未取得预留')
        def accept(p):
            current=offered(p)
            return c.tool('accept_resource',dict(leaseId=p['job']['id'],token=p['token'],offerId=current['offerId'],generation=current['job']['generation']))
        a=request('a-first');a=accept(a);b=request('b-waiting');third=request('c-waiting')
        permit=self.data/'private-step-permit.json';permit.write_text(json.dumps(dict(leaseId=a['job']['id'],token=a['token'],generation=a['job']['generation'])),encoding='utf-8')
        worker=self.data/'test-step.py';worker.write_text("import os,json,pathlib,sys\ntry:\n raise RuntimeError('模拟测试发现问题')\nfinally:\n pathlib.Path(os.environ['CODEX_RESOURCE_STEP_CLEANUP']).write_text(json.dumps(dict(stepId=os.environ['CODEX_RESOURCE_STEP_ID'],leaseId=os.environ['CODEX_RESOURCE_STEP_LEASE'],generation=int(os.environ['CODEX_RESOURCE_STEP_GENERATION']),operationsStopped=True,ownedResourcesCleaned=True)))\n",encoding='utf-8')
        output=self.data/'steps'
        executed=subprocess.run([sys.executable,str(ROOT/'scripts/Run-ResourceStep.py'),'--permit',str(permit),'--output',str(output),'--timeout','3','--instance',self.instance,'--bridge',str(BRIDGE),'--',sys.executable,str(worker)],capture_output=True,text=True,encoding='utf-8',timeout=30)
        self.assertEqual(1,executed.returncode)
        result=json.loads(executed.stdout);self.assertEqual('Released',result['resourceStatus']);self.assertEqual('OK',result['cleanupStatus']);self.assertFalse(result['testPassed']);self.assertTrue(result['cleanupConfirmed'])
        fixing=subprocess.Popen([sys.executable,'-c','import time;time.sleep(20)'],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
        try:
            b=accept(b);self.assertIsNone(fixing.poll(),'A离线修复尚未结束时，B应已获准')
            retry=request('a-retest');self.assertEqual('Queued',retry['status'])
            c.tool('release_resource',dict(leaseId=b['job']['id'],token=b['token'],generation=b['job']['generation'],cleanupConfirmed=True))
            third=accept(third);self.assertEqual('Queued',c.tool('get_resource',dict(leaseId=retry['job']['id'],token=retry['token']))['status'])
            c.tool('release_resource',dict(leaseId=third['job']['id'],token=third['token'],generation=third['job']['generation'],cleanupConfirmed=True))
            retry=accept(retry);c.tool('release_resource',dict(leaseId=retry['job']['id'],token=retry['token'],generation=retry['job']['generation'],cleanupConfirmed=True))
            audit=dict(simulationOnly=True,hostAccessed=False,aTestFailedButReleased=True,bGrantedWhileAOfflineWorkerRunning=fixing.poll() is None,retestRespectedFifo=True,phaseResult=result)
            (self.data/'resource-step-audit.json').write_text(json.dumps(audit,ensure_ascii=False,indent=2),encoding='utf-8')
        finally:
            if fixing.poll() is None:fixing.terminate()
            fixing.wait(timeout=5)
        for file in output.rglob('*'):
            if file.is_file():self.assertNotIn(a['token'],file.read_text(encoding='utf-8'))

    def start_daemon(self):
        daemon_env = dict(os.environ, DISPATCH_TEST_NO_HOST_RECOVERY='1')
        self.daemon = subprocess.Popen(['dotnet', str(DAEMON), '--instance', self.instance, '--data', str(self.data)],
                                       stdout=self.log, stderr=self.log, env=daemon_env)
        deadline = time.monotonic() + 12
        c = self.client()
        while time.monotonic() < deadline:
            result = c.tool('get_health', allow_error=True)
            if result.get('daemonRunning'):
                return
            self.assertIsNone(self.daemon.poll(), '调度服务启动失败，见 daemon.log')
            time.sleep(.1)
        self.fail('调度服务未就绪')

    def test_workspace_invalid_calls_and_environment_probe_never_enter_host(self):
        c = self.client()
        report = subprocess.check_output(['dotnet', str(DAEMON.parent / 'workspace/Runner.Workspace.dll'), '--environment-only'], text=True, encoding='utf-8')
        self.assertFalse(json.loads(report)['hostAccessed'])
        health = c.tool('get_health')
        self.assertTrue(health['workspaceAdapterAvailable'])
        self.assertFalse(health['automaticResourceWakeupSupported'])
        request = {'requestId': 'desktop', 'sessionId': 'window-a', 'resource': 'desktop', 'purpose': '仅验证拒绝分支，不操作宿主'}
        permit = c.tool('request_resource', request)
        for _ in range(60):
            current = c.tool('get_resource', {'leaseId': permit['job']['id'], 'token': permit['token']})
            if current['status'] == 'Offered':
                current = c.tool('accept_resource', {'leaseId': permit['job']['id'], 'token': permit['token'], 'offerId': current['offerId'], 'generation': current['job']['generation']})
                break
            time.sleep(.025)
        capture = {'leaseId': permit['job']['id'], 'token': permit['token'], 'generation': current['job']['generation'],
                   'requestId': 'capture', 'completedWork': '测试拒绝分支', 'remainingWork': '无', 'safePointConfirmed': True}
        self.assertEqual('INVALID_RESOURCE', c.tool('capture_workspace', capture, allow_error=True)['code'])
        self.assertEqual('INVALID_PERMIT_TOKEN', c.tool('capture_workspace', dict(capture, token='bad'), allow_error=True)['code'])
        self.assertEqual('NOT_FOUND', c.tool('restore_workspace', {'leaseId': permit['job']['id'], 'token': permit['token'], 'generation': 1, 'checkpointId': 'none'}, allow_error=True)['code'])
        self.assertEqual([], c.tool('get_resource_events', {'leaseId': permit['job']['id'], 'token': permit['token'], 'afterSequence': 0}))
        c.tool('release_resource', {'leaseId': permit['job']['id'], 'token': permit['token'], 'generation': current['job']['generation'], 'cleanupConfirmed': True})

    def submit(self, c, name, suite='mock-sw'):
        return c.tool('submit_test', {'requestId': 'request-' + name, 'sessionId': name, 'suiteId': suite})

    def finish(self, c, job_id):
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline:
            j = c.tool('wait_test', {'jobId': job_id, 'maxWaitSeconds': 1})
            if j['state'] not in ['Queued', 'Running', 'Cleanup']:
                return j
        self.fail('任务未结束')

    def private_call(self, method, arguments):
        # 配置走与桌面相同的本用户私有管道，MCP不暴露授权或主管配置入口。
        sid = subprocess.check_output(['powershell', '-NoProfile', '-Command',
            '[Security.Principal.WindowsIdentity]::GetCurrent().User.Value'], text=True).strip()
        name = hashlib.sha256(sid.encode()).hexdigest().upper()[:20]
        request_id = uuid.uuid4().hex
        for attempt in range(50):
            try:
                connection = open(r'\\.\pipe\CodexDispatch-' + name + '-' + self.instance, 'r+b', buffering=0)
                break
            except OSError as error:
                if error.errno not in (2,22) or attempt==49: raise
                time.sleep(.02)  # 仅在写入前等管道可连接，不重发任何已发送请求。
        with connection as pipe:
            pipe.write((json.dumps({'version': 1, 'requestId': request_id, 'method': method, 'arguments': arguments}) + '\n').encode())
            line = bytearray()
            # 与服务独立的4Mi字符响应上限配套；UTF-8字节最多按四倍预留。
            while len(line) <= 16 * 1024 * 1024:
                ch = pipe.read(1)
                if not ch or ch == b'\n': break
                line.extend(ch)
            self.assertLessEqual(len(line),16 * 1024 * 1024,'私有接口响应超出上限')
        reply = json.loads(line)
        self.assertEqual(request_id, reply['requestId'])
        self.assertTrue(reply['ok'], reply)
        return reply['result']

    def test_authorized_handoff_plan_and_receipts_do_not_send_messages(self):
        (self.data / 'model-handoff-authorization.json').write_text(json.dumps({'threads': ['bound-test-thread']}), encoding='utf-8')
        (self.data / 'diagnostics').mkdir(exist_ok=True)
        (self.data / 'diagnostics/model-access.json').write_text(json.dumps({'standaloneCatalog': {'reachable': True, 'models': [{'model': 'mock-model', 'efforts': ['medium']}, {'model': 'mock-supervisor', 'efforts': ['high']}]}}), encoding='utf-8')
        self.private_call('set_supervisor_settings', {'sessionId': 'bound-test-thread', 'model': 'mock-supervisor', 'effort': 'high'})
        c = self.client()
        args = {'requestId': 'handoff-test', 'sessionId': 'bound-test-thread', 'model': 'mock-model', 'effort': 'medium', 'reason': '模拟交接协议', 'handoff': '只验证状态，不发送实际消息',
                'plannerModel': 'mock-supervisor', 'plannerEffort': 'high', 'completionCriteria': '完成协议核对后立即返回主管'}
        home = self.data / 'runtime-fixture'
        sessions = home / 'sessions' / '2026' / '10' / '29'
        sessions.mkdir(parents=True)
        self.private_call('set_runtime_source', {'home': str(home)})
        def write_runtime(model, effort):
            records = [{'type': 'session_meta', 'payload': {'id': 'bound-test-thread'}},
                       {'type': 'turn_context', 'timestamp': datetime.now(timezone.utc).isoformat(),
                        'payload': {'turn_id': 'fixture-turn', 'model': model, 'effort': effort}}]
            (sessions / 'rollout-bound-test-thread.jsonl').write_text(''.join(json.dumps(r) + '\n' for r in records), encoding='utf-8')
        self.assertEqual('PLANNER_RUNTIME_UNAVAILABLE', c.tool('request_model_switch', args, allow_error=True)['code'])
        write_runtime('mock-supervisor', 'high')
        d = c.tool('request_model_switch', args)
        self.assertEqual('AWAITING_CALLER_EXECUTION', d['status'])
        receipt = {'decisionId': d['id'], 'token': d['execution']['token'], 'evidence': '模拟测试状态回执，未调用聊天发送工具'}
        self.assertEqual('SENDING', c.tool('record_model_handoff', dict(receipt, outcome='sending'))['status'])
        self.assertEqual('SENT_AWAITING_VERIFICATION', c.tool('record_model_handoff', dict(receipt, outcome='sent'))['status'])
        self.assertEqual('HANDOFF_IN_PROGRESS', c.tool('request_model_switch', dict(args, requestId='another'), allow_error=True)['code'])
        self.assertEqual('INVALID_STATE', c.tool('record_model_handoff', dict(receipt, outcome='work_done'), allow_error=True)['code'])
        verify_args = {'decisionId': d['id'], 'token': d['execution']['token']}
        self.assertEqual('AWAITING_MATCH', c.tool('verify_model_runtime', verify_args)['status'])
        self.assertEqual('AWAITING_MATCH', c.tool('record_model_handoff', dict(receipt, outcome='runtime_verified', observedModel='mock-model', observedEffort='medium'), allow_error=True)['code'])
        write_runtime('mock-model', 'medium')
        self.assertEqual('EXECUTING', c.tool('verify_model_runtime', verify_args)['decision']['status'])
        ret = c.tool('request_supervisor_takeover', dict(verify_args, reason='测试提前交回主管', completedWork='已核验执行模型', remainingWork='主管核对结果', runningOperations='无', resourceState='无'))
        self.assertEqual('RETURN_READY', ret['status'])
        self.assertEqual(('mock-supervisor', 'high'), (ret['execution']['model'], ret['execution']['thinking']))
        self.assertTrue(ret['execution']['returning'])
        self.assertEqual('HANDOFF_IN_PROGRESS', c.tool('request_model_switch', dict(args, requestId='another'), allow_error=True)['code'])
        self.assertEqual('RETURN_SENDING', c.tool('record_model_handoff', dict(receipt, outcome='return_sending'))['status'])
        self.assertEqual('INVALID_STATE', c.tool('record_model_handoff', dict(receipt, outcome='return_sending'), allow_error=True)['code'])
        self.assertEqual('RETURN_SENT_AWAITING_VERIFICATION', c.tool('record_model_handoff', dict(receipt, outcome='return_sent'))['status'])
        self.assertEqual('AWAITING_MATCH', c.tool('record_model_handoff', dict(receipt, outcome='runtime_verified', observedModel='mock-model', observedEffort='medium'), allow_error=True)['code'])
        self.assertEqual('AWAITING_MATCH', c.tool('verify_model_runtime', verify_args)['status'])
        write_runtime('mock-supervisor', 'high')
        self.assertEqual('RETURN_VERIFIED', c.tool('verify_model_runtime', verify_args)['decision']['status'])
        self.assertEqual('RETURN_VERIFIED', c.tool('get_model_decision', {'decisionId': d['id']})['status'])
        self.assertEqual('AWAITING_CALLER_EXECUTION', c.tool('request_model_switch', dict(args, requestId='another'))['status'])

    def test_background_queue_observer_tracks_idle_and_new_input_without_sending(self):
        thread = 'queue-observer-fixture'
        home = self.data / 'observer-runtime'
        folder = home / 'sessions'
        folder.mkdir(parents=True)
        source = folder / ('rollout-' + thread + '.jsonl')
        records = [{'type': 'session_meta', 'payload': {'id': thread}},
                   {'type': 'turn_context', 'timestamp': datetime.now(timezone.utc).isoformat(),
                    'payload': {'turn_id': 'observer-turn', 'model': 'fixture', 'effort': 'medium'}}]
        source.write_text(''.join(json.dumps(r)+'\n' for r in records), encoding='utf-8')
        self.private_call('set_runtime_source', {'home': str(home)})
        self.private_call('set_queue_resume_consent', {'threadId': thread, 'enabled': True})
        self.private_call('set_queue_paused', {'paused': True})
        c = self.client()
        modules = c.tool('get_module_settings')
        self.private_call('set_module_settings', dict(modules, modelsEnabled=False))
        p = c.tool('request_resource', {'requestId': 'observer', 'sessionId': 'alias', 'resource': 'app:observer-fixture',
            'purpose': '只检查隔离日志，不启动宿主或桌面IPC', 'resumeThreadId': thread,
            'sourceTurnId': 'observer-turn', 'remainingWork': '核对模拟状态'})
        key = {'leaseId': p['job']['id'], 'token': p['token']}
        with source.open('a', encoding='utf-8') as f:
            f.write(json.dumps({'type': 'event_msg', 'payload': {'type': 'task_complete', 'turn_id': 'observer-turn'}})+'\n')
        end = time.monotonic()+5
        while time.monotonic() < end:
            state = self.private_call('get_queue_resumption_settings', {})
            if state['sources'][0]['finished']: break
            time.sleep(0.1)
        self.assertTrue(state['sources'][0]['finished'])
        self.assertEqual([], state['wakes'])
        with source.open('a', encoding='utf-8') as f:
            f.write(json.dumps({'type': 'response_item', 'payload': {'type': 'message', 'role': 'user',
                'content': [{'type': 'input_text', 'text': '取消旧任务，安排新的工作'}]}})+'\n')
        end = time.monotonic()+5
        while time.monotonic() < end:
            state = self.private_call('get_queue_resumption_settings', {})
            if state['sources'][0]['blocked']: break
            time.sleep(0.1)
        self.assertTrue(state['sources'][0]['blocked'])
        self.private_call('set_queue_paused', {'paused': False})
        self.assertEqual('WaitingForClient', c.tool('get_resource', key)['status'])
        self.assertEqual([], self.private_call('get_queue_resumption_settings', {})['wakes'])
        c.tool('release_resource', dict(key, generation=0, cleanupConfirmed=False))
        self.assertNotIn(p['token'], (self.data / 'logs' / 'bug.jsonl').read_text(encoding='utf-8'))

    def test_idle_cleanup_observer_and_settings_use_private_api_and_never_interrupt_active_chat(self):
        thread = '22222222-2222-4222-8222-222222222222'
        home = self.data / 'idle-runtime'
        folder = home / 'sessions'
        folder.mkdir(parents=True)
        source = folder / ('rollout-' + thread + '.jsonl')
        records = [{'type': 'session_meta', 'payload': {'id': thread}},
                   {'type': 'turn_context', 'timestamp': datetime.now(timezone.utc).isoformat(),
                    'payload': {'turn_id': 'idle-active-turn', 'model': 'fixture', 'effort': 'medium'}}]
        source.write_text(''.join(json.dumps(r)+'\n' for r in records), encoding='utf-8')
        self.private_call('set_runtime_source', {'home': str(home)})
        c = self.client()
        settings = c.tool('get_module_settings')
        self.private_call('set_module_settings', dict(settings, modelsEnabled=False))
        saved = self.private_call('set_idle_cleanup_settings', {'enabled': True, 'idleMinutes': 1, 'version': 0})
        p = c.tool('request_resource', {'requestId': 'idle-observer', 'sessionId': 'alias-not-identity', 'ownerThreadId': thread,
            'resource': 'app:idle-fixture', 'purpose': '只验证活动观察，不发送真实消息或启动软件'})
        key = {'leaseId': p['job']['id'], 'token': p['token']}
        end = time.monotonic()+5
        while time.monotonic() < end:
            p = c.tool('get_resource', key)
            if p['status'] == 'Offered': break
            time.sleep(.03)
        self.assertEqual('Granted', c.tool('accept_resource', dict(key, offerId=p['offerId'], generation=p['job']['generation']))['status'])
        end = time.monotonic()+5
        while time.monotonic() < end:
            view = self.private_call('get_idle_cleanup_settings', {})
            if view['statuses'][0]['status'] == 'WINDOW_ACTIVE': break
            time.sleep(.05)
        self.assertEqual('WINDOW_ACTIVE', view['statuses'][0]['status'])
        self.assertEqual([], view['tickets'])
        self.private_call('set_idle_cleanup_settings', dict(saved, enabled=False))
        c.tool('release_resource', dict(key, generation=p['job']['generation'], cleanupConfirmed=True))
        self.assertNotIn(key['token'], (self.data/'logs/bug.jsonl').read_text(encoding='utf-8'))

    def test_bound_queue_offer_acceptance_is_independent_of_models(self):
        thread = 'queue-fixture-thread'
        home = self.data / 'queue-runtime'
        sessions = home / 'sessions'
        sessions.mkdir(parents=True)
        records = [{'type': 'session_meta', 'payload': {'id': thread}},
                   {'type': 'turn_context', 'timestamp': datetime.now(timezone.utc).isoformat(),
                    'payload': {'turn_id': 'queue-fixture-turn', 'model': 'fixture', 'effort': 'medium'}}]
        (sessions / ('rollout-' + thread + '.jsonl')).write_text(''.join(json.dumps(r) + '\n' for r in records), encoding='utf-8')
        self.private_call('set_runtime_source', {'home': str(home)})
        self.private_call('set_queue_resume_consent', {'threadId': thread, 'enabled': True})
        c = self.client()
        settings = c.tool('get_module_settings')
        self.private_call('set_module_settings', dict(settings, modelsEnabled=False))
        request = {'requestId': 'bound-offer', 'sessionId': 'alias-not-identity', 'resource': 'app:fixture',
                   'purpose': '只验证接单，不启动任何宿主', 'resumeThreadId': thread,
                   'sourceTurnId': 'queue-fixture-turn', 'remainingWork': '核对并释放许可'}
        permit = c.tool('request_resource', request)
        key = {'leaseId': permit['job']['id'], 'token': permit['token']}
        end = time.monotonic() + 5
        while time.monotonic() < end:
            offer = c.tool('get_resource', key)
            if offer['status'] == 'Offered': break
            time.sleep(0.03)
        self.assertEqual('Offered', offer['status'])
        self.assertIsNone(offer['expiresAt'])
        binding = c.tool('get_queue_binding', key)
        self.assertEqual(thread, binding['binding']['threadId'])
        self.assertNotIn(permit['token'], json.dumps(binding))
        accept = dict(key, offerId=offer['offerId'], generation=offer['job']['generation'])
        granted = c.tool('accept_resource', accept)
        self.assertEqual('Granted', granted['status'])
        self.assertEqual(granted['expiresAt'], c.tool('accept_resource', accept)['expiresAt'])
        self.assertEqual('Released', c.tool('release_resource', dict(key, generation=granted['job']['generation'], cleanupConfirmed=True))['status'])
        self.assertEqual(permit['job']['id'], c.tool('request_resource', request)['job']['id'])

    def test_three_clients_fifo_and_report_routing(self):
        clients = [self.client() for _ in range(3)]
        with concurrent.futures.ThreadPoolExecutor(3) as pool:
            jobs = list(pool.map(lambda x: self.submit(x[1], f'窗口{x[0]}'), enumerate(clients)))
        reports = [self.finish(c, j['id']) for c, j in zip(clients, jobs)]
        ordered = sorted(reports, key=lambda j: j['sequence'])
        self.assertTrue(all(j['state'] == 'Passed' for j in ordered))
        for a, b in zip(ordered, ordered[1:]):
            self.assertLessEqual(a['endedAt'], b['startedAt'])
        for index, c in enumerate(clients):
            report = c.tool('get_test_report', {'jobId': jobs[index]['id']})
            self.assertEqual(f'窗口{index}', report['job']['request']['sessionId'])
            self.assertTrue(report['simulationOnly'])
            self.assertEqual(64, len(report['suiteHash']))
        duplicate = self.submit(clients[0], '窗口0')
        self.assertEqual(jobs[0]['id'], duplicate['id'])
        (self.data / 'verified-reports.json').write_text(json.dumps(reports, ensure_ascii=False, indent=2), encoding='utf-8')

    def test_headless_parallel_timeout_failure_and_model_record(self):
        c = self.client()
        sw = self.submit(c, 'sw')
        headless = self.submit(c, 'headless', 'mock-headless')
        h = self.finish(c, headless['id'])
        s = self.finish(c, sw['id'])
        self.assertLess(h['startedAt'], s['endedAt'])
        self.assertEqual('Failed', self.finish(c, self.submit(c, 'fail', 'mock-fail')['id'])['state'])
        self.assertEqual('TimedOut', self.finish(c, self.submit(c, 'timeout', 'mock-timeout')['id'])['state'])
        d = c.tool('request_model_switch', {'requestId': 'model-1', 'sessionId': '窗口A', 'model': '用户选定模型',
                    'effort': 'high', 'reason': '进入审查', 'handoff': '检查并发问题'})
        self.assertEqual('UNSUPPORTED', d['status'])
        self.assertEqual(d, c.tool('get_model_decision', {'decisionId': d['id']}))

    def test_crash_and_cleanup_uncertainty_block_following_jobs(self):
        c = self.client()
        bad = self.submit(c, 'crash', 'mock-crash')
        self.assertEqual('RecoveryRequired', self.finish(c, bad['id'])['state'])
        following = self.submit(c, 'following')
        time.sleep(.3)
        self.assertEqual('Queued', c.tool('get_test_status', {'jobId': following['id']})['state'])
        result = c.tool('cancel_test', {'jobId': bad['id']}, allow_error=True)
        self.assertEqual('RECOVERY_REQUIRED', result['code'])

    def test_cancel_running_waits_for_exit_and_offline_is_explicit(self):
        c = self.client()
        a = self.submit(c, 'cancel')
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            status = c.tool('get_test_status', {'jobId': a['id']})
            if status.get('runnerPid'):
                break
            time.sleep(.01)
        else:
            self.fail('未观察到模拟执行进程')
        c.tool('cancel_test', {'jobId': a['id']})
        self.assertEqual('Cancelled', self.finish(c, a['id'])['state'])
        self.daemon.kill()
        self.daemon.wait()
        self.assertEqual('TEST_CENTER_OFFLINE', c.tool('get_health', allow_error=True)['code'])

    def test_restart_quarantines_old_lease(self):
        c = self.client()
        a = self.submit(c, 'restart')
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            if c.tool('get_test_status', {'jobId': a['id']})['state'] == 'Running':
                break
            time.sleep(.01)
        else:
            self.fail('未观察到执行状态')
        self.daemon.kill()
        self.daemon.wait()
        self.start_daemon()
        self.assertEqual('RecoveryRequired', c.tool('get_test_status', {'jobId': a['id']})['state'])
        b = self.submit(c, 'blocked')
        time.sleep(.2)
        self.assertEqual('Queued', c.tool('get_test_status', {'jobId': b['id']})['state'])

    def test_singleton_pause_and_protocol_validation(self):
        duplicate = subprocess.run(['dotnet', str(DAEMON), '--instance', self.instance, '--data', str(self.data / 'other')],
                                   capture_output=True, timeout=5)
        self.assertEqual(2, duplicate.returncode)
        c = self.client()
        self.assertEqual(28, len(c.rpc('tools/list')['result']['tools']))
        c.tool('set_queue_paused', {'paused': True})
        a = self.submit(c, 'paused')
        time.sleep(.15)
        self.assertEqual('Queued', c.tool('get_test_status', {'jobId': a['id']})['state'])
        bad = c.rpc('tools/call', {'name': 'wait_test', 'arguments': {'jobId': a['id'], 'maxWaitSeconds': 21}})
        self.assertEqual(-32602, bad['error']['code'])
        unknown = c.rpc('tools/call', {'name': 'submit_test', 'arguments': {'requestId': 'q', 'sessionId': 'q', 'suiteId': 'mock-sw', 'command': 'calc'}})
        self.assertEqual(-32602, unknown['error']['code'])
        c.tool('set_queue_paused', {'paused': False})
        self.assertEqual('Passed', self.finish(c, a['id'])['state'])

    def test_cleanup_failure_has_report_but_keeps_resources(self):
        c = self.client()
        a = self.submit(c, 'cleanup', 'mock-cleanup-fail')
        self.assertEqual('RecoveryRequired', self.finish(c, a['id'])['state'])
        report = c.tool('get_test_report', {'jobId': a['id']})
        self.assertEqual('UNCERTAIN', report['job']['cleanupStatus'])
        self.assertTrue(report['reportReady'])

    def test_supported_older_protocol_is_negotiated(self):
        # 使用新的未初始化进程，检查协议协商而非仅验证 JSON 可以解析。
        p = subprocess.Popen(['dotnet', str(BRIDGE), '--instance', self.instance], stdin=subprocess.PIPE,
                             stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding='utf-8')
        request = {'jsonrpc': '2.0', 'id': 'old-version', 'method': 'initialize',
                   'params': {'protocolVersion': '2024-11-05', 'capabilities': {}, 'clientInfo': {'name': 'test', 'version': '1'}}}
        out, _ = p.communicate(json.dumps(request) + '\n', timeout=5)
        self.assertEqual('2024-11-05', json.loads(out)['result']['protocolVersion'])

    def test_registered_cli_entry_drives_two_independent_clients(self):
        spec = importlib.util.spec_from_file_location('connection', ROOT / 'scripts/connection.py')
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        home = self.data / 'isolated-codex'
        installer = module.Installer(home, shutil.which('codex'), BRIDGE, self.data / 'backups', instance=self.instance)
        installer.connect()
        entry = json.loads(installer.cli_run(home, 'get', module.NAME, '--json'))['transport']
        clients = [Client(self.instance, [entry['command'], *entry['args']]) for _ in range(2)]
        self.clients.extend(clients)
        with concurrent.futures.ThreadPoolExecutor(2) as pool:
            jobs = list(pool.map(lambda pair: self.submit(pair[1], 'registered-' + str(pair[0])), enumerate(clients)))
        reports = [self.finish(c, j['id']) for c, j in zip(clients, jobs)]
        self.assertTrue(all(j['state'] == 'Passed' for j in reports))
        reports.sort(key=lambda j: j['sequence'])
        self.assertLessEqual(reports[0]['endedAt'], reports[1]['startedAt'])
        installer.disconnect()

    def test_native_readonly_without_host_reports_blocked(self):
        running = subprocess.run(['tasklist', '/FI', 'IMAGENAME eq SLDWORKS.exe', '/FO', 'CSV', '/NH'], capture_output=True, timeout=5)
        if b'SLDWORKS.EXE' in running.stdout.upper():
            self.skipTest('真实宿主正在运行，不自动接触 COM；成功路径留给人工验收')
        c = self.client()
        a = self.submit(c, 'native-no-host', 'sw-readonly')
        self.assertEqual('Blocked', self.finish(c, a['id'])['state'])
        report = c.tool('get_test_report', {'jobId': a['id']})
        self.assertFalse(report['simulationOnly'])
        self.assertEqual('OK', report['job']['cleanupStatus'])
        self.assertEqual('Passed', self.finish(c, self.submit(c, 'after-native-block')['id'])['state'])

    def test_native_environment_only_never_enters_com(self):
        worker = DAEMON.parent / 'solidworks/Runner.SolidWorks.dll'
        result = subprocess.run(['dotnet', str(worker), '--environment-only'], capture_output=True, timeout=5)
        self.assertEqual(0, result.returncode)
        value = json.loads(result.stdout)
        self.assertFalse(value['passed'])
        self.assertTrue(value['blocked'])
        self.assertTrue(value['cleanupOk'])

    def test_external_resource_permit_holds_queue_until_owner_cleanup(self):
        owner = self.client()
        other = self.client()
        permit = owner.tool('request_resource', {'requestId': 'external-a', 'sessionId': '窗口A', 'resource': 'solidworks', 'purpose': '临时测试使用许可', 'durationSeconds': 30})
        args = {'leaseId': permit['job']['id'], 'token': permit['token']}
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            permit = owner.tool('get_resource', args)
            if permit['status'] == 'Offered':
                permit = owner.tool('accept_resource', dict(args, offerId=permit['offerId'], generation=permit['job']['generation']))
                break
            time.sleep(.05)
        else: self.fail('资源许可未被授予')
        job = self.submit(other, 'waiting-for-owner')
        time.sleep(.2)
        self.assertEqual('Queued', other.tool('get_test_status', {'jobId': job['id']})['state'])
        invalid = other.tool('get_resource', dict(args, token='wrong-token'), allow_error=True)
        self.assertEqual('INVALID_PERMIT_TOKEN', invalid['code'])
        release = dict(args, generation=permit['job']['generation'], cleanupConfirmed=False)
        self.assertEqual('RecoveryRequired', owner.tool('release_resource', release)['status'])
        self.assertEqual('Queued', other.tool('get_test_status', {'jobId': job['id']})['state'])
        release['cleanupConfirmed'] = True
        self.assertEqual('Released', owner.tool('release_resource', release)['status'])
        self.assertEqual('Passed', self.finish(other, job['id'])['state'])
        report = owner.tool('get_test_report', {'jobId': permit['job']['id']})
        self.assertTrue(report['resourcePermit'])
        self.assertEqual('Released', report['job']['state'])

    def tearDown(self):
        for c in getattr(self, 'clients', []):
            c.close()
        if getattr(self, 'daemon', None) and self.daemon.poll() is None:
            self.daemon.kill()
            self.daemon.wait()
        if hasattr(self, 'log'):
            self.log.close()


if __name__ == '__main__':
    output = ROOT / 'artifacts/tests/integration.log'
    output.parent.mkdir(parents=True, exist_ok=True)
    class Tee:
        def __init__(self, file):
            self.file = file
        def write(self, text):
            sys.stderr.write(text)
            self.file.write(text)
        def flush(self):
            sys.stderr.flush()
            self.file.flush()
    with output.open('w', encoding='utf-8') as log:
        runner = unittest.TextTestRunner(stream=Tee(log), verbosity=2)
        unittest.main(testRunner=runner)
