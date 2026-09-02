# 抖小云内网数据接口使用指南

> 版本：2026-09-02 ｜ 适用：抖小云 `3da773a` 及之后版本（open/videos 已含 19 字段全量）
> 两个接口均**免登录**，仅供**可信内网**使用（请勿将端口映射到公网）

- 服务地址：`http://<抖小云IP>:10101`（NAS 部署后即 `http://10.1.10.21:10101`）
- 数据格式：JSON（UTF-8）
- 时间格式：`yyyy-MM-dd HH:mm:ss`；筛选参数里可只传日期 `yyyy-MM-dd`
- **中文参数必须 URL 编码（UTF-8）**，如 `uperName=耳火` → `uperName=%E8%80%B3%E7%81%AB`

---

## 一、博主清单接口

```
GET /api/follow/open/uperids
```

### 返回字段说明

| 字段 | 类型 | 说明 |
|---|---|---|
| uperName | string | 博主昵称 |
| uperId | string | 博主数字账号 ID（uid） |
| douyinNo | string | 抖音号 |
| secUid | string | 博主 sec_uid（`MS4wLjABAAAA...` 长串，用于抖音开放接口定位博主） |
| openSync | bool | 是否开启同步 |
| lastSyncTime | string | 该博主最后同步时间 |

### 调用示例

**curl**
```bash
curl http://10.1.10.21:10101/api/follow/open/uperids
```

**Python**
```python
import requests
data = requests.get("http://10.1.10.21:10101/api/follow/open/uperids", timeout=10).json()
print(len(data), "个博主")
for u in data[:3]:
    print(u["uperName"], u["uperId"], u["douyinNo"], u["secUid"][:20])
```

**返回示例**（截取）
```json
[
  {
    "uperName": "耳火Fendy⭐",
    "uperId": "95845330308",
    "douyinNo": "613584202",
    "secUid": "MS4wLjABAAAA09vRitmlDkWCests2XIkj2kLMzb...",
    "openSync": true,
    "lastSyncTime": "2026-09-01 22:15:03"
  }
]
```

---

## 二、视频清单接口

```
GET /api/follow/open/videos
```

### 返回字段说明（data 内每条，共 19 个字段）

| 字段 | 类型 | 说明 |
|---|---|---|
| uperName | string | 博主昵称 |
| douyinNo | string | 博主抖音号（无则为 null） |
| secUid | string | 博主 sec_uid（无则为 null） |
| uperId | string | 博主数字 ID |
| title | string | 视频标题 |
| videoId | string | 视频 ID（aweme_id，抖音侧唯一） |
| playCount | long | 播放数 |
| diggCount | long | 点赞数 |
| commentCount | long | 评论数 |
| shareCount | long | 分享数 |
| collectCount | long | 收藏数 |
| viedoType | string | 来源：dy_favorite 喜欢 / dy_collects 收藏 / dy_follows 关注 / dy_custom_collect 自定义收藏 / dy_mix 合集 / dy_series 短剧 |
| syncTime | string | 同步入抖小云的时间（日期筛选 syncStart/syncEnd 按此字段） |
| createTime | string | 视频发布时间（createStart/createEnd 按此字段） |
| id | string | 库内视频 ID（lanPlayUrl 的键，与 videoId 不同） |
| lanPlayUrl | string | **内网免登录播放直链**（流式，支持进度拖动）；未下载视频为空串 |
| playUrl | string | 抖音播放页链接 `https://www.douyin.com/video/{videoId}` |
| dyUser | string | CK名称（用哪个账号同步的） |
| subtitle | string | 字幕全文（ASR 转写）；未转写为空串 |

### 筛选参数（全部可选、自由组合）

| 参数 | 说明 | 示例 |
|---|---|---|
| uperId | 按博主 ID 精确过滤 | `uperId=95845330308` |
| uperName | 按博主昵称模糊 | `uperName=%E8%80%B3%E7%81%AB`（耳火） |
| keyword | 标题关键词模糊 | `keyword=%E5%BA%95%E5%A6%86`（底妆） |
| syncStart / syncEnd | **同步日期区间**（⚠️闭区间含边界：「9月1日全天」应传 `syncStart=2026-09-01&syncEnd=2026-09-02`） | `syncStart=2026-09-01&syncEnd=2026-09-02` |
| createStart / createEnd | 发布日期区间（格式同上，同样注意闭区间） | `createStart=2026-09-01&createEnd=2026-09-02` |
| minPlay / maxPlay | 播放数区间 | `minPlay=10000` |
| minDigg / maxDigg | 点赞数区间 | `minDigg=1000&maxDigg=50000` |
| minComment / maxComment | 评论数区间 | |
| minShare / maxShare | 分享数区间 | |
| minCollect / maxCollect | 收藏数区间 | |
| viedoType | 来源类型数字 | `1`喜欢 `2`收藏 `3`关注 `5`自定义 `6`合集 `7`短剧 |
| orderBy | 排序（倒序） | `syncTime`（默认）/`createTime`/`digg`/`play`/`collect` |
| pageIndex / pageSize | 分页 | `pageSize` 上限 500；**不传 pageSize = 返回全量** |

### 响应结构

```json
{
  "total": 2252,          // 筛选命中总数（分页时判断拉完的依据）
  "pageIndex": 1,         // 分页时才有，全量模式为 null
  "pageSize": 100,
  "data": [ ... ]
}
```

### 调用示例

**① 全量拉取**（数据几千条时一步到位，约 1.6MB）
```bash
curl http://10.1.10.21:10101/api/follow/open/videos
```

**② 组合筛选：耳火最近一周点赞过千的视频，按点赞排序，取前 20**
```bash
curl "http://10.1.10.21:10101/api/follow/open/videos?uperName=%E8%80%B3%E7%81%AB&syncStart=2026-08-25&minDigg=1000&orderBy=digg&pageSize=20"
```

**③ 增量同步：只拉某天之后新入库的**（推荐给定时任务）
```bash
curl "http://10.1.10.21:10101/api/follow/open/videos?syncStart=2026-09-01%2000:00:00&pageSize=500"
```

**④ 翻页拉全量**（数据量大时）
```python
import requests

API = "http://10.1.10.21:10101/api/follow/open/videos"
page, size, all_rows = 1, 500, []

while True:
    r = requests.get(API, params={"pageIndex": page, "pageSize": size}, timeout=30).json()
    all_rows.extend(r["data"])
    if len(all_rows) >= r["total"] or not r["data"]:
        break
    page += 1

# 按 videoId 去重（翻页期间若有新数据入库，个别行可能跨页重复）
seen, uniq = set(), []
for row in all_rows:
    if row["videoId"] not in seen:
        seen.add(row["videoId"])
        uniq.append(row)

print(f"共 {len(uniq)} 条")
```

**⑤ 拉某一天的完整数据**（即飞书多维表格每天推送的口径）
```bash
curl "http://10.1.10.21:10101/api/follow/open/videos?syncStart=2026-09-01&syncEnd=2026-09-02"
```

**返回示例**（截取，含 2026-09-02 新增的 5 个字段）
```json
{
  "total": 403,
  "pageIndex": null,
  "pageSize": null,
  "data": [
    {
      "uperName": "丹妮Danee【绷带面膜到货啦！】",
      "douyinNo": "61211144413",
      "secUid": "MS4wLjABAAAAkpx7RNV2fPsqQevHSjEQllrJ5Lcq...",
      "uperId": "104698640448",
      "title": "夏季底妆不脱妆的秘诀",
      "videoId": "7289605212971535655",
      "playCount": 123456,
      "diggCount": 15596,
      "commentCount": 890,
      "shareCount": 234,
      "collectCount": 567,
      "viedoType": "dy_follows",
      "syncTime": "2026-09-01 22:15:03",
      "createTime": "2026-08-31 18:00:00",
      "id": "2094806479646490624",
      "lanPlayUrl": "http://10.1.10.21:10101/api/video/play/2094806479646490624",
      "playUrl": "https://www.douyin.com/video/7289605212971535655",
      "dyUser": "测试",
      "subtitle": "夏天底妆总是脱妆？三个技巧..."
    }
  ]
}
```

---

## 三、常见问题

| 问题 | 说明 |
|---|---|
| 中文筛选查不到数据 | 参数必须 UTF-8 URL 编码；用 python requests 默认正确，手拼 URL 注意编码 |
| 按天筛选漏掉当天数据 | syncEnd/createEnd 是**闭区间**（<=）：传 `2026-09-01` 等于 9/1 00:00:00，当天数据全排除。「全天」要传次日 `syncEnd=2026-09-02` |
| secUid / douyinNo 为 null | 极少数历史数据无此值（作者未关注且未回填成功）；新数据 100% 有 |
| playCount 为 0 | 抖音部分列表接口不返回播放数，属数据源限制 |
| lanPlayUrl 为空串 | 该视频未下载到本地（只有元数据）；有文件的才给播放直链 |
| subtitle 为空串 | 该视频未做 ASR 转写（或纯音乐无内容） |
| 带字幕的响应较大 | 全量含 subtitle 可能几 MB；不需要字幕可忽略该字段 |
| 一次拉多少合适 | 全量（几千条）一次拉即可；建议接入方做增量（syncStart）定时拉取 |
| 数据多久更新 | 抖小云每 30 分钟同步一轮；五项统计每天 05:30 回填刷新 |
