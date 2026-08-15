# 视频统计数据 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 同步视频入库时保存播放/点赞/评论/分享/收藏五项统计，前端「数据」列聚合展示+悬浮详情，并提供手动回填 API 补老数据。

**Architecture:** 四层各改一处——响应模型恢复 `Statistics` 建模、`DouyinVideo` 实体+SQLite 加 5 列、`CreateVideoEntity` 映射、前端聚合列；回填走 `VideoController` 新端点（注入 cookie/http 服务，翻页拉列表按 AwemeId 局部更新统计列）。

**Tech Stack:** .NET 6 + Newtonsoft.Json（`[JsonProperty]`）、SqlSugar（`UpdateOne`）、SQLite（ALTER TABLE）、Vue3 + ant-design-vue（a-tooltip）。

## Global Constraints

- 统计字段类型统一 `long?`（实体与模型），映射时 `?? 0`
- 表结构变更走一次性 `ALTER TABLE`（宿主 python sqlite3 执行），不动 CodeFirst
- 回填只 UPDATE 五个统计字段，不得触碰视频文件/标题/字幕等其他字段
- 回填翻页节奏：页间 `Task.Delay(Random 2-10s)`，安全上限 50 页
- 部署链：`dotnet publish` → `docker cp dy.net.dll` → 前端 `npm run build` → `docker cp dist`（展平嵌套）→ `docker commit dysync:asr-local` → `docker compose up -d --force-recreate`
- 编码安全：所有新中文文案直接写中文（UTF-8）

---

### Task 1: 模型 + 实体 + 表结构

**Files:**
- Modify: `D:\dysync\dysync.net\model\response\DouyinVideoInfoResponse.cs`（Aweme 类 :310 附近注释区 + 文件内新类）
- Modify: `D:\dysync\dysync.net\model\entity\DouyinVideo.cs`（SubtitleStatusMsg 后）
- Modify: `D:\dysync\data\db\dy.sqlite`（ALTER TABLE）

**Interfaces:**
- Produces: `Aweme.Statistics`（`Statistics` 类型，五属性 `long?`）；`DouyinVideo.PlayCount/DiggCount/CommentCount/ShareCount/CollectCount`（`long?`）——Task 2/3 消费

- [ ] **Step 1: Aweme 里恢复 Statistics 属性**

定位（DouyinVideoInfoResponse.cs 约 :308-311）：
```csharp
        //[JsonProperty("statistics")]
        //public Statistics Statistics { get; set; }
```
改为：
```csharp
        [JsonProperty("statistics")]
        public Statistics Statistics { get; set; }
```

- [ ] **Step 2: 新建 Statistics 类（文件末尾 `public class Aweme` 同级，其他类的风格）**

在 `DouyinVideoInfoResponse.cs` 文件末尾（最后一个类的闭括号前）加：
```csharp
    public class Statistics
    {
        /// <summary>播放量</summary>
        [JsonProperty("play_count")]
        public long? PlayCount { get; set; }

        /// <summary>点赞数</summary>
        [JsonProperty("digg_count")]
        public long? DiggCount { get; set; }

        /// <summary>评论数</summary>
        [JsonProperty("comment_count")]
        public long? CommentCount { get; set; }

        /// <summary>分享数</summary>
        [JsonProperty("share_count")]
        public long? ShareCount { get; set; }

        /// <summary>收藏数</summary>
        [JsonProperty("collect_count")]
        public long? CollectCount { get; set; }
    }
```

- [ ] **Step 3: DouyinVideo 实体加 5 字段**

定位 `model/entity/DouyinVideo.cs` 的：
```csharp
        public string SubtitleStatusMsg { get; set; }
```
其后加：
```csharp
        /// <summary>播放量</summary>
        public long? PlayCount { get; set; }

        /// <summary>点赞数</summary>
        public long? DiggCount { get; set; }

        /// <summary>评论数</summary>
        public long? CommentCount { get; set; }

        /// <summary>分享数</summary>
        public long? ShareCount { get; set; }

        /// <summary>收藏数</summary>
        public long? CollectCount { get; set; }
```

- [ ] **Step 4: SQLite 加列（宿主直改，一次性）**

Run:
```bash
python -c "
import sqlite3
db=r'D:\dysync\data\db\dy.sqlite'
c=sqlite3.connect(db)
for col in ['PlayCount','DiggCount','CommentCount','ShareCount','CollectCount']:
    try:
        c.execute(f'ALTER TABLE dy_collect_video ADD COLUMN {col} INTEGER')
        print(f'{col}: added')
    except sqlite3.OperationalError as e:
        print(f'{col}: {e}')
c.commit(); c.close()
"
```
Expected: 5 行 `: added`（若已存在会打印 duplicate column，幂等安全）

- [ ] **Step 5: 编译验证**

Run: `cd /d/dysync/dysync.net && /d/dotnet-sdk/dotnet.exe build dy.net.csproj -c Release 2>&1 | tail -3`
Expected: `0 个错误`（0 errors；警告可忽略）

- [ ] **Step 6: 提交**

```bash
cd /d/dysync/dysync.net
git add model/response/DouyinVideoInfoResponse.cs model/entity/DouyinVideo.cs
git commit -m "feat: 视频统计建模(Aweme.Statistics+实体5字段)+SQLite加列"
```
（带 Co-Authored-By 尾注）

---

### Task 2: 入库映射

**Files:**
- Modify: `D:\dysync\dysync.net\job\DouyinBasicSyncJob.cs`（`CreateVideoEntity` 对象初始化器，:1537 `CateXId` 行后）

**Interfaces:**
- Consumes: Task 1 的 `Aweme.Statistics` 与实体字段
- Produces: 同步入库的视频带统计值（Task 4 验证依赖）

- [ ] **Step 1: 对象初始化器加 5 行映射**

定位（DouyinBasicSyncJob.cs `CreateVideoEntity` 内，`CateId = cate?.Id,` 与 `CateXId = cate?.XId,` 之后、`};` 之前）：
```csharp
                CateId = cate?.Id,
                CateXId = cate?.XId,
```
其后加：
```csharp
                PlayCount = item.Statistics?.PlayCount ?? 0,
                DiggCount = item.Statistics?.DiggCount ?? 0,
                CommentCount = item.Statistics?.CommentCount ?? 0,
                ShareCount = item.Statistics?.ShareCount ?? 0,
                CollectCount = item.Statistics?.CollectCount ?? 0,
```

- [ ] **Step 2: 编译验证**

Run: `cd /d/dysync/dysync.net && /d/dotnet-sdk/dotnet.exe build dy.net.csproj -c Release 2>&1 | tail -3`
Expected: `0 个错误`

- [ ] **Step 3: 提交**

```bash
git add job/DouyinBasicSyncJob.cs
git commit -m "feat: CreateVideoEntity 映射五项统计"
```

---

### Task 3: 回填 API

**Files:**
- Modify: `D:\dysync\dysync.net\Controllers\VideoController.cs`（依赖注入 + 新端点，放 `GetSubtitleContent` 之后）

**Interfaces:**
- Consumes: `douyinCookieService.GetOpendCookiesAsync(...)`（Func 筛选）、`douyinHttpClientService.SyncFavoriteVideos(count, cursor, secUserId, cookie)`、`douyinVideoService.GetByAwemeId(awemeId)` / `UpdateOne(video)`
- Produces: `POST /api/video/stats/backfill` → `ApiResult.Success(new { updated, scanned })`（Task 4 前端消费）

- [ ] **Step 1: 构造器注入两个服务**

把：
```csharp
        private readonly DouyinVideoService douyinVideoService;
        private readonly DouyinCommonService douyinCommonService;
        private readonly LocalAsrSubtitleService localAsrSubtitleService;

        public VideoController(DouyinVideoService dyCollectVideoService, DouyinCommonService douyinCommonService, LocalAsrSubtitleService localAsrSubtitleService)
        {
            this.douyinVideoService = dyCollectVideoService;
            this.douyinCommonService = douyinCommonService;
            this.localAsrSubtitleService = localAsrSubtitleService;
        }
```
改为：
```csharp
        private readonly DouyinVideoService douyinVideoService;
        private readonly DouyinCommonService douyinCommonService;
        private readonly LocalAsrSubtitleService localAsrSubtitleService;
        private readonly DouyinCookieService douyinCookieService;
        private readonly DouyinHttpClientService douyinHttpClientService;

        public VideoController(DouyinVideoService dyCollectVideoService, DouyinCommonService douyinCommonService, LocalAsrSubtitleService localAsrSubtitleService, DouyinCookieService douyinCookieService, DouyinHttpClientService douyinHttpClientService)
        {
            this.douyinVideoService = dyCollectVideoService;
            this.douyinCommonService = douyinCommonService;
            this.localAsrSubtitleService = localAsrSubtitleService;
            this.douyinCookieService = douyinCookieService;
            this.douyinHttpClientService = douyinHttpClientService;
        }
```

- [ ] **Step 2: 加回填端点（`GetSubtitleContent` 方法闭括号后）**

```csharp
        /// <summary>
        /// 回填统计数据:翻页拉取喜欢列表,按AwemeId匹配库中视频,只更新五项统计字段。
        /// 不动视频文件/标题/字幕等其他字段。安全上限50页。
        /// </summary>
        [Authorize]
        [HttpPost("stats/backfill")]
        public async Task<IActionResult> BackfillVideoStats()
        {
            var cookies = await douyinCookieService.GetOpendCookiesAsync(
                x => !string.IsNullOrWhiteSpace(x.FavSavePath) && !string.IsNullOrWhiteSpace(x.SecUserId));
            if (cookies == null || !cookies.Any())
            {
                return ApiResult.Fail("没有可用的 Cookie(需配置喜欢视频存储路径且已授权)");
            }

            int updated = 0, scanned = 0;
            var random = new Random();

            foreach (var cookie in cookies)
            {
                string cursor = "0";
                for (var page = 0; page < 50; page++)
                {
                    DouyinVideoInfoResponse data;
                    try
                    {
                        data = await douyinHttpClientService.SyncFavoriteVideos("20", cursor, cookie.SecUserId, cookie.Cookies);
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Error(ex, "[stats/backfill] 拉取失败, cookie={User}", cookie.UserName);
                        break;
                    }
                    if (data?.AwemeList == null || !data.AwemeList.Any())
                    {
                        break;
                    }

                    foreach (var item in data.AwemeList)
                    {
                        scanned++;
                        var video = await douyinVideoService.GetByAwemeId(item.AwemeId);
                        if (video == null || item.Statistics == null)
                        {
                            continue;
                        }
                        video.PlayCount = item.Statistics.PlayCount ?? 0;
                        video.DiggCount = item.Statistics.DiggCount ?? 0;
                        video.CommentCount = item.Statistics.CommentCount ?? 0;
                        video.ShareCount = item.Statistics.ShareCount ?? 0;
                        video.CollectCount = item.Statistics.CollectCount ?? 0;
                        await douyinVideoService.UpdateOne(video);
                        updated++;
                    }

                    if (data.HasMore != 1)
                    {
                        break;
                    }
                    cursor = data.Cursor ?? (data.MaxCursor ?? "0");
                    await Task.Delay(random.Next(2, 10) * 1000);
                }
            }

            Serilog.Log.Information("[stats/backfill] 完成: updated={Updated} scanned={Scanned}", updated, scanned);
            return ApiResult.Success(new { updated, scanned });
        }
```

- [ ] **Step 3: 编译验证**

Run: `cd /d/dysync/dysync.net && /d/dotnet-sdk/dotnet.exe build dy.net.csproj -c Release 2>&1 | tail -3`
Expected: `0 个错误`。若 `GetOpendCookiesAsync` 命名不同，以 `service/DouyinCookieService.cs` 实际为准（曾见于 `DouYinFavoritSyncJob.cs:25` 的用法）。

- [ ] **Step 4: 提交**

```bash
git add Controllers/VideoController.cs
git commit -m "feat: POST /api/video/stats/backfill 手动回填统计"
```

---

### Task 4: 前端「数据」列 + 回填按钮 + 部署验证

**Files:**
- Modify: `D:\dysync\dysync.net\app\src\pages\workplace\RecordTable.vue`（columns、#bodyCell、DataItem、formatCount）
- Modify: `D:\dysync\dysync.net\app\src\pages\set\AppSet.vue`（回填按钮）
- Modify: `D:\dysync\dysync.net\app\src\store\coreapi.ts`（回填 API 封装）

**Interfaces:**
- Consumes: Task 3 的 `/api/video/stats/backfill`；分页 API 已返回实体新字段（后端序列化自动带出）
- Produces: 无（终端交付）

- [ ] **Step 1: coreapi.ts 加回填封装（GetSubtitleContent 函数后）**

```ts
  //回填统计数据
  async function BackfillVideoStats() {
    return http.request<any, Response<any>>('/api/video/stats/backfill', 'post_json', {}).then(r => {
      return r;
    }).finally(() => {
    });
  }
```
并在文件末尾 `return {` 的导出对象里（`GetSubtitleContent,` 行后）加 `BackfillVideoStats,`

- [ ] **Step 2: RecordTable.vue — DataItem 补字段**

定位 `subtitleCreateTime?: string;     // 生成时间` 后加：
```ts
  playCount?: number;      // 播放量
  diggCount?: number;      // 点赞
  commentCount?: number;   // 评论
  shareCount?: number;     // 分享
  collectCount?: number;   // 收藏
```

- [ ] **Step 3: RecordTable.vue — formatCount 函数（subtitleStatusOf 附近）**

```ts
/** 数字格式化:万/亿中文缩写 */
const formatCount = (n?: number): string => {
  if (!n || n <= 0) return '0';
  if (n >= 100000000) return (n / 100000000).toFixed(1).replace(/\.0$/, '') + '亿';
  if (n >= 10000) return (n / 10000).toFixed(1).replace(/\.0$/, '') + '万';
  return n.toLocaleString();
};
```

- [ ] **Step 4: RecordTable.vue — columns 加「数据」列**

定位「字幕」列对象后、「操作」前插入：
```ts
  {
    title: '数据',
    dataIndex: 'stats',
    align: 'center',
    width: 150,
  },
```

- [ ] **Step 5: RecordTable.vue — #bodyCell 加 stats 分支**

定位 `v-if="column.dataIndex === 'subtitle'"` 模板前，插入：
```html
        <template v-if="column.dataIndex === 'stats'">
          <a-tooltip
            v-if="record.diggCount > 0 || record.playCount > 0"
            :title="`播放 ${formatCount(record.playCount)}\n点赞 ${formatCount(record.diggCount)}\n评论 ${formatCount(record.commentCount)}\n分享 ${formatCount(record.shareCount)}\n收藏 ${formatCount(record.collectCount)}`"
          >
            <span class="stats-cell">{{ formatCount(record.diggCount) }}赞 · {{ formatCount(record.commentCount) }}评</span>
          </a-tooltip>
          <span v-else>-</span>
        </template>
```

- [ ] **Step 6: RecordTable.vue — 样式（.subtitle-content-box 附近）**

```css
.stats-cell {
  cursor: default;
  white-space: nowrap;
}
```

- [ ] **Step 7: AppSet.vue — 回填按钮（ASR Status 区块后）**

定位 `</a-form-item>`（ASR Status 块的闭合，`checkAsrHealth` 按钮所在块）后加：
```html
        <a-form-item label="视频统计">
          <a-button :loading="backfillLoading" @click="handleBackfillStats">回填统计数据</a-button>
          <span style="margin-left: 8px; color: #888; font-size: 12px">为已同步视频补齐播放/点赞等数据，不影响视频文件</span>
        </a-form-item>
```
script 加（asrHealthDetail 附近）：
```ts
const backfillLoading = ref(false);
const handleBackfillStats = () => {
  Modal.confirm({
    title: '回填统计数据',
    content: '将重新拉取列表接口为已同步视频补齐统计数据（不动视频文件），翻页拉取约需几分钟，期间请勿频繁操作。是否继续？',
    okText: '开始回填',
    cancelText: '取消',
    onOk: () => {
      backfillLoading.value = true;
      useApiStore()
        .BackfillVideoStats()
        .then((res: any) => {
          backfillLoading.value = false;
          if (res.code === 0) {
            message.success(`回填完成：更新 ${res.data?.updated ?? 0} 条 / 扫描 ${res.data?.scanned ?? 0} 条`);
          } else {
            message.error(res.message || '回填失败');
          }
        })
        .catch(() => {
          backfillLoading.value = false;
          message.error('回填失败，请稍后重试');
        });
    },
  });
};
```
（`Modal`/`message`/`useApiStore` 若未 import 则按文件现有 import 风格补；`ref` 已有）

- [ ] **Step 8: 前端构建**

Run: `cd /d/dysync/dysync.net/app && npm run build 2>&1 | tail -3`
Expected: `✓ built in ...s`，无 vue-tsc 错误

- [ ] **Step 9: 部署（后端 dll + 前端 dist）**

```bash
cd /d/dysync/dysync.net
/d/dotnet-sdk/dotnet.exe publish dy.net.csproj -c Release -r linux-x64 --self-contained false -o /d/dysync/build-context/pub-stats
docker cp /d/dysync/build-context/pub-stats/dy.net.dll dysync2026:/app/dy.net.dll
docker exec dysync2026 sh -c 'rm -rf /app/app/dist/assets /app/app/dist/index.html /app/app/dist/logo.png /app/app/dist/dist'
docker cp /d/dysync/dysync.net/app/dist/. dysync2026:/app/app/dist
docker exec dysync2026 sh -c 'if [ -d /app/app/dist/dist ]; then cd /app/app/dist && rm -rf assets index.html logo.png && cp -r dist/* ./ && rm -rf dist && echo 已展平; else echo 无嵌套; fi'
docker commit dysync2026 dysync:asr-local
cd /d/dysync && docker compose up -d --force-recreate
```
Expected: recreate 成功；容器 Image == 镜像 Id

- [ ] **Step 10: 验证回填（真实数据）**

```bash
sleep 8
TOKEN=$(curl -s -X POST "http://localhost:10101/api/Auth/Login" -H 'Content-Type: application/json' -d '{"UserName":"douyin","Password":"douyin2026"}' | python -c "import sys,json;print(json.load(sys.stdin).get('token',''))")
curl -s -m 600 -X POST "http://localhost:10101/api/video/stats/backfill" -H "Authorization: Bearer $TOKEN"
echo
python -c "
import sqlite3
c=sqlite3.connect(r'D:\dysync\data\db\dy.sqlite'); cur=c.cursor()
cur.execute('SELECT COUNT(*) FROM dy_collect_video WHERE DiggCount > 0')
print('有统计值的视频:', cur.fetchone()[0], '/', end=' ')
cur.execute('SELECT COUNT(*) FROM dy_collect_video')
print(cur.fetchone()[0])"
```
Expected: backfill 返回 `updated>=19`；SQL 显示 `有统计值的视频: 19 / 19+`

- [ ] **Step 11: 提交 + 记忆更新**

```bash
git add app/src/store/coreapi.ts app/src/pages/workplace/RecordTable.vue app/src/pages/set/AppSet.vue
git commit -m "feat: 同步记录「数据」列(赞/评聚合+悬浮五项)+设置页回填按钮"
```
更新 `asr-integration-status.md` 所在记忆体系：新增长记 `video-stats-fields.md`（统计字段、回填端点、格式化规则）或并入现有 dysync 部署记忆。

---

## Self-Review

1. **Spec 覆盖**：模型✅T1S1-2 实体✅T1S3 表✅T1S4 映射✅T2 前端聚合列+悬浮✅T4S4-6 回填API✅T3 回填按钮✅T4S7 验证标准逐条对应✅T4S10（19条回填）+部署链✅T4S9
2. **占位符**：T3S3/T4S7 各有一处「以实际为准」的防御性说明（非占位，代码完整给出）
3. **类型一致**：`Statistics.PlayCount(long?)` ↔ `DouyinVideo.PlayCount(long?)` ↔ 映射 `?? 0` ↔ 前端 `diggCount?: number` 一致；回填端点路径 T3 产出 `/api/video/stats/backfill` = T4S1 消费一致
