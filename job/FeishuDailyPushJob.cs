using dy.net.service;
using Quartz;

namespace dy.net.job
{
    /// <summary>飞书每日推送任务(Quartz 调度入口,逻辑全在 FeishuPushService)。</summary>
    [DisallowConcurrentExecution]
    public class FeishuDailyPushJob : IJob
    {
        private readonly FeishuPushService pushService;

        public FeishuDailyPushJob(FeishuPushService pushService)
        {
            this.pushService = pushService;
        }

        public async Task Execute(IJobExecutionContext context)
        {
            await pushService.RunDailyPushAsync(waitForSubtitles: true);
        }
    }
}
