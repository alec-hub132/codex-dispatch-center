import net from 'node:net';
import { randomUUID } from 'node:crypto';

// 版本限定桌面入口；不创建聊天、不设置模型、不操纵桌面输入。
export async function prepareQueueDesktop(plan,pipeName='\\\\.\\pipe\\codex-ipc'){
 const socket=net.createConnection(pipeName),pending=new Map();let buffer=Buffer.alloc(0),clientId='initializing',started=false;
 const fail=e=>{for(const p of pending.values()){clearTimeout(p.timer);p.reject(e);}pending.clear();};
 socket.on('error',fail);socket.on('close',()=>fail(Error('IPC断开')));
 socket.on('data',chunk=>{
  buffer=Buffer.concat([buffer,chunk]);
  while(buffer.length>=4){
   const n=buffer.readUInt32LE(0);if(n<1||n>4*1024*1024){socket.destroy();fail(Error('IPC响应超限'));return;}
   if(buffer.length<n+4)return;
   let r;try{r=JSON.parse(buffer.subarray(4,n+4).toString('utf8'));}catch{socket.destroy();fail(Error('IPC响应无效'));return;}
   buffer=buffer.subarray(n+4);if(r.type!=='response')continue;
   const p=pending.get(r.requestId);if(p){pending.delete(r.requestId);clearTimeout(p.timer);if(r.resultType!=='success')p.reject(Error('IPC拒绝请求'));else p.resolve(r);}
  }
 });
 function close(){socket.destroy();fail(Error('IPC已关闭'));}
 function call(method,params,version,targetClientId){
  return new Promise((resolve,reject)=>{
   const requestId=randomUUID(),timer=setTimeout(()=>{pending.delete(requestId);reject(Error('IPC超时，不重发'));},4000);
   pending.set(requestId,{resolve,reject,timer});
   const b=Buffer.from(JSON.stringify({type:'request',requestId,sourceClientId:clientId,version,method,params,timeoutMs:3500,...targetClientId?{targetClientId}:{}}));
   const frame=Buffer.alloc(b.length+4);frame.writeUInt32LE(b.length);b.copy(frame,4);socket.write(frame);
  });
 }
 try{
  await new Promise((resolve,reject)=>{
   const timer=setTimeout(()=>{close();reject(Error('IPC不可达'));},2500);
   socket.once('connect',()=>{clearTimeout(timer);resolve();});socket.once('error',e=>{clearTimeout(timer);reject(e);});
  });
  clientId=(await call('initialize',{clientType:'codex-dispatch-queue'},0)).result?.clientId;
  if(typeof clientId!=='string'||!clientId)throw Error('IPC初始化不匹配');
  const owner=await call('thread-owner-discovery',{hostId:'local',conversationId:plan.threadId},1);const target=owner.handledByClientId;
  if(typeof target!=='string'||!target)throw Error('原聊天拥有者不可达');
  const settings=await call('thread-follower-read-model-settings',{conversationId:plan.threadId},1,target);
  if(settings.handledByClientId!==target||typeof settings.result?.settings?.model!=='string')throw Error('原聊天拥有者或协议已改变');
  return {close,start:async()=>{
   if(started)throw Error('禁止再次启动');started=true;
   const result=await call('thread-follower-start-turn',{conversationId:plan.threadId,turnStart:{
    request:{threadId:plan.threadId,input:[{type:'text',text:plan.prompt,text_elements:[]}],clientUserMessageId:'dispatch-queue-'+plan.wakeId},
    context:{inheritThreadSettings:true}}},2,target);
   if(result.handledByClientId!==target)throw Error('新轮响应拥有者不匹配');
   return result.result?.result?.turn?.id;
  }};
 }catch(e){close();throw e;}
}
