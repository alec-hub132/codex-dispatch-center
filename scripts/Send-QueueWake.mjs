import net from 'node:net';
import { createHash,randomUUID } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { readFile,writeFile,open,realpath } from 'node:fs/promises';
import path from 'node:path';
import { sendQueueWakeOnce } from './queue-wake-policy.mjs';
import { prepareQueueDesktop } from './codex-queue-ipc.mjs';

if(process.platform!=='win32'||!process.argv[2])throw Error('仅支持Windows固定接续发送器');
const plan=JSON.parse(await readFile(process.argv[2],'utf8'));
if(!/^[a-zA-Z0-9_-]{1,80}$/.test(plan.instance)||typeof plan.token!=='string'||!plan.token)throw Error('计划无效');
const data=await realpath(plan.dataRoot);
const sid=execFileSync('whoami',['/user','/fo','csv','/nh'],{encoding:'utf8',windowsHide:true}).match(/S-1-\d+(?:-\d+)+/)?.[0];
if(!sid)throw Error('当前Windows用户不可确定');
const servicePipe='\\\\.\\pipe\\CodexDispatch-'+createHash('sha256').update(sid).digest('hex').toUpperCase().slice(0,20)+'-'+plan.instance;
let desktop;
const journal=path.join(data,'diagnostics','queue-wake-'+plan.wakeId+'.json');
function beginSend(){
 return new Promise((resolve,reject)=>{
  const socket=net.createConnection(servicePipe);let buffer='';
  const timer=setTimeout(()=>{socket.destroy();reject(Error('服务核验超时'));},2500);
  socket.on('error',reject);socket.on('close',()=>clearTimeout(timer));
  socket.on('connect',()=>socket.write(JSON.stringify({version:1,requestId:randomUUID(),method:'begin_queue_wake_send',arguments:{leaseId:plan.leaseId,token:plan.token,wakeId:plan.wakeId}})+'\n'));
  socket.on('data',chunk=>{
   buffer+=chunk.toString('utf8');if(buffer.length>16384){socket.destroy();reject(Error('服务响应超限'));return;}
   if(buffer.includes('\n')){socket.destroy();try{const r=JSON.parse(buffer.split('\n')[0]);resolve(r.ok&&r.result?.allowed===true);}catch{reject(Error('核验响应无效'));}}
  });
 });
}
try{
 await sendQueueWakeOnce(plan,{
  claim:async()=>{try{const file=await open(journal,'wx');await file.writeFile(JSON.stringify({leaseId:plan.leaseId,wakeId:plan.wakeId,status:'DISPATCHING'}));await file.close();return true;}catch(e){if(e.code==='EEXIST')return false;throw e;}},
  preflight:async()=>{desktop=await prepareQueueDesktop(plan);},beginSend,start:()=>desktop.start(),
  // 不写计划、正文、令牌或原始IPC响应。
  record:(status,newTurnId)=>writeFile(journal,JSON.stringify({leaseId:plan.leaseId,wakeId:plan.wakeId,threadId:plan.threadId,sourceTurnId:plan.sourceTurnId,status,newTurnId,at:new Date().toISOString()})),
 });
}finally{desktop?.close();}
