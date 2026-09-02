# 抖小云内网数据接口使用指南

> 版本：2026-09-02（第3次更新） ｜ 适用：抖小云 `b3b95f5` 及之后版本
> **主接口只有 1 个**：视频清单接口（博主信息已内嵌在每条视频里，无需单独调用博主接口）
> 接口**免登录**，仅供**可信内网**使用（请勿将端口映射到公网）

- 服务地址：`http://<抖小云IP>:10101`（NAS 部署后即 `http://10.1.10.21:10101`）
- 数据格式：JSON（UTF-8）
- 时间格式：`yyyy-MM-dd HH:mm:ss`；筛选参数里可只传日期 `yyyy-MM-dd`
- **中文参数必须 URL 编码（UTF-8）**，如 `uperName=耳火` → `uperName=%E8%80%B3%E7%81%AB`

---

## 视频清单接口（唯一主接口）

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
| subtitle | string | 字幕全文（ASR 转写）；**默认不返回（空串），withSubtitle=true 才读**；未转写也为空串 |

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
| pageIndex / pageSize | 分页（2026-09-02 起**默认每页 100 条**、按同步时间从近到远；翻页传 `pageIndex`；要全量传 `pageSize=0`，响应大慎用） | `pageSize` 上限 500 |
| withSubtitle | 是否返回字幕全文（默认 false 不带；字幕需逐条读盘，带则变慢，全量模式尤其明显） | `withSubtitle=true` |

### 响应结构

```json
{
  "total": 5406,          // 筛选命中总数（翻页判断拉完的依据：拉够 total 或 data 为空即停）
  "pageIndex": 1,         // 当前页；全量模式（pageSize=0）时为 null
  "pageSize": 100,
  "data": [ ... ]         // 本页数据，按同步时间从近到远（默认 orderBy=syncTime 倒序）
}
```

### 调用示例

**① 默认请求：最新 100 条**（不带参数即第 1 页，从近到远）
```bash
curl http://10.1.10.21:10101/api/follow/open/videos
```

**② 翻页：取后续数据**
```bash
curl "http://10.1.10.21:10101/api/follow/open/videos?pageIndex=2"
curl "http://10.1.10.21:10101/api/follow/open/videos?pageIndex=3"
# 循环翻到 data 为空 或 累计条数 >= total 为止
```

**③ 全量拉取**（⚠️显式传 pageSize=0；响应约 3.7MB，多 人同时拉会更慢，建议用翻页或增量）
```bash
curl "http://10.1.10.21:10101/api/follow/open/videos?pageSize=0"
```

**④ 组合筛选：耳火最近一周点赞过千的视频，按点赞排序，取前 20**
```bash
curl "http://10.1.10.21:10101/api/follow/open/videos?uperName=%E8%80%B3%E7%81%AB&syncStart=2026-08-25&minDigg=1000&orderBy=digg&pageSize=20"
```

**⑤ 增量同步：只拉某天之后新入库的**（推荐给定时任务）
```bash
curl "http://10.1.10.21:10101/api/follow/open/videos?syncStart=2026-09-01%2000:00:00&pageSize=500"
```

**⑥ 翻页拉全量**（数据量大时）
```python
import requests

url = "http://10.1.10.21:10101/api/follow/open/videos"

payload = {}          # 每轮翻页改这里：{'pageIndex': 2, 'pageSize': 500}
headers = {}          # 本接口免登录，无需 Authorization

all_rows, page = [], 1
while True:
    payload = {"pageIndex": page, "pageSize": 500}
    response = requests.request("GET", url, headers=headers, params=payload, timeout=30)
    r = response.json()
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

**⑦ 拉某一天的完整数据**（即飞书多维表格每天推送的口径）
```bash
curl "http://10.1.10.21:10101/api/follow/open/videos?syncStart=2026-09-01&syncEnd=2026-09-02"
```

**⑧ 带字幕全文**（默认不带；逐条读盘，页越大越慢）
```bash
curl "http://10.1.10.21:10101/api/follow/open/videos?withSubtitle=true&pageSize=100"
```

**⑨ Python 标准写法**（url + payload + headers 的通用格式，改造自 TikHub 风格示例）
```python
import requests

# 通用格式：url = 服务地址 + API路径，参数拼在 url 上或放 params
url = "http://10.1.10.21:10101/api/follow/open/videos?syncStart=2026-09-01&syncEnd=2026-09-02"

payload = {}                          # 本接口用 GET + params，无需请求体
headers = {}                          # 免登录接口，无需 Authorization；需要时可加自定义头

response = requests.request("GET", url, headers=headers, data=payload, timeout=30)
print(response.text)                  # 原始 JSON 文本
data = response.json()                # 解析为字典
print(data["total"], "条")
for row in data["data"]:
    print(row["uperName"], row["diggCount"], row["videoId"])
```

**返回示例**（⑦拉某天的口径，截取；subtitle 仅 withSubtitle=true 时非空）
```json
{
  "total": 403,
  "pageIndex": 1,
  "pageSize": 100,
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

## 常见问题

| 问题 | 说明 |
|---|---|
| 中文筛选查不到数据 | 参数必须 UTF-8 URL 编码；用 python requests 默认正确，手拼 URL 注意编码 |
| 按天筛选漏掉当天数据 | syncEnd/createEnd 是**闭区间**（<=）：传 `2026-09-01` 等于 9/1 00:00:00，当天数据全排除。「全天」要传次日 `syncEnd=2026-09-02` |
| secUid / douyinNo 为 null | 极少数历史数据无此值（作者未关注且未回填成功）；新数据 100% 有 |
| playCount 为 0 | 抖音部分列表接口不返回播放数，属数据源限制 |
| lanPlayUrl 为空串 | 该视频未下载到本地（只有元数据）；有文件的才给播放直链 |
| subtitle 为空串 | 该视频未做 ASR 转写（或纯音乐无内容） |
| 带字幕的响应较大 | 字幕默认不返回（快）；`withSubtitle=true` 逐条读盘，页越大越慢（100条约7s），按需开启 |
| 一次拉多少合适 | 全量（几千条）一次拉即可；建议接入方做增量（syncStart）定时拉取 |
| 数据多久更新 | 抖小云每 30 分钟同步一轮；五项统计每天 05:30 回填刷新 |

---

## 附录：博主清单接口（补充）

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

url = "http://10.1.10.21:10101/api/follow/open/uperids"

payload = {}
headers = {}

response = requests.request("GET", url, headers=headers, data=payload, timeout=10)
data = response.json()   # 注意：本接口返回纯数组，没有 total 包装
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
