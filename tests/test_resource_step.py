import importlib.util,json,pathlib,sys,tempfile,unittest
ROOT=pathlib.Path(__file__).resolve().parents[1]
sys.stdout.reconfigure(encoding='utf-8')
spec=importlib.util.spec_from_file_location('resource_step',ROOT/'scripts/Run-ResourceStep.py')
step=importlib.util.module_from_spec(spec);spec.loader.exec_module(step)

class FakeService:
    def __init__(self):self.state='Granted';self.releases=[]
    def __call__(self,method,args):
        if method=='get_module_settings':return dict(queueEnabled=True)
        if method=='get_resource':return dict(status=self.state,job=dict(id='lease-a',generation=1,cleanupStatus='OK' if self.state=='Released' else 'UNCERTAIN'))
        if method=='release_resource':
            self.releases.append(args);self.state='Released' if args['cleanupConfirmed'] else 'RecoveryRequired'
            return self('get_resource',args)
        raise AssertionError('执行器不能创建申请或调用其他接口')

class ResourceStepTests(unittest.TestCase):
    def run_case(self,body,timeout=3):
        folder=pathlib.Path(tempfile.mkdtemp(prefix='dispatch-resource-step-'));service=FakeService()
        result=step.run_step(service,dict(leaseId='lease-a',token='PRIVATE_TEST_TOKEN',generation=1),[sys.executable,'-c',body],folder,timeout)
        self.assertNotIn('PRIVATE_TEST_TOKEN',json.dumps(result))
        for p in folder.rglob('*.json'):self.assertNotIn('PRIVATE_TEST_TOKEN',p.read_text(encoding='utf-8'))
        return service,result,folder
    def test_success_releases_before_caller_continues(self):
        service,result,_=self.run_case("import os,json,pathlib;pathlib.Path(os.environ['CODEX_RESOURCE_STEP_CLEANUP']).write_text(json.dumps(dict(stepId=os.environ['CODEX_RESOURCE_STEP_ID'],leaseId=os.environ['CODEX_RESOURCE_STEP_LEASE'],generation=int(os.environ['CODEX_RESOURCE_STEP_GENERATION']),operationsStopped=True,ownedResourcesCleaned=True)))")
        self.assertEqual('Released',result['resourceStatus']);self.assertTrue(result['cleanupConfirmed']);self.assertEqual(1,len(service.releases));self.assertEqual(0,result['exitCode'])
    def test_test_failure_with_valid_cleanup_still_releases(self):
        service,result,_=self.run_case("import os,json,pathlib,sys;pathlib.Path(os.environ['CODEX_RESOURCE_STEP_CLEANUP']).write_text(json.dumps(dict(stepId=os.environ['CODEX_RESOURCE_STEP_ID'],leaseId=os.environ['CODEX_RESOURCE_STEP_LEASE'],generation=int(os.environ['CODEX_RESOURCE_STEP_GENERATION']),operationsStopped=True,ownedResourcesCleaned=True)));sys.exit(7)")
        self.assertEqual('Released',result['resourceStatus']);self.assertEqual(7,result['exitCode']);self.assertFalse(result['testPassed']);self.assertTrue(service.releases[0]['cleanupConfirmed'])
    def test_exit_alone_is_not_cleanup_proof(self):
        service,result,_=self.run_case('pass')
        self.assertEqual('RecoveryRequired',result['resourceStatus']);self.assertFalse(service.releases[0]['cleanupConfirmed'])
    def test_wrong_or_old_step_receipt_never_unlocks(self):
        service,result,_=self.run_case("import os,json,pathlib;pathlib.Path(os.environ['CODEX_RESOURCE_STEP_CLEANUP']).write_text(json.dumps(dict(stepId='old-step',leaseId='lease-a',generation=1,operationsStopped=True,ownedResourcesCleaned=True)))")
        self.assertEqual('RecoveryRequired',result['resourceStatus']);self.assertFalse(result['cleanupConfirmed'])
    def test_timeout_never_unlocks(self):
        service,result,_=self.run_case('import time;time.sleep(10)',.15)
        self.assertEqual('RecoveryRequired',result['resourceStatus']);self.assertEqual('STEP_TIMEOUT',result['errorCode']);self.assertFalse(result['cleanupConfirmed'])
    def test_released_original_is_not_executed_again(self):
        folder=pathlib.Path(tempfile.mkdtemp(prefix='dispatch-resource-step-'));service=FakeService();service.state='Released'
        result=step.run_step(service,dict(leaseId='lease-a',token='PRIVATE_TEST_TOKEN',generation=1),[sys.executable,'-c',"raise RuntimeError('不能执行')"],folder,3)
        self.assertFalse(result['commandStarted']);self.assertFalse(service.releases)
    def test_wrong_generation_does_not_execute_or_release(self):
        folder=pathlib.Path(tempfile.mkdtemp(prefix='dispatch-resource-step-'));service=FakeService()
        result=step.run_step(service,dict(leaseId='lease-a',token='PRIVATE_TEST_TOKEN',generation=99),[sys.executable,'-c','pass'],folder,3)
        self.assertFalse(result['commandStarted']);self.assertFalse(service.releases)
    def test_test_output_redacts_original_token(self):
        _,_,folder=self.run_case("print('PRIVATE_TEST_TOKEN')")
        for path in folder.rglob('*.log'):self.assertNotIn('PRIVATE_TEST_TOKEN',path.read_text(encoding='utf-8'))

if __name__=='__main__':unittest.main(verbosity=2)
