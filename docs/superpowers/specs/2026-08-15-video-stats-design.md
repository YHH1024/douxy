# 视频统计数据设计（播放/点赞/评论/分享/收藏）

日期：2026-08-15
分支：`asr-windows-test`
范围：响应模型、视频实体、SQLite 表、入库映射、回填 API、前端展示

## 背景

抖音列表接口实际返回每条视频的 `statistics` 统计块，但 `Aweme` 模型里 `Statistics` 属性被注释未建模，数据直接丢弃；`DouyinVideo` 实体与 `dy_collect_video` 表也无统计字段。用户需要播放/点赞/评论/分享/收藏五项数据。

## 数据流

```
抖音列表接口 → statistics 块（恢复注释的模型）
  → CreateVideoEntity 映射 5 个新字段
  → dy_collect_video 表加 5 列（一次性 ALTER TABLE，老数据 NULL）
  → 分页 API 原样返回 → 前端「数据」列聚合显示 + 悬浮详情
```

## 改动点（4 处）

### ① 模型：`model/response/DouyinVideoInfoResponse.cs`
- 取消 `[JsonProperty("statistics")] public Statistics Statistics` 的注释
- 新建 `Statistics` 类（文件内其他类同风格）：

```csharp
public class Statistics
{
    [JsonProperty("play_count")]
    public long? PlayCount { get; set; }

    [JsonProperty("digg_count")]
    public long? DiggCount { get; set; }

    [JsonProperty("comment_count")]
    public long? CommentCount { get; set; }

    [JsonProperty("share_count")]
    public long? ShareCount { get; set; }

    [JsonProperty("collect_count")]
    public long? CollectCount { get; set; }
}
```
- 全部 `long?`：抖音不同接口有时返回字符串数字，反序列化失败时保持 null 而非抛错（Newtonsoft 默认宽松处理）

### ② 实体 + 表：`model/entity/DouyinVideo.cs` + SQLite
- 实体加（`SubtitleStatusMsg` 字段后，同区域）：

```csharp
public long? PlayCount { get; set; }
public long? DiggCount { get; set; }
public long? CommentCount { get; set; }
public long? ShareCount { get; set; }
public long? CollectCount { get; set; }
```
- 表结构：部署时执行一次性 SQL（宿主直改 `D:\dysync\data\db\dy.sqlite`，或经代码 `DbMaintenance.AddColumn`——**选宿主 python sqlite3 直改**，最直接且无需动运行中服务）：

```sql
ALTER TABLE dy_collect_video ADD COLUMN PlayCount INTEGER;
ALTER TABLE dy_collect_video ADD COLUMN DiggCount INTEGER;
ALTER TABLE dy_collect_video ADD COLUMN CommentCount INTEGER;
ALTER TABLE dy_collect_video ADD COLUMN ShareCount INTEGER;
ALTER TABLE dy_collect_video ADD COLUMN CollectCount INTEGER;
```

### ③ 入库映射：`job/DouyinBasicSyncJob.cs` `CreateVideoEntity`（:1511 的对象初始化器内）
```csharp
PlayCount = item.Statistics?.PlayCount ?? 0,
DiggCount = item.Statistics?.DiggCount ?? 0,
CommentCount = item.Statistics?.CommentCount ?? 0,
ShareCount = item.Statistics?.ShareCount ?? 0,
CollectCount = item.Statistics?.CollectCount ?? 0,
```

### ④ 前端：`app/src/pages/workplace/RecordTable.vue`
- `columns` 在「字幕」列后、「操作」前加：

```ts
  {
    title: '数据',
    dataIndex: 'stats',
    align: 'center',
    width: 150,
  },
```
- `#bodyCell` 加 `v-if="column.dataIndex === 'stats'"` 分支：有 DiggCount 时显示 `formatCount(diggCount) + '赞 · ' + formatCount(commentCount) + '评'`，外包 `<a-tooltip>` 显示五项全值（每行一条：播放/点赞/评论/分享/收藏）；全为 0/null 显示 `-`
- `DataItem` 接口补：`playCount?/diggCount?/commentCount?/shareCount?/collectCount?`（number）
- 新增格式化函数（万/亿中文缩写）：

```ts
const formatCount = (n?: number): string => {
  if (!n || n <= 0) return '0';
  if (n >= 100000000) return (n / 100000000).toFixed(1).replace(/\.0$/, '') + '亿';
  if (n >= 10000) return (n / 10000).toFixed(1).replace(/\.0$/, '') + '万';
  return n.toLocaleString();
};
```

## 回填功能（手动触发，老数据补统计）

### 后端：`Controllers/VideoController.cs` 加端点

```csharp
[Authorize]
[HttpPost("stats/backfill")]
public async Task<IActionResult> BackfillVideoStats()
```
逻辑：
1. 取开启的有效 cookie（复用 `GetSyncCookies` 同条件：FavSavePath 非空 + SecUserId 非空）
2. 翻页调 `douyinHttpClientService.SyncFavoriteVideos(count, cursor, secUserId, cookie)`（与同步任务同一接口、同参数），页间 `Task.Delay(random 2-10s)` 防风控
3. 每页按 `AwemeId` 匹配库中已有视频，**只 UPDATE 五个统计字段**（SqlSugar `Updateable` 局部列），不动文件/不动其他字段
4. 返回 `{ updated: N, scanned: M }`；`has_more=0` 或翻满 50 页（安全上限 1000 条）即停

### 前端：`AppSet.vue` ASR 区附近加按钮
「回填统计数据」——`Modal.confirm` 提示"将重新拉取列表接口为已入库视频补统计，不影响视频文件，约几分钟"，确认后 POST，按钮 loading，完成 `message.success('已更新 N 条')`。

## 不做的事（YAGNI）
- 不做定时刷新统计（接口调用量大、风控风险）
- 不做按统计排序（聚合列排序无意义）
- 不动 CodeFirst 建表逻辑（表结构变更走一次性 SQL）
- 不给图文/合集等其它入口单独建模（`Aweme.Statistics` 一处建模全部受益）

## 验证标准
1. 部署后新同步的视频五项统计完整入库（抽查 SQL）
2. 「数据」列显示 `12.3万赞 · 8500评`，悬浮显示五项精确值，空数据显示 `-`
3. 点「回填统计数据」→ 几分钟后 19 条老视频统计从 `-` 变有值
4. 回填不改动老视频的文件路径/标题/字幕等其他字段（SQL 抽查对比）
5. `vue-tsc` 类型检查通过、后端编译 0 错误

## 风险与对策
- 抖音接口 statistics 字段缺失/字符串化 → `long?` 宽松解析，null 时前端显示 `-`，不阻塞同步主流程
- 回填触发风控 → 页间随机延迟与同步任务一致；回填是手动低频操作
- SQLite 加列时服务在写 → ALTER TABLE ADD COLUMN 在 SQLite 是安全操作（不锁旧数据读取）；选在任务间隔期执行
