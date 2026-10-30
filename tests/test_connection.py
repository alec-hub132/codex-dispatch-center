import importlib.util
import json
import pathlib
import shutil
import tempfile
import unittest
import sys
import uuid
from unittest.mock import patch

sys.stdout.reconfigure(encoding='utf-8')
sys.stderr.reconfigure(encoding='utf-8')
ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('connection', ROOT / 'scripts/connection.py')
connection = importlib.util.module_from_spec(spec)
spec.loader.exec_module(connection)


class ConnectionTests(unittest.TestCase):
    def setUp(self):
        self.root = pathlib.Path(tempfile.mkdtemp(prefix='connection-', dir=ROOT / 'artifacts'))
        self.home = self.root / '中文配置目录'
        self.home.mkdir()
        self.cli = shutil.which('codex')
        self.assertTrue(self.cli, '需要本机真实 Codex CLI')
        self.bridge = ROOT / 'src/Dispatch.McpBridge/bin/Release/net8.0/Dispatch.McpBridge.dll'
        self.installer = connection.Installer(self.home, self.cli, self.bridge, self.root / 'backups')

    def test_directory_resolution_and_conflict(self):
        self.assertEqual(self.home.resolve(), connection.resolve_home(None, None, str(self.home)))
        with self.assertRaises(connection.ConnectionError):
            connection.resolve_home(None, str(self.home), str(self.root / 'other'))
        self.assertEqual(self.home.resolve(), connection.resolve_home(str(self.home), 'x', 'y'))

    def test_two_module_registrations_are_independent_and_preserve_other_settings(self):
        config = self.home / 'config.toml'
        original = b'model="user-choice"\n[mcp_servers.unrelated]\ncommand="unrelated.exe"\n'
        config.write_bytes(original)
        queue_installer = connection.Installer(self.home, self.cli, self.bridge, self.root / 'backups', module='queue')
        models_installer = connection.Installer(self.home, self.cli, self.bridge, self.root / 'backups', module='models')
        queue_installer.connect(); models_installer.connect()
        registered = connection.parse(config.read_bytes())['mcp_servers']
        self.assertEqual('queue', registered['codex-queue']['args'][-1])
        self.assertEqual('models', registered['codex-model-dispatch']['args'][-1])
        self.assertTrue(queue_installer.inspect()['mcpRegistered'])
        self.assertTrue(models_installer.inspect()['mcpRegistered'])
        queue_installer.disconnect()
        self.assertTrue(models_installer.inspect()['rulesInstalled'])
        self.assertTrue(models_installer.inspect()['mcpRegistered'])
        self.assertNotIn('codex-queue', connection.parse(config.read_bytes())['mcp_servers'])
        models_installer.disconnect()
        self.assertEqual(connection.parse(original), connection.parse(config.read_bytes()))

    def test_split_replaces_only_owned_legacy_registration_after_new_entries_exist(self):
        original = b'model="keep-model"\n[mcp_servers.other]\ncommand="other.exe"\n'
        (self.home / 'config.toml').write_bytes(original)
        with patch.object(connection, 'RULE', connection.BEGIN + '\nold legacy rules\n' + connection.END):
            self.installer.connect()
        result = connection.connect_modules(self.installer)
        self.assertTrue(result['mcpRegistered'])
        self.assertTrue(result['rulesInstalled'])
        self.assertTrue(all(item['rulesInstalled'] for item in result['modules']))
        self.assertFalse(result['legacyRegistered'])
        servers = connection.parse((self.home / 'config.toml').read_bytes())['mcp_servers']
        self.assertEqual({'other', 'codex-queue', 'codex-model-dispatch'}, set(servers))
        connection.disconnect_modules(self.installer)
        self.assertEqual(connection.parse(original), connection.parse((self.home / 'config.toml').read_bytes()))

    def test_explorer_environment_finds_installed_codex_without_path_entry(self):
        with patch.object(connection.shutil, 'which', return_value=None):
            resolved = connection.discover_cli()
        self.assertTrue(pathlib.Path(resolved).is_file())
        self.assertEqual('codex.exe', pathlib.Path(resolved).name.lower())

    def test_reserved_node_repl_args_survive_cli_registration(self):
        config = self.home / 'config.toml'
        original = b'[mcp_servers.node_repl]\ncommand="node.exe"\nargs=[]\n'
        config.write_bytes(original)
        self.installer.connect()
        self.assertEqual([], connection.parse(config.read_bytes())['mcp_servers']['node_repl']['args'])
        self.installer.disconnect()
        self.assertEqual(original, config.read_bytes())

    def test_managed_rule_upgrade_preserves_user_text_and_disconnect_restore(self):
        rules = self.home / 'AGENTS.md'
        rules.write_bytes(b'original user text\r\n')
        with patch.object(connection, 'RULE', connection.BEGIN + '\nold rules\n' + connection.END):
            self.installer.connect()
        with rules.open('a', encoding='utf-8') as output:
            output.write('\nuser later addition\n')
        config = (self.home / 'config.toml').read_bytes()
        self.installer.upgrade()
        self.assertIn('request_resource', rules.read_text(encoding='utf-8'))
        self.assertIn('user later addition', rules.read_text(encoding='utf-8'))
        self.assertEqual(config, (self.home / 'config.toml').read_bytes())
        self.installer.disconnect()
        self.assertNotIn(connection.BEGIN, rules.read_text(encoding='utf-8'))
        self.assertIn('original user text', rules.read_text(encoding='utf-8'))

    def test_real_cli_connect_preserves_other_settings_and_disconnect(self):
        config = self.home / 'config.toml'
        original = '# 原有设置\nmodel = "original"\n[mcp_servers.other]\ncommand = "existing.exe"\n'
        config.write_text(original, encoding='utf-8')
        rules = self.home / 'AGENTS.md'
        rules.write_bytes(b'original rules\r\n')
        result = self.installer.connect()
        self.assertEqual('CLIENT_RELOAD_REQUIRED', result['status'])
        self.assertEqual('original', connection.parse(config.read_bytes())['model'])
        self.assertEqual('existing.exe', connection.parse(config.read_bytes())['mcp_servers']['other']['command'])
        self.assertTrue(self.installer.inspect()['mcpRegistered'])
        self.assertIn(connection.BEGIN, rules.read_text(encoding='utf-8'))
        self.installer.disconnect()
        self.assertNotIn(connection.NAME, connection.parse(config.read_bytes()).get('mcp_servers', {}))
        self.assertEqual(b'original rules\r\n', rules.read_bytes())

    def test_repeat_connect_is_idempotent(self):
        self.installer.connect()
        before = (self.home / 'AGENTS.md').read_bytes()
        self.installer.connect()
        self.assertEqual(before, (self.home / 'AGENTS.md').read_bytes())
        self.installer.disconnect()
        self.assertFalse((self.home / 'AGENTS.md').exists())

    def test_foreign_registration_never_overwritten(self):
        config = self.home / 'config.toml'
        original = b'[mcp_servers.codex-dispatch]\ncommand="foreign.exe"\n'
        config.write_bytes(original)
        with self.assertRaises(connection.ConnectionError):
            self.installer.connect()
        self.assertEqual(original, config.read_bytes())

    def test_empty_foreign_table_is_not_silently_replaced(self):
        config = self.home / 'config.toml'
        original = b'[mcp_servers.codex-dispatch]\n'
        config.write_bytes(original)
        with self.assertRaises(connection.ConnectionError):
            self.installer.connect()
        self.assertEqual(original, config.read_bytes())

    def test_override_file_and_later_user_edits_are_preserved(self):
        rules = self.home / 'AGENTS.override.md'
        rules.write_text('override original\n', encoding='utf-8')
        self.installer.connect()
        with rules.open('a', encoding='utf-8') as f:
            f.write('\nuser later edit\n')
        config = self.home / 'config.toml'
        with config.open('a', encoding='utf-8') as f:
            f.write('\n[extra]\nvalue=42\n')
        self.installer.disconnect()
        self.assertIn('user later edit', rules.read_text(encoding='utf-8'))
        self.assertEqual(42, connection.parse(config.read_bytes())['extra']['value'])

    def test_partial_failure_rolls_back_only_own_changes(self):
        config = self.home / 'config.toml'
        original = b'model="original"\n'
        config.write_bytes(original)
        def fail(stage):
            if stage == 'config-written':
                raise OSError('模拟断电前的写入错误')
        with self.assertRaises(OSError):
            self.installer.connect(checkpoint=fail)
        self.assertEqual(original, config.read_bytes())
        self.assertFalse((self.home / 'AGENTS.md').exists())

    def test_concurrent_change_blocks_rollback_overwrite(self):
        config = self.home / 'config.toml'
        def fail(stage):
            if stage == 'config-written':
                with config.open('a') as f:
                    f.write('\n[other_window]\nvalue=1\n')
                raise OSError('模拟其他窗口修改')
        with self.assertRaises(OSError):
            self.installer.connect(checkpoint=fail)
        self.assertEqual(1, connection.parse(config.read_bytes())['other_window']['value'])
        self.assertEqual('RECOVERY_REQUIRED', json.loads(self.installer.receipt.read_text(encoding='utf-8'))['state'])

    def test_recover_after_interrupted_install_preserves_user_change(self):
        config = self.home / 'config.toml'
        def fail(stage):
            if stage == 'config-written':
                with config.open('a') as f:
                    f.write('\n[changed]\nvalue=9\n')
                raise OSError('写入中断')
        with self.assertRaises(OSError):
            self.installer.connect(checkpoint=fail)
        self.assertEqual('RECOVERY_REQUIRED', self.installer.recover()['status'])
        self.assertEqual(9, connection.parse(config.read_bytes())['changed']['value'])

    def test_probe_detects_bridge_but_reports_offline_service(self):
        result = self.installer.probe(instance='offline-test-' + uuid.uuid4().hex)
        self.assertTrue(result['bridgeReachable'])
        self.assertFalse(result['daemonOnline'])
        self.assertEqual(28, result['toolCount'])

    def test_invalid_config_and_malformed_markers_block_without_write(self):
        config = self.home / 'config.toml'
        config.write_bytes(b'[broken')
        with self.assertRaises(connection.ConnectionError):
            self.installer.connect()
        self.assertEqual(b'[broken', config.read_bytes())
        config.write_bytes(b'')
        (self.home / 'AGENTS.md').write_text(connection.BEGIN, encoding='utf-8')
        with self.assertRaises(connection.ConnectionError):
            self.installer.connect()
        self.assertEqual(b'', config.read_bytes())


if __name__ == '__main__':
    output = ROOT / 'artifacts/tests/connection.log'
    output.parent.mkdir(parents=True, exist_ok=True)
    class Tee:
        def __init__(self, file): self.file = file
        def write(self, text):
            sys.stderr.write(text)
            self.file.write(text)
        def flush(self):
            sys.stderr.flush()
            self.file.flush()
    with output.open('w', encoding='utf-8') as log:
        unittest.main(testRunner=unittest.TextTestRunner(stream=Tee(log), verbosity=2))
