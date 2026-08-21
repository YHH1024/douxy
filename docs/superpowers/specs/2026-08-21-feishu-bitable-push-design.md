# 抖小云同步数据定时推送飞书多维表格 — 设计文档

> 日期：2026-08-21 ｜ 状态：已确认（用户逐段批准）
> 背景：dysync 每天同步抖音视频数据（含统计与 ASR 字幕），目前只能手动「导出Excel」。目标：每天定时把当天同步记录自动推送到飞书多维表格，并在飞书群里通知推送结果。

## 1. 已确认决策

| 决策点 | 结论 |
|---|---|
| 表格形态 | **每月一个 Base**（如「抖小云同步数据-2026年8月」），Base 内**每天一张数据表**（如「8月21日」） |
| 推送内容 | 当天**全部同步记录**，13 列与现有 Excel 导出一致（含 ASR 字幕全文列，无字幕留空） |
| 运行位置 | **dysync 容器内**：.NET Quartz 每日定时任务，直连飞书开放 API（手写 HTTP，零 NuGet 依赖，同 SimpleXlsxBuilder 思路） |
| 认证 | 飞书**自建应用**（app_id/app_secret → tenant_access_token），用户尚未创建（见 §8 准备清单） |
| 幂等策略 | **清空重写**：当天表存在则清空全部记录后重写，重复推送不产生重复行 |
| 通知 | 飞书**群机器人 webhook**：成功发「N 条 → 表格链接」，失败发原因 |
| 文件位置 | 可选配置 `FeishuFolderToken`，月度 Base 建到指定文件夹；不填建到应用根空间 |
| 规模 | 每天约 1k 条；企业版单表 5 万行上限，余量充足 |

## 2. 架构总览

```
Quartz 每日任务（默认 23:50，cron 可配）
   │
   ├─ ① 读数据
   │    ├─ sqlite 查当天同步记录（复用 ExportTodayExcel 同款查询）
   │    └─ 每条记录读视频旁 .txt 字幕全文（复用 ReadSubtitleTextAsync，>32000 截断）
   │
   ├─ ② FeishuBitableService（新，service/FeishuBitableService.cs）
   │    ├─ tenant_access_token 获取 + 内存缓存（2h TTL，提前 5min 刷新）
   │    ├─ 定位本月 Base（配置缓存 app_token + 月份；月份变了 → 建新 Base + 加协作者）
   │    ├─ 定位今日表（不存在 → 建表含 13 字段；存在 → 批量删除全部记录）
   │    └─ 批量写入：200 条/批串行，批间 ~300ms，限流(1254291)退避重试 3 次
   │
   └─ ③ 通知（FeishuNotifyService，群机器人 webhook）
        ├─ 成功：「8月21日抖小云同步数据已推送 N 条 → <Base链接>」
        └─ 失败：「8月21日推送失败：<原因>」
```

手动补偿：设置页「立即推送今天」按钮 → `POST /api/feishu/push/today`，走同一逻辑（幂等，点多次安全）。

## 3. 配置项（AppConfig 新增）

| 字段 | 说明 | 必填 |
|---|---|---|
| `FeishuPushEnabled` | 总开关（默认关，配好再开） | 是 |
| `FeishuAppId` / `FeishuAppSecret` | 自建应用凭证 | 是 |
| `FeishuUserEmail` | 你的飞书邮箱，新建 Base 后自动加为协作者 | 是 |
| `FeishuNotifyWebhook` | 群机器人 webhook URL（空则跳过通知） | 否 |
| `FeishuFolderToken` | 目标文件夹 token（空则建到应用根空间） | 否 |
| `FeishuPushCron` | 推送时刻 cron（默认 `0 50 23 * * ?`，即 23:50） | 是 |
| `FeishuBaseTokenCache` / `FeishuBaseMonthCache` | 运行时缓存：本月 Base token 与月份（不写设置页，程序自管理） | — |

设置页新增「飞书推送」区块：上述表单项 + 「立即推送今天」按钮 + 上次推送结果展示（时间/条数/成败）。

app_secret 存 sqlite 明文（与其他配置同级，自托管可接受）。

## 4. 数据表结构（13 列）

| 列 | Bitable 字段类型 | 备注 |
|---|---|---|
| 同步时间 | 日期时间（毫秒时间戳） | `yyyy/M/d HH:mm` 显示 |
| 发布时间 | 日期时间 | 同上 |
| 同步类型 | 单选 | 喜欢/收藏/关注/合集/短剧/自定义收藏夹 |
| 博主 | 文本 | |
| 视频类型 | 单选 | 视频/图文 |
| 视频标题 | 文本 | |
| CK名称 | 单选 | 便于按账号筛选 |
| 播放 | 数字 | 原始值（不缩写，筛选排序用） |
| 点赞 | 数字 | |
| 评论 | 数字 | |
| 分享 | 数字 | |
| 收藏 | 数字 | |
| 字幕全文 | 文本（多行） | >32000 字符截断，与 Excel 导出一致 |

## 5. 关键流程细节

### 5.1 月度 Base 定位与创建

```
if (缓存月份 == 当前月份 && 缓存 token 有效) → 直接用
else:
   POST /open-apis/bitable/v1/apps  {name:"抖小云同步数据-yyyy年M月", folder_token?}
   POST /open-apis/drive/v1/permissions/{token}/members?type=bitable
        {member_type:"email", member_id: FeishuUserEmail, perm:"edit"}
   更新缓存（token + 月份）
```

月初首次推送自动滚动，无需人工干预。

### 5.2 每日表定位与清空重写

```
tables = GET /apps/{token}/tables          # 按名字找「M月d日」
不存在 → POST /tables（含 13 字段定义）
存在   → GET records 分页拿 record_id → POST records/batch_delete 清空
```

### 5.3 批量写入

- 200 条/批（保守值，避开 `1254104`），串行，批间 300ms
- `1254291`（并发写冲突）→ 等 1s/2s/4s 退避重试，最多 3 次
- 1k 条/天 ≈ 5 批，秒级完成，远低于 ~10 QPS 限制
- 企业版单表 5 万行；每天一表 1k 行，无上限风险

### 5.4 通知

- webhook 空 → 静默跳过
- 消息体：`{"msg_type":"text","content":{"text":"8月21日抖小云同步数据已推送 1024 条 → https://...feishu.cn/base/xxx"}}`
- 失败同样通知（含异常摘要），避免静默坏掉

### 5.5 token 管理

- `POST /open-apis/auth/v3/tenant_access_token/internal` → 缓存（expire-300s）
- 业务调用遇 token 失效错误码 → 强制刷新重试 1 次

## 6. 错误处理

| 场景 | 处理 |
|---|---|
| 飞书 API 任意一步失败 | 记 serilog（含 Base/表/批次上下文）+ webhook 发失败通知 |
| 限流 1254291 | 退避重试（见 5.3） |
| token 过期 | 自动刷新重试 1 次 |
| 加协作者失败 | 不阻断推送，仅告警（Base 已建，链接可用应用身份访问；用户手动共享即可） |
| 当天 0 条记录 | 正常建空表 + 通知「0 条」，视为成功 |
| 网络不可达（NAS→open.feishu.cn） | 失败通知走不了 webhook 时仅记日志；部署验收需覆盖此检查 |

## 7. 测试与验证

- 无单测（依赖飞书真接口），走实测验证：
  1. 设置页填好配置 → 「立即推送今天」→ 飞书里确认：Base 名称、表名、13 列类型、行数 = 当天同步数、字幕列内容正确
  2. 再点一次 → 行数不变（幂等）
  3. 群消息收到「N 条 → 链接」
  4. 推送后用 lark-cli `base +record-list` 抽查 3 条交叉验证
  5. 改系统日期不可行 → 月初滚动逻辑以「缓存月份≠当前月份」单测式手动验证（改缓存字段模拟）

## 8. 用户一次性准备清单

1. 飞书开发者后台 → 创建企业自建应用「抖小云推送」
2. 权限：开通 **Bitable 读写**（bitable:app:read / bitable:app:write）+ **云文档权限管理**（drive:drive:permission）→ 创建版本并发布
3. 拿 app_id / app_secret 填进抖小云设置页
4. 云文档新建文件夹「抖小云数据」→ 共享给该应用（可编辑）→ URL 取 folder_token 填入（可选）
5. 飞书群 → 群机器人 → 自定义机器人 → 复制 webhook 填入
6. 开启总开关，点「立即推送今天」验收

（实施时附带截图位置的详细操作文档 `docs/feishu-app-setup.md`。）

## 9. 部署注意

- 本功能在 dysync 容器内运行，**NAS 容器必须能访问 `open.feishu.cn`（443）**；部署验收加一条：容器内 curl 该域名 200
- 镜像更新走现行流程：publish → docker cp/commit → `up -d --force-recreate`（见 dysync-deployment 记忆）
- ASR 分离部署与本功能无关（本功能不碰 ASR 服务，只读已落盘的 .txt）

## 10. 范围外（YAGNI）

- 增量追加 / upsert（清空重写已满足，追加留给将来真有需要时）
- 仪表盘、图表、公式字段（用户在飞书侧自行配置即可）
- 历史数据回填到飞书（如需，后续加 backfill 端点，复用同一写入服务）
- 对字幕内容做 AI 分析/摘要（系统内无 LLM，且非本次目标）
- 多用户/多群的精细化权限与通知路由
