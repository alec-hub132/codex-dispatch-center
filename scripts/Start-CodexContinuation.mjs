// 研究候选：限定已授权聊天，等待已知轮结束，再启动一次只读续跑。
import net from 'node:net';
import { createHash, randomUUID } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { readFile, writeFile, open, realpath, appendFile } from 'node:fs/promises';
import path from 'node:path';
import { continueOnce, inspectCycle } from './continuation-policy.mjs';

const planFile=process.argv[2];
if(process.platform!=='win32'||!planFile)throw Error('仅支持Windows，需指定续跑计划');
const plan=JSON.parse(await readFile(planFile,'utf8'));
for(const key of ['threadId','sourceTurnId']) if(!/^[a-zA-Z0-9-]{1,128}$/.test(plan[key]??''))throw Error('聊天或轮次编号无效');
if(typeof plan.prompt!=='string'||plan.prompt.length>16000||!plan.prompt.trim())throw Error('续跑任务无效');
if(!Number.isInteger(plan.maxPolls)||plan.maxPolls<1||plan.maxPolls>1200)throw Error('等待次数超限');
if(!Number.isFinite(Date.parse(plan.armedAfter)))throw Error('续跑登记时间无效');
if(plan.cycleId && (!/^[a-zA-Z0-9-]{1,128}$/.test(plan.cycleId)||typeof plan.cycleToken!=='string'))throw Error('模型周期参数无效');
const home=await realpath(plan.home),data=await realpath(plan.dataRoot);
const source=await realpath(plan.sourceFile),sessions=await realpath(path.join(home,'sessions'));
if(!source.toLowerCase().startsWith((sessions+path.sep).toLowerCase())||!source.endsWith('-'+plan.threadId+'.jsonl'))throw Error('运行记录不属于目标聊天');
const journal=path.join(data,'diagnostics',`continuation-${plan.threadId}-${plan.sourceTurnId}.json`);
const log=path.join(data,'logs','bug.jsonl');
const sid=execFileSync('whoami',['/user','/fo','csv','/nh'],{encoding:'utf8',windowsHide:true}).match(/S-1-\d+(?:-\d+)+/)?.[0];
if(!sid)throw Error('无法确定当前Windows用户');
if(!/^[a-zA-Z0-9_-]{1,80}$/.test(plan.instance??'default'))throw Error('实例名无效');
const pipeName='\\\\.\\pipe\\CodexDispatch-'+createHash('sha256').update(sid).digest('hex').toUpperCase().slice(0,20)+'-'+(plan.instance??'default');
let status='ARMED';
async function record(next,extra={}) {
 status=next;
 // 日志只含关联编号和状态，不记录聊天正文、令牌或响应全文。
 await writeFile(journal,JSON.stringify({threadId:plan.threadId,sourceTurnId:plan.sourceTurnId,status,at:new Date().toISOString(),...extra},null,2));
 await appendFile(log,JSON.stringify({time:new Date().toISOString(),code:'MODEL_CONTINUATION_'+next,message:extra.message??next,jobId:plan.sourceTurnId,error:null})+'\n');
}
async function service(method='get_health',args={}){
 if(!['get_health','get_model_decision','get_supervisor_settings'].includes(method))throw Error('续跑器禁止调用其他服务方法');
 return new Promise((resolve,reject)=>{
  const socket=net.createConnection(pipeName);let buffer='';const timer=setTimeout(()=>{socket.destroy();reject(Error('调度服务不可用'));},2500);
  socket.on('error',reject);socket.on('close',()=>clearTimeout(timer));
  socket.on('connect',()=>socket.write(JSON.stringify({version:1,requestId:randomUUID(),method,arguments:args})+'\n'));
  socket.on('data',chunk=>{buffer+=chunk.toString('utf8');if(buffer.length>65536){socket.destroy();reject(Error('健康响应超限'));return;}
   if(buffer.includes('\n')){socket.destroy();try{const r=JSON.parse(buffer.split('\n')[0]);if(!r.ok)throw Error('健康查询失败');resolve(r.result);}catch(e){reject(e);}}});
 });
}
async function inspect(){
 const auth=JSON.parse(await readFile(path.join(data,'model-handoff-authorization.json'),'utf8'));
 const h=await service();
 const cycle=plan.cycleId ? inspectCycle(plan,await service('get_model_decision',{decisionId:plan.cycleId})) : {returning:false};
 if(plan.cycleId){const settings=await service('get_supervisor_settings');const config=settings.configs.find(c=>c.sessionId===plan.threadId);
  if(config?.version!==plan.supervisorVersion)throw Error('用户主管配置已改变');}
 const file=await open(source,'r');let bytes;
 try{const first=Buffer.alloc(65536);const head=await file.read(first,0,first.length,0);const text=first.subarray(0,head.bytesRead).toString('utf8');
  if(!text.includes('\n'))throw Error('会话身份记录无效');const meta=JSON.parse(text.split('\n')[0]);
  if(meta.type!=='session_meta'||meta.payload?.id!==plan.threadId)throw Error('会话身份不匹配');
  const stat=await file.stat();const offset=Math.max(0,stat.size-2*1024*1024);const buffer=Buffer.alloc(stat.size-offset);const tail=await file.read(buffer,0,buffer.length,offset);bytes=buffer.subarray(0,tail.bytesRead).toString('utf8');if(offset)bytes=bytes.slice(bytes.indexOf('\n')+1);}finally{await file.close();}
 if(!bytes.endsWith('\n'))return {authorized:auth.threads.includes(plan.threadId),modelsEnabled:h.modules.modelsEnabled,...cycle,latestTurnId:plan.sourceTurnId,finished:false};
 let latest=null,finished=false,newUserInput=false;
 for(const line of bytes.split('\n')){
  if(!line)continue;const r=JSON.parse(line),p=r.payload;
  if(r.type==='turn_context'){latest=p.turn_id;}
  if(r.type==='event_msg'&&p.type==='task_complete'&&p.turn_id===plan.sourceTurnId)finished=true;
  if(Date.parse(r.timestamp)>Date.parse(plan.armedAfter)&&r.type==='response_item'&&p.type==='message'&&p.role==='user')newUserInput=true;
 }
 if(!latest)throw Error('有界运行记录中找不到实际轮次');
 return {authorized:auth.threads.includes(plan.threadId),modelsEnabled:h.modules.modelsEnabled,...cycle,latestTurnId:latest,finished,newUserInput};
}
async function ipc(action){
 const socket=net.createConnection('\\\\.\\pipe\\codex-ipc');const pending=new Map();let buffer=Buffer.alloc(0),clientId='initializing';
 const fail=e=>{for(const p of pending.values()){clearTimeout(p.timer);p.reject(e);}pending.clear();};
 socket.on('error',fail);socket.on('close',()=>fail(Error('IPC已断开')));
 socket.on('data',chunk=>{buffer=Buffer.concat([buffer,chunk]);while(buffer.length>=4){const n=buffer.readUInt32LE(0);if(n<1||n>4*1024*1024){socket.destroy();fail(Error('IPC响应超限'));return;}if(buffer.length<n+4)return;
  let r;try{r=JSON.parse(buffer.subarray(4,n+4).toString('utf8'));}catch{socket.destroy();fail(Error('IPC响应无效'));return;}buffer=buffer.subarray(n+4);
  if(r.type!=='response')continue;const p=pending.get(r.requestId);if(p){pending.delete(r.requestId);clearTimeout(p.timer);if(r.resultType!=='success')p.reject(Error('IPC拒绝请求'));else p.resolve(r);}
 }});
 function call(method,params,version,targetClientId){
  return new Promise((resolve,reject)=>{const requestId=randomUUID(),timer=setTimeout(()=>{pending.delete(requestId);reject(Error('IPC请求超时，禁止重发'));},8000);pending.set(requestId,{resolve,reject,timer});
   const b=Buffer.from(JSON.stringify({type:'request',requestId,sourceClientId:clientId,version,method,params,timeoutMs:7000,...targetClientId?{targetClientId}:{}}));const frame=Buffer.alloc(b.length+4);frame.writeUInt32LE(b.length);b.copy(frame,4);socket.write(frame);
  });
 }
 try{
  await new Promise((resolve,reject)=>{const t=setTimeout(()=>{socket.destroy();reject(Error('IPC连接超时'));},3000);socket.once('connect',()=>{clearTimeout(t);resolve();});socket.once('error',e=>{clearTimeout(t);reject(e);});});
  clientId=(await call('initialize',{clientType:'codex-dispatch-continuation'},0)).result.clientId;
  const owner=await call('thread-owner-discovery',{hostId:'local',conversationId:plan.threadId},1);const target=owner.handledByClientId;
  const r=await call('thread-follower-read-model-settings',{conversationId:plan.threadId},1,target);
  if(r.handledByClientId!==target||r.result?.settings?.model!==plan.expectedModel)throw Error('拥有者或用户模型设置已改变');
  if(action==='check')return;
  const state=await inspect();if(!state.authorized||(!state.modelsEnabled&&!state.returning)||!state.finished||state.newUserInput||state.latestTurnId!==plan.sourceTurnId)throw Error('发送前状态已改变');
  // 沿用当前设置，不传model/effort，不覆盖用户强度；本入口仅验证续跑。
  const response=await call('thread-follower-start-turn',{conversationId:plan.threadId,turnStart:{request:{threadId:plan.threadId,input:[{type:'text',text:plan.prompt,text_elements:[]}],clientUserMessageId:plan.sourceTurnId+'-dispatch-continuation'},context:{inheritThreadSettings:true}}},2,target);
  if(response.handledByClientId!==target)throw Error('启动响应拥有者改变');
  return response.result?.result?.turn?.id;
 }finally{socket.destroy();fail(Error('续跑IPC连接已结束'));}
}
await continueOnce(plan,{
 claim:async()=>{try{const f=await open(journal,'wx');await f.writeFile(JSON.stringify({status:'ARMED',threadId:plan.threadId,sourceTurnId:plan.sourceTurnId}));await f.close();return true;}catch(e){if(e.code==='EEXIST')return false;throw e;}},
 inspect,preflight:()=>ipc('check'),record,start:()=>ipc('start'),pause:()=>new Promise(r=>setTimeout(r,500)),
});
