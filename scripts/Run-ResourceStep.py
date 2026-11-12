"""在一段宿主测试结束并取得本次清理回执后立即释放原许可；不申请、不抢占。"""
import argparse,collections,json,os,pathlib,shutil,stat,subprocess,sys,threading,uuid
from datetime import datetime,timezone

def now():return datetime.now(timezone.utc).isoformat()

def confirm_cleanup(*,operations_stopped,owned_resources_cleaned):
    """由测试程序finally在实际停止/清理后调用；程序退出本身不是清理证明。"""
    value=dict(stepId=os.environ['CODEX_RESOURCE_STEP_ID'],leaseId=os.environ['CODEX_RESOURCE_STEP_LEASE'],
        generation=int(os.environ['CODEX_RESOURCE_STEP_GENERATION']),operationsStopped=operations_stopped is True,ownedResourcesCleaned=owned_resources_cleaned is True)
    with open(os.environ['CODEX_RESOURCE_STEP_CLEANUP'],'x',encoding='utf-8') as stream:json.dump(value,stream)

def cleanup_matches(path,step_id,lease,generation):
    try:
        info=path.lstat()
        if not stat.S_ISREG(info.st_mode) or path.is_symlink() or info.st_size>16384:return False
        value=json.loads(path.read_text(encoding='utf-8'))
        return (value.get('stepId')==step_id and value.get('leaseId')==lease and type(value.get('generation')) is int and
            value['generation']==generation and value.get('operationsStopped') is True and value.get('ownedResourcesCleaned') is True)
    except (OSError,ValueError,TypeError,AttributeError):return False

def run_step(call,key,command,output,timeout,cwd=None):
    step_id=uuid.uuid4().hex;folder=pathlib.Path(output)/step_id;folder.mkdir(parents=True,exist_ok=False)
    cleanup=folder/'cleanup.json';secret=key['token'];auth=dict(leaseId=key['leaseId'],token=secret)
    result=dict(stepId=step_id,leaseId=key['leaseId'],startedAt=now(),commandStarted=False,testPassed=False,
        cleanupConfirmed=False,resourceStatus='UNKNOWN',exitCode=None,errorCode=None)
    def event(code):
        with (folder/'bug.jsonl').open('a',encoding='utf-8') as f:f.write(json.dumps(dict(at=now(),code=code,stepId=step_id,leaseId=key['leaseId']))+'\n')
    owned=False;generation=None;timed_out=False;pipe_closed=True;process=None;reader=None;tail=collections.deque(maxlen=16)
    try:
        modules=call('get_module_settings',{});current=call('get_resource',auth);result['resourceStatus']=current['status'];generation=current['job']['generation']
        if current['status']!='Granted':result['errorCode']='LEASE_NOT_GRANTED';return result
        if key.get('generation',generation)!=generation:result['errorCode']='STALE_GENERATION';return result
        if not modules['queueEnabled']:result['errorCode']='QUEUE_DISABLED';return result
        if not command or not 0<timeout<=1800:result['errorCode']='INVALID_COMMAND_OR_TIMEOUT';return result
        if current.get('expiresAt'):
            remaining=(datetime.fromisoformat(current['expiresAt'].replace('Z','+00:00'))-datetime.now(timezone.utc)).total_seconds()
            if remaining<timeout+3:result['errorCode']='INSUFFICIENT_LEASE_TIME';return result
        owned=True;result['generation']=generation;event('RESOURCE_STEP_STARTED')
        env=os.environ.copy();env.update(CODEX_RESOURCE_STEP_ID=step_id,CODEX_RESOURCE_STEP_LEASE=key['leaseId'],
            CODEX_RESOURCE_STEP_GENERATION=str(generation),CODEX_RESOURCE_STEP_CLEANUP=str(cleanup.resolve()),CODEX_RESOURCE_STEP_HELPER=str(pathlib.Path(__file__).resolve()))
        process=subprocess.Popen(command,cwd=cwd,env=env,stdin=subprocess.DEVNULL,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0)
        result.update(commandStarted=True,testPid=process.pid)
        def drain():
            try:
                while True:
                    chunk=process.stdout.read(4096)
                    if not chunk:break
                    tail.append(chunk)
            except (OSError,ValueError):pass
        reader=threading.Thread(target=drain,daemon=True);reader.start()
        try:result['exitCode']=process.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            timed_out=True;result['errorCode']='STEP_TIMEOUT';event('RESOURCE_STEP_TIMEOUT')
            # 只停止本次直接启动的测试进程；不遍历或强关宿主，后续仍保留隔离。
            process.kill();result['exitCode']=process.wait(timeout=3)
        reader.join(2);pipe_closed=not reader.is_alive()
        result['processEndedAt']=now();result['testPassed']=result['exitCode']==0 and not timed_out
        result['cleanupConfirmed']=not timed_out and pipe_closed and cleanup_matches(cleanup,step_id,key['leaseId'],generation)
        if not result['cleanupConfirmed'] and not result['errorCode']:result['errorCode']='CLEANUP_UNCONFIRMED'
        event('RESOURCE_STEP_CLEANUP_CONFIRMED' if result['cleanupConfirmed'] else 'RESOURCE_STEP_CLEANUP_UNCONFIRMED')
    except Exception:
        result['errorCode']=result['errorCode'] or 'STEP_EXECUTION_FAILED';result['cleanupConfirmed']=False;event('RESOURCE_STEP_FAILED')
    finally:
        if process is not None and process.poll() is None:
            try:process.kill();process.wait(timeout=3)
            except (OSError,subprocess.TimeoutExpired):pass
            result['cleanupConfirmed']=False
        if reader is not None and reader.is_alive():reader.join(.1)
        if process is not None and process.stdout is not None and (reader is None or not reader.is_alive()):process.stdout.close()
        if owned:
            try:
                current=call('get_resource',auth)
                if current['job']['generation']!=generation:raise RuntimeError('代次改变，禁止释放其他操作段')
                if current['status'] in ('Granted','RecoveryRequired'):
                    call('release_resource',dict(auth,generation=generation,cleanupConfirmed=result['cleanupConfirmed']))
                final=call('get_resource',auth);result.update(resourceStatus=final['status'],cleanupStatus=final['job']['cleanupStatus'],releasedAt=now() if final['status']=='Released' else None)
                event('RESOURCE_STEP_RELEASED' if final['status']=='Released' else 'RESOURCE_STEP_ISOLATED')
            except Exception:result['errorCode']='RELEASE_UNCONFIRMED';event('RESOURCE_STEP_RELEASE_UNCONFIRMED')
        text=b''.join(tail).decode('utf-8',errors='replace').replace(secret,'[已隐藏令牌]')
        (folder/'test-tail.log').write_text(text,encoding='utf-8')
        result['finishedAt']=now();result['reportFolder']=str(folder)
        (folder/'result.json').write_text(json.dumps(result,ensure_ascii=False,indent=2).replace(secret,'[已隐藏令牌]'),encoding='utf-8')
    return result

class McpCalls:
    def __init__(self,bridge,instance):self.bridge=bridge;self.instance=instance
    def __call__(self,name,args):
        messages=[dict(jsonrpc='2.0',id=1,method='initialize',params=dict(protocolVersion='2025-06-18')),
            dict(jsonrpc='2.0',id=2,method='tools/call',params=dict(name=name,arguments=args))]
        p=subprocess.run([shutil.which('dotnet') or 'dotnet',str(self.bridge),'--instance',self.instance,'--module','queue'],
            input=''.join(json.dumps(x)+'\n' for x in messages),text=True,encoding='utf-8',capture_output=True,timeout=32,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0)
        replies={v['id']:v for v in map(json.loads,p.stdout.splitlines())};reply=replies[2]
        if p.returncode or 'error' in reply or reply['result'].get('isError'):raise RuntimeError('队列接口未确认成功')
        return json.loads(reply['result']['content'][0]['text'])

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--permit',required=True,type=pathlib.Path,help='本机原许可私有计划路径，不传明文令牌')
    parser.add_argument('--output',required=True,type=pathlib.Path)
    parser.add_argument('--timeout',type=float,default=120)
    parser.add_argument('--cwd',type=pathlib.Path)
    parser.add_argument('--instance',default='default')
    parser.add_argument('--bridge',type=pathlib.Path)
    parser.add_argument('command',nargs=argparse.REMAINDER)
    args=parser.parse_args();command=args.command[1:] if args.command[:1]==['--'] else args.command
    if not command:parser.error('请在--之后提供仅含本段测试/清理的命令，编译修复放在执行器之外')
    base=pathlib.Path(__file__).resolve().parents[1]
    bridge=args.bridge or (base/'mcp/Dispatch.McpBridge.dll' if (base/'mcp/Dispatch.McpBridge.dll').exists() else base/'src/Dispatch.McpBridge/bin/Release/net8.0/Dispatch.McpBridge.dll')
    try:
        key=json.loads(args.permit.read_text(encoding='utf-8-sig'));result=run_step(McpCalls(bridge,args.instance),key,command,args.output,args.timeout,args.cwd)
        print(json.dumps(result,ensure_ascii=False));return 0 if result['testPassed'] and result['cleanupConfirmed'] and result['resourceStatus']=='Released' and result.get('cleanupStatus')=='OK' else 1
    except Exception:
        print(json.dumps(dict(errorCode='STEP_SETUP_FAILED',message='执行器启动未确认，请核对本机计划/路径，保留原许可清理责任。'),ensure_ascii=False));return 1

if __name__=='__main__':sys.exit(main())
