import test from 'node:test';
import assert from 'node:assert/strict';
import { sendQueueWakeOnce } from '../scripts/queue-wake-policy.mjs';
import { deliveryRoute } from '../scripts/queue-wake-policy.mjs';
const plan={leaseId:'lease-a',wakeId:'lease-a-1',threadId:'thread-a',sourceTurnId:'turn-a',prompt:'仅接续原任务',token:'not-for-logs'};
test('清理与接单分别核验和落回执，拒绝未知发送类型',()=>{
 assert.deepEqual(deliveryRoute({kind:'idle-cleanup'}),{method:'begin_idle_cleanup_send',journalPrefix:'idle-cleanup-'});
 assert.deepEqual(deliveryRoute({}),{method:'begin_queue_wake_send',journalPrefix:'queue-wake-'});
 assert.throws(()=>deliveryRoute({kind:'other'}));
});
function fixture(){
 let claimed=false;const events=[];
 return {events,claim:async()=>{if(claimed)return false;claimed=true;return true;},preflight:async()=>events.push('owner'),
   beginSend:async()=>{events.push('gate');return true;},start:async()=>{events.push('start');return 'turn-b';},record:async(state,turn)=>events.push({state,turn})};
}
test('独立排队一次发送，不要求模型开关，不改变模型设置',async()=>{
 const io=fixture();assert.equal(await sendQueueWakeOnce(plan,io),'SENT');assert.equal(await sendQueueWakeOnce(plan,io),'ALREADY_CLAIMED');
 assert.deepEqual(io.events,['owner','gate','start',{state:'SENT',turn:'turn-b'}]);assert.ok(!JSON.stringify(io.events).includes(plan.token));
});
test('发送前授权/资源状态不再有效时不启动',async()=>{
 const io=fixture();io.beginSend=async()=>false;assert.equal(await sendQueueWakeOnce(plan,io),'STOPPED_NO_RETRY');assert.ok(!io.events.includes('start'));
});
test('拥有者不可达、启动超时或无效回执不重发',async()=>{
 for(const mode of ['owner','start','same-turn','empty']){
  const io=fixture();if(mode==='owner')io.preflight=async()=>{throw Error('不可达');};
  if(mode==='start')io.start=async()=>{io.events.push('start');throw Error('结果不明');};
  if(mode==='same-turn')io.start=async()=>plan.sourceTurnId;if(mode==='empty')io.start=async()=>null;
  assert.equal(await sendQueueWakeOnce(plan,io),'STOPPED_NO_RETRY');assert.equal(await sendQueueWakeOnce(plan,io),'ALREADY_CLAIMED');
  assert.ok(io.events.filter(e=>e==='start').length<=1);
 }
});
test('非法跨聊天参数先拒绝，不建立发送登记',async()=>{
 for(const p of [{...plan,threadId:'../another'},{...plan,prompt:''},{...plan,sourceTurnId:'x\n'}]){
  const io=fixture();await assert.rejects(sendQueueWakeOnce(p,io));assert.deepEqual(io.events,[]);
 }
});
