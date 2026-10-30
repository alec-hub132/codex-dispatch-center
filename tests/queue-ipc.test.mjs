import test from 'node:test';
import assert from 'node:assert/strict';
import net from 'node:net';
import {randomUUID} from 'node:crypto';
import {prepareQueueDesktop} from '../scripts/codex-queue-ipc.mjs';

async function fakeDesktop(action,run){
 const name='\\\\.\\pipe\\dispatch-fake-queue-'+randomUUID(),requests=[],sockets=new Set();
 const server=net.createServer(socket=>{
  sockets.add(socket);socket.on('close',()=>sockets.delete(socket));let buffer=Buffer.alloc(0);
  socket.on('data',chunk=>{buffer=Buffer.concat([buffer,chunk]);while(buffer.length>=4){
   const n=buffer.readUInt32LE();if(buffer.length<n+4)return;
   const r=JSON.parse(buffer.subarray(4,n+4));buffer=buffer.subarray(n+4);requests.push(r);
   const reply={type:'response',requestId:r.requestId,resultType:'success',handledByClientId:'original-owner',result:{}};
   if(r.method==='initialize')reply.result={clientId:'queue-client'};
   if(r.method==='thread-follower-read-model-settings')reply.result={settings:{model:'user-chosen-model',reasoningEffort:'user-chosen-effort'}};
   if(r.method==='thread-follower-start-turn')reply.result={result:{turn:{id:'new-real-turn'}}};
   action?.(r,reply);const bytes=Buffer.from(JSON.stringify(reply)),frame=Buffer.alloc(bytes.length+4);frame.writeUInt32LE(bytes.length);bytes.copy(frame,4);socket.write(frame);
  }});
 });
 await new Promise((resolve,reject)=>{server.once('error',reject);server.listen(name,resolve);});
 try{await run(name,requests);}finally{for(const socket of sockets)socket.destroy();await new Promise(r=>server.close(r));}
}
test('实际帧协议仅发现原聊天并启动一次，继承用户模型及强度',async()=>{
 await fakeDesktop(null,async(name,requests)=>{
  const desktop=await prepareQueueDesktop({threadId:'original-thread',leaseId:'lease',prompt:'原任务'},name);
  try{
   assert.equal(requests.length,3);assert.equal(await desktop.start(),'new-real-turn');await assert.rejects(desktop.start());
   assert.deepEqual(requests.map(r=>r.method),['initialize','thread-owner-discovery','thread-follower-read-model-settings','thread-follower-start-turn']);
   const r=requests[3];assert.equal(r.targetClientId,'original-owner');assert.equal(r.params.conversationId,'original-thread');
   assert.deepEqual(r.params.turnStart.context,{inheritThreadSettings:true});
   assert.ok(!('model' in r.params.turnStart.request));assert.ok(!('effort' in r.params.turnStart.request));
  }finally{desktop.close();}
 });
});
test('原拥有者变化或协议拒绝时不启动其他聊天',async()=>{
 for(const mode of ['changed-owner','denied'])await fakeDesktop((r,reply)=>{
  if(r.method==='thread-follower-read-model-settings'){if(mode==='changed-owner')reply.handledByClientId='other-owner';else reply.resultType='error';}
 },async(name,requests)=>{
  await assert.rejects(prepareQueueDesktop({threadId:'original-thread'},name));assert.ok(!requests.some(r=>r.method==='thread-follower-start-turn'));
 });
});
