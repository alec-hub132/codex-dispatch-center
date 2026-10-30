// 一次发送；投递回执不代表接单或测试成功。
export async function sendQueueWakeOnce(plan,io){
 for(const key of ['leaseId','wakeId','threadId','sourceTurnId'])if(!/^[a-zA-Z0-9-]{1,128}$/.test(plan[key]??'')||/[\r\n]/.test(plan[key]))throw Error('关联编号无效');
 if(typeof plan.prompt!=='string'||!plan.prompt.trim()||plan.prompt.length>16000)throw Error('接续任务无效');
 if(!await io.claim())return 'ALREADY_CLAIMED';
 try{
  await io.preflight();
  if(!await io.beginSend())throw Error('原任务已不允许接续');
  const turn=await io.start();
  if(typeof turn!=='string'||turn===plan.sourceTurnId||!/^[a-zA-Z0-9-]{1,128}$/.test(turn)||/[\r\n]/.test(turn))throw Error('未取得有效新轮回执');
  await io.record('SENT',turn);return 'SENT';
 }catch{
  await io.record('STOPPED_NO_RETRY');return 'STOPPED_NO_RETRY';
 }
}
