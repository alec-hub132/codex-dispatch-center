"""只读检查模型目录与既有后台入口；不创建会话、发送提示词或修改模型。"""
import argparse
import importlib.util
import json
import os
import pathlib
import queue
import subprocess
import threading
from datetime import datetime, timezone

ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('connection', ROOT / 'scripts/connection.py')
connection = importlib.util.module_from_spec(spec)
spec.loader.exec_module(connection)


def inspect(cli, codex_home, proxy):
    flags = subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0
    env = dict(os.environ, CODEX_HOME=str(codex_home))
    command = [cli, 'app-server', 'proxy'] if proxy else [cli, 'app-server', '--stdio']
    process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                               stderr=subprocess.PIPE, text=True, encoding='utf-8',
                               env=env, creationflags=flags)
    replies, errors = queue.Queue(), []
    def read_stdout():
        for line in process.stdout:
            try:
                replies.put(json.loads(line))
            except ValueError:
                replies.put({'invalidResponse': True})
        replies.put({'closed': True})
    # 后台stderr可能含账户/鉴权细节，不存正文，仅计数避免堵塞管道。
    def drain_stderr():
        for _ in process.stderr:
            errors.append(True)
    output_thread = threading.Thread(target=read_stdout, daemon=True)
    error_thread = threading.Thread(target=drain_stderr, daemon=True)
    output_thread.start(); error_thread.start()
    def call(identifier, method, params):
        if method not in ('initialize', 'model/list', 'thread/loaded/list'):
            raise RuntimeError('仅允许只读能力检查')
        process.stdin.write(json.dumps({'id': identifier, 'method': method, 'params': params}) + '\n')
        process.stdin.flush()
        while True:
            result = replies.get(timeout=12)
            if result.get('closed') or result.get('invalidResponse'):
                raise RuntimeError('后台连接已关闭或返回非协议内容')
            if result.get('id') == identifier:
                if 'error' in result:
                    raise RuntimeError('后台拒绝只读请求：' + str(result['error'].get('code')))
                return result['result']
    try:
        call(1, 'initialize', {'clientInfo': {'name': 'codex_dispatch_capability_check', 'version': '0.6'},
                              'capabilities': {'experimentalApi': True}})
        process.stdin.write(json.dumps({'method': 'initialized'}) + '\n'); process.stdin.flush()
        loaded = call(2, 'thread/loaded/list', {})
        models, cursor = [], None
        for page in range(10):
            params = {'limit': 100, 'includeHidden': False}
            if cursor:
                params['cursor'] = cursor
            result = call(3 + page, 'model/list', params)
            models.extend({'model': m['model'], 'displayName': m['displayName'],
                           'efforts': [v['reasoningEffort'] for v in m['supportedReasoningEfforts']]}
                          for m in result.get('data', []))
            cursor = result.get('nextCursor')
            if not cursor:
                break
        return {'reachable': True, 'loadedThreadCount': len(loaded.get('data', [])),
                'models': models, 'catalogComplete': not bool(cursor),
                'scope': '既有后台入口' if proxy else '独立检查进程，不是桌面窗口后台'}
    except (OSError, RuntimeError, queue.Empty) as error:
        return {'reachable': False, 'errorType': type(error).__name__, 'scope': '既有后台入口' if proxy else '独立检查进程'}
    finally:
        process.stdin.close()
        try:
            process.wait(timeout=3)
        except subprocess.TimeoutExpired:
            process.kill(); process.wait(timeout=3)  # 仅终止本脚本自己创建的检查进程。
        output_thread.join(timeout=1); error_thread.join(timeout=1)
        process.stdout.close(); process.stderr.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--home', type=pathlib.Path, required=True)
    parser.add_argument('--output', type=pathlib.Path, required=True)
    args = parser.parse_args()
    cli = connection.discover_cli()
    result = {'checkedAt': datetime.now(timezone.utc).isoformat(), 'readOnly': True,
              'sessionCreated': False, 'modelChanged': False,
              'existingServer': inspect(cli, args.home, True),
              'standaloneCatalog': inspect(cli, args.home, False)}
    # 能读取独立进程的目录不能证明已绑定桌面会话，实际切换能力保持关闭。
    result['existingWindowSwitchVerified'] = False
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(result, ensure_ascii=False))


if __name__ == '__main__':
    main()
