using SqlSugar;

namespace dy.net.model.entity
{

    [SugarTable(TableName = "dy_app_config")]
    public class AppConfig
    {
        /// <summary>
        /// 是否是程序启动时...
        /// </summary>
        public bool IsFirstRunning { get; set; }
        /// <summary>
        /// 
        /// </summary>
        [SugarColumn(IsPrimaryKey = true, Length = 100)]
        public string Id { get; set; }

        [SugarColumn(Length = 200, IsNullable = true)]
        public int Cron { get; set; }

        /// <summary>
        /// 每次查询数量
        /// </summary>
        public int BatchCount { get; set; } = 18;

        /// <summary>
        /// 博主视频是否 直接用标题做文件名
        /// </summary>
        //public bool UperUseViedoTitle { get; set; }
        /// <summary>
        /// 是否博主视频直接放一个根目录，不另外按名字建文件夹
        /// </summary>
        //public bool UperSaveTogether { get; set; }
        /// <summary>
        /// 是否下载图片视频
        /// </summary>
        public bool DownImageVideo { get; set; }
        /// <summary>
        /// 图文视频 是否额外下载图片
        /// </summary>
        public bool DownImage { get; set; }
        /// <summary>
        /// 图文视频 是否额外下载mp3
        /// </summary>
        public bool DownMp3 { get; set; }

        /// <summary>
        /// 日志保留天数,防止容器日志太多，默认10天
        /// </summary>
        public int LogKeepDay { get; set; } = 10;
        /// <summary>
        /// 关注的视频标题命名模板{id}{VideoTitle}{SyncTime}{ReleaseTime}{FileHash}{Resolution}{FileSize}
        /// </summary>
        public string FollowedTitleTemplate { get; set; }
        /// <summary>
        /// 分隔符
        /// </summary>
        public string FollowedTitleSeparator { get; set; }
        /// <summary>
        /// 完整的标题模板，包含分隔符
        /// </summary>
        public string FullFollowedTitleTemplate { get; set; }


        /// <summary>
        /// 图文视频是否单独存放，否的话则按原类型存储位置存放，比如收藏夹、喜欢等
        /// </summary>
        //public bool ImageViedoSaveAlone { get; set; }



        /// <summary>
        /// 自动去重-逻辑是遇到相同ID的视频直接跳过
        /// </summary>
        public bool AutoDistinct { get; set; }

        /// <summary>
        /// 去重优先级配置，json字符串格式存储
        /// </summary>
        [SugarColumn(Length = 500, IsNullable = true)]
        public string PriorityLevel { get; set; }

        /// <summary>
        /// 是否下载动态图视频
        /// </summary>
        public bool DownDynamicVideo { get; set; }

        /// <summary>
        /// 仅同步新视频（github 有人提议增加这个配置项，因为之前收藏了很多烂七八糟的视频。不想同步，又不想一个一个清除）
        /// </summary>
        public bool OnlySyncNew { get; set; } = true;
        /// <summary>
        /// 是否合并下载动态图视频
        /// </summary>
        public bool MegDynamicVideo { get; set; }
        /// <summary>
        /// 保留原动态视频文件
        /// </summary>
        public bool KeepDynamicVideo { get; set; }

        ///// <summary>
        ///// 合集路径
        ///// </summary>
        //[SugarColumn(Length = 500, IsNullable = true)]
        //public string MixPath { get; set; }
        ///// <summary>
        ///// 短剧路径
        ///// </summary>
        //[SugarColumn(Length = 500, IsNullable = true)]
        //public string SeriesPath { get; set; }

        /// <summary>
        /// 264 265 0-默认0=264
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public int? VideoEncoder { get; set; }
        /// <summary>
        /// 是否不启用刮削
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public bool CloseNfo { get; set; }

        /// <summary>
        /// 下载完成后自动生成本地字幕
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public bool AutoGenSubtitle { get; set; }

        /// <summary>
        /// 本地ASR服务地址
        /// </summary>
        [SugarColumn(Length = 500, IsNullable = true)]
        public string AsrServiceUrl { get; set; }

        /// <summary>
        /// 兼容旧版本：本地ASR可执行文件路径
        /// </summary>
        [SugarColumn(Length = 500, IsNullable = true)]
        public string AsrExecutablePath { get; set; }

        /// <summary>
        /// 兼容旧版本：本地ASR模型文件路径
        /// </summary>
        [SugarColumn(Length = 500, IsNullable = true)]
        public string AsrModelPath { get; set; }

        /// <summary>
        /// 字幕识别语言，默认 zh
        /// </summary>
        [SugarColumn(Length = 50, IsNullable = true)]
        public string AsrLanguage { get; set; }

        /// <summary>
        /// 可选提示词
        /// </summary>
        [SugarColumn(Length = 500, IsNullable = true)]
        public string AsrPrompt { get; set; }

        /// <summary>
        /// 已存在字幕时是否覆盖
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public bool AsrOverwriteExisting { get; set; }

        /// <summary>
        /// 字幕队列回扫窗口(小时):入库时间在窗口内的无字幕视频会被自动提交转写。默认48;调大可转历史视频(如720=30天,8760=1年,0=只转当天)
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public int AsrBackfillHours { get; set; } = 48;

        /// <summary>飞书推送总开关</summary>
        public bool FeishuPushEnabled { get; set; }
        /// <summary>飞书自建应用 AppId</summary>
        [SugarColumn(Length = 100, IsNullable = true)]
        public string FeishuAppId { get; set; }
        /// <summary>飞书自建应用 AppSecret</summary>
        [SugarColumn(Length = 200, IsNullable = true)]
        public string FeishuAppSecret { get; set; }
        /// <summary>你的飞书邮箱(新建Base后自动加为协作者)</summary>
        [SugarColumn(Length = 200, IsNullable = true)]
        public string FeishuUserEmail { get; set; }
        /// <summary>飞书群机器人webhook(空则跳过通知)</summary>
        [SugarColumn(Length = 500, IsNullable = true)]
        public string FeishuNotifyWebhook { get; set; }
        /// <summary>月度Base存放的文件夹token(空则应用根空间)</summary>
        [SugarColumn(Length = 100, IsNullable = true)]
        public string FeishuFolderToken { get; set; }
        /// <summary>推送时刻cron(默认 0 50 23 * * ?)</summary>
        [SugarColumn(Length = 50, IsNullable = true)]
        public string FeishuPushCron { get; set; }
        /// <summary>运行时缓存:本月Base token(程序自管理,不在设置页展示)</summary>
        [SugarColumn(Length = 100, IsNullable = true)]
        public string FeishuBaseTokenCache { get; set; }
        /// <summary>运行时缓存:缓存对应的月份yyyy-M(程序自管理)</summary>
        [SugarColumn(Length = 20, IsNullable = true)]
        public string FeishuBaseMonthCache { get; set; }
        /// <summary>上次推送结果展示(设置页只读展示)</summary>
        [SugarColumn(Length = 500, IsNullable = true)]
        public string FeishuLastPushResult { get; set; }
        /// <summary>运行时缓存:专属文件夹「抖小云同步数据」token,FolderToken未配置时程序自动建并记在此(程序自管理)</summary>
        [SugarColumn(Length = 100, IsNullable = true)]
        public string FeishuAutoFolderToken { get; set; }
        /// <summary>用户授权token(OAuth user_access_token,程序自管理)</summary>
        [SugarColumn(Length = 4000, IsNullable = true, ColumnDataType = "TEXT")]
        public string FeishuUserAccessToken { get; set; }
        /// <summary>用户授权刷新token(一次性,每次刷新后更新,程序自管理)</summary>
        [SugarColumn(Length = 4000, IsNullable = true, ColumnDataType = "TEXT")]
        public string FeishuUserRefreshToken { get; set; }
        /// <summary>用户授权token过期时刻(程序自管理)</summary>
        [SugarColumn(IsNullable = true)]
        public DateTime? FeishuUserTokenExpiresAt { get; set; }
        /// <summary>用户授权刷新token过期时刻(程序自管理)</summary>
        [SugarColumn(IsNullable = true)]
        public DateTime? FeishuUserRefreshExpiresAt { get; set; }
    }

}
