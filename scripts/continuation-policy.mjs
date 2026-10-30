// 同一聊天的一次性续跑；传输回执与实际模型生效分开记录。
export async function continueOnce(plan, io) {
  if (!await io.claim(plan)) return 'ALREADY_CLAIMED';
  try {
    for (let i = 0; i < plan.maxPolls; i++) {
      const state = await io.inspect(plan);
      if (!state.authorized || (!state.modelsEnabled && !state.returning)) throw Error('授权已撤销或模型调度已关闭');
      if (state.newUserInput || state.latestTurnId !== plan.sourceTurnId) throw Error('出现新的用户输入或其他轮次，停止续跑');
      if (state.finished) {
        await io.preflight(plan);
        // 在任何可能写入IPC的操作之前持久化；崩溃后不重发。
        await io.record('DISPATCHING');
        const turnId = await io.start(plan);
        if (!turnId) throw Error('启动回执没有轮次编号，结果不明');
        await io.record('STARTED_AWAITING_RUNTIME', { turnId });
        return 'STARTED_AWAITING_RUNTIME';
      }
      await io.pause();
    }
    throw Error('等待原轮次结束超时');
  } catch (error) {
    await io.record('STOPPED_NO_RETRY', { message: error.message });
    return 'STOPPED_NO_RETRY';
  }
}
export function inspectCycle(plan, decision) {
  const p=decision?.execution;
  if(decision?.id!==plan.cycleId || !p || !plan.cycleToken || p.token!==plan.cycleToken || p.threadId!==plan.threadId || p.model!==plan.expectedModel)
    throw Error('周期或交接方向与续跑计划不符');
  if(decision.status!==(p.returning?'RETURN_SENT_AWAITING_VERIFICATION':'SENT_AWAITING_VERIFICATION'))
    throw Error('周期已改变，不再次启动执行或返回任务');
  return {returning:!!p.returning};
}
