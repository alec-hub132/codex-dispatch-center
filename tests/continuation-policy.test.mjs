import test from 'node:test';
import assert from 'node:assert/strict';
import { continueOnce } from '../scripts/continuation-policy.mjs';
import { inspectCycle } from '../scripts/continuation-policy.mjs';
const plan = { sourceTurnId: 'original', maxPolls: 3 };
function fixture(states) {
  const events=[]; let claimed=false; let index=0;
  return { events, claim:async()=> {if(claimed)return false;claimed=true;return true;},
    inspect:async()=>({authorized:true,modelsEnabled:true,latestTurnId:'original',...states[Math.min(index++,states.length-1)]}),
    preflight:async()=>events.push('preflight'),record:async s=>events.push(s),
    start:async()=>{events.push('start');return 'new-turn';},pause:async()=>events.push('wait') };
}
test('等待指定轮结束，登记后只启动一次，回执不等于模型核验',async()=>{
 const io=fixture([{finished:false},{finished:true}]);
 assert.equal(await continueOnce(plan,io),'STARTED_AWAITING_RUNTIME');
 assert.deepEqual(io.events,['wait','preflight','DISPATCHING','start','STARTED_AWAITING_RUNTIME']);
 assert.equal(await continueOnce(plan,io),'ALREADY_CLAIMED');
 assert.equal(io.events.filter(x=>x==='start').length,1);
});
for(const state of [{authorized:false},{modelsEnabled:false},{newUserInput:true},{latestTurnId:'another'}])
 test('用户或轮次变化阻止启动 '+JSON.stringify(state),async()=>{
  const io=fixture([{finished:true,...state}]);assert.equal(await continueOnce(plan,io),'STOPPED_NO_RETRY');assert.ok(!io.events.includes('start'));
 });
test('启动结果不明只发送一次，重新运行不重发',async()=>{
 const io=fixture([{finished:true}]);io.start=async()=>{io.events.push('start');throw Error('断线');};
 assert.equal(await continueOnce(plan,io),'STOPPED_NO_RETRY');assert.equal(await continueOnce(plan,io),'ALREADY_CLAIMED');
 assert.equal(io.events.filter(x=>x==='start').length,1);
});
test('超时和预检失败均不发送',async()=>{
 const io=fixture([{finished:false}]);assert.equal(await continueOnce(plan,io),'STOPPED_NO_RETRY');assert.ok(!io.events.includes('start'));
 const bad=fixture([{finished:true}]);bad.preflight=async()=>{throw Error('模型被用户改变');};
 assert.equal(await continueOnce(plan,bad),'STOPPED_NO_RETRY');assert.ok(!bad.events.includes('start'));
});
test('模型模块关闭仍允许已发送的主管返回续跑',async()=>{
 const io=fixture([{finished:true,modelsEnabled:false,returning:true}]);
 assert.equal(await continueOnce(plan,io),'STARTED_AWAITING_RUNTIME');
});
test('周期只接受本聊天本方向的已发送待核验状态',()=>{
 const p={threadId:'chat',cycleId:'cycle',cycleToken:'secret',expectedModel:'worker'};
 const d={id:'cycle',status:'SENT_AWAITING_VERIFICATION',execution:{threadId:'chat',token:'secret',model:'worker',returning:false}};
 assert.deepEqual(inspectCycle(p,d),{returning:false});
 for(const patch of [{status:'EXECUTING'},{status:'RETURN_VERIFIED'},{execution:{...d.execution,threadId:'foreign'}},{execution:{...d.execution,token:'wrong'}},{execution:{...d.execution,model:'other'}}])
  assert.throws(()=>inspectCycle(p,{...d,...patch}));
 const r={...d,status:'RETURN_SENT_AWAITING_VERIFICATION',execution:{...d.execution,returning:true}};
 assert.deepEqual(inspectCycle(p,r),{returning:true});
});
