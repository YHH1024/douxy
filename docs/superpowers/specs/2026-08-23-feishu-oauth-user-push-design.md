# 飞书 OAuth 用户授权推送 — 设计文档

日期：2026-08-23
状态：已确认（用户批准设计，四项决策均为推荐项）

## 背景与目标

当前飞书推送以**应用身份**（tenant_access_token）执行，Base 建在应用自己的空间里，
用户只能通过「组织内链接可编辑」访问（个人版飞书无法给用户文件夹加应用协作者，
1254701 DriveNodePermNotAllow，已实测）。

用户诉求：**多维表格直接建在自己账号的文件夹里**，我的空间直接可见、归属本人。

方案：接入飞书 OAuth 用户授权（authorization_code 流程），推送改以**用户身份**
（user_access_token）执行。授权一次后全自动；用户身份不可用时回落应用身份（向后兼容）。

## 已确认的决策

| 决策点 | 选择 |
|---|---|
| 授权方式 | 自动回调（配置重定向 URL，浏览器一键授权） |
| Base 位置 | 用户现有文件夹（设置页 FolderToken 填用户自己的文件夹 token） |
| 授权失效处理 | 手动重授权（不做心跳保活；每日推送使 refresh_token 自然滚动续期） |
| 存量数据 | 切换后重建（授权成功清 Base 缓存，下次推送在用户文件夹重建；删应用空间旧文件夹/Base） |

## 飞书 OAuth 机制要点（已实测/查证）

- **token 端点**：`POST https://open.feishu.cn/open-apis/authen/v2/oauth/token`（v2，兼容 JSON；
  v3 为 `https://accounts.feishu.cn/oauth/v3/token`，请求/响应结构一致——用 v2 即可，
  容器内域名连通性与 tenant token 一致）
- **grant_type=authorization_code**：参数 client_id/client_secret/code/redirect_uri
- **grant_type=refresh_token**：参数 client_id/client_secret/refresh_token
- **access_token**：约 7200s（以响应 expires_in 为准，勿硬编码）
- **refresh_token**：约 604800s=7 天，**一次性使用**——每次刷新返回新的 refresh_token，
  必须立即落库，旧即刻作废
- **offline_access scope**：不申请则不返回 refresh_token（必须申请）
- **code**：5 分钟有效、一次性
- **重定向 URL**：必须先在开发者后台→安全设置→重定向 URL 配置（精确匹配，可配多个）
- **token 长度**：1~2KB（可能更长），存储列宽预留 4000
- 响应非飞书通用信封：code=0 在顶层，access_token/refresh_token 直接在顶层（类似 tenant token 的扁平结构）

## 授权流程

```
设置页「授权飞书账号」按钮（新窗口）
  → GET /api/feishu/oauth/url 返回授权链接（或前端直接拼）
  → 用户在飞书授权页点「同意」
  → 飞书 302 到 http://<host>:10101/api/feishu/oauth/callback?code=xxx
  → 后端：code 换 token（含 offline_access）→ 4 个新配置列落库
       同时清 FeishuBaseTokenCache/MonthCache（触发下次推送在用户文件夹重建）
  → 返回简单 HTML「授权成功」（无需跳转回设置页）
```

授权链接形如：
`https://open.feishu.cn/open-apis/authen/v1/index?app_id=<id>&redirect_uri=<enc>&scope=bitable:app%20drive:drive%20offline_access&state=<rand>`

用户一次性操作：开发者后台 → 安全设置 → 重定向 URL 添加
`http://localhost:10101/api/feishu/oauth/callback`（如需从局域网其他设备授权，
再加对应 `http://<LAN-IP>:10101/...`，支持多条）。

## 架构改动

### AppConfig 新增 4 列（CodeFirst 自动建列，程序自管）

| 列 | 类型/宽 | 说明 |
|---|---|---|
| FeishuUserAccessToken | string(4000) | user_access_token |
| FeishuUserRefreshToken | string(4000) | refresh_token（一次性，最新值） |
| FeishuUserTokenExpiresAt | DateTime? | access_token 过期时刻 |
| FeishuUserRefreshExpiresAt | DateTime? | refresh_token 过期时刻 |

**必须同步加进 `ConfigController.UpdateConfig` 的回填清单**（现在已有 4 项：
BaseTokenCache/BaseMonthCache/LastPushResult/AutoFolderToken，扩到 8 项）——
否则设置页保存会冲掉授权（本坑已实际踩过两次）。

### FeishuBitableService：token 层改造

- `AuthedClientAsync` 改为：**有用户 token → 用用户身份；无 → tenant token（现行为）**
- 新增 `GetUserAccessTokenAsync(config)`：
  - 未过期（留 5 分钟余量）→ 直接用
  - 过期但 refresh_token 未过期 → 调刷新端点 → 新 token + **新 refresh_token 立即落库**
  - refresh_token 也过期/刷新失败 → 抛出明确异常「飞书授权已过期,请到设置页重新授权」
    （不静默回落应用身份——避免文件写错位置用户不知道）
- 刷新落库同样走 UpdateConfig（绕过 Controller，直接 commonService）

### 建 Base 逻辑（身份相关分支）

用户身份模式（UserAccessToken 存在）：
- Base 建在 `config.FeishuFolderToken`（用户自己的文件夹）——**未填则报配置错误**，
  提示「用户授权模式下必须在设置页填写文件夹Token」（不自动建应用文件夹、不建用户根空间）
- **跳过**：链接分享（不需要，归属本人）、加协作者（本人）、EnsureAutoFolderAsync（应用文件夹无关）

应用身份模式（无用户 token，回落）：
- 完整保留现状：EnsureAutoFolderAsync + tenant_editable 链接分享

### FeishuController 新增端点

| 端点 | 说明 |
|---|---|
| GET /api/feishu/oauth/url | 返回 {url}：构造好的授权链接（state 随机） |
| GET /api/feishu/oauth/callback | code 换 token、落库、清 Base 缓存，返回 HTML 结果页（成功/失败+原因）。**AllowAnonymous**（飞书重定向不带 JWT） |
| GET /api/feishu/status（扩展） | 增加 oauth 字段：{authorized, userTokenExpiresAt, refreshExpiresAt} |

state 校验：callback 校验 state 与发起时一致（内存 Map，5 分钟 TTL）防 CSRF。
简化处理：state 为随机串存内存字典；容器重启丢字典属可接受（授权流程本就是交互式的）。

### 前端 AppSet.vue 飞书区

- 「授权飞书账号」按钮：调 /oauth/url → window.open(url)
- 授权状态行：已授权（access 到期时间 / refresh 到期时间）或「未授权（推送将以应用身份执行）」
- 按钮旁提示文案：首次使用需在飞书开发者后台→安全设置添加重定向 URL（给出确切 URL 值方便复制）

### 通知（FeishuNotifyService）

推送失败且原因是「授权已过期」时，群通知文案带上「请到设置页重新授权」。
（webhook 通知本身用 tenant/无鉴权 webhook，不受用户 token 影响。）

## 切换与数据迁移

1. 部署新版本后，用户操作：后台配重定向 URL → 设置页填 FolderToken（自己文件夹）→ 点「授权」
2. 授权成功回调里自动清 Base 缓存
3. 手动点「立即推送」或等 23:50 → 当月 Base 建在用户文件夹（归属=用户）
4. 验证后**删除应用空间旧数据**：旧专属文件夹 `JulCfSkJolMwuxdCPKNcM37UnCg`
   及其下 8月 Base `LVMAbgoqSaYMWysKxVIczawNnth`（DELETE drive/v1/files/{token}?type=...，
   已验证过该 API）。DB 里 FeishuAutoFolderToken 顺手清空。
5. 用户 FolderToken 恢复填写原值 `JQcsfjNxel3SsYdpmAscGM3FnZf`（原「抖小云数据」文件夹）

## 错误处理汇总

| 场景 | 行为 |
|---|---|
| code 无效/过期（>5min 或已用） | 回调页显示「授权码已失效,请重新点击授权按钮」 |
| 用户在授权页点取消 | 回调页显示「已取消授权」，无落库无副作用 |
| state 不匹配 | 回调页显示「非法请求」，不换 token |
| 推送时 access 过期 | 自动刷新（对用户透明） |
| refresh 过期（>7 天未推送/用户取消授权/改密） | 推送失败，LastPushResult 与群通知均提示「请重新授权」；不回落应用身份 |
| FolderToken 未填（用户身份模式） | 推送失败提示「请填写文件夹Token」；不自动建文件夹 |
| 授权成功但推送失败（权限不足等） | 常规错误路径，LastPushResult 展示飞书原始 code/msg |

## 测试计划

1. 单元级：token 刷新分支（未过期/需刷新/refresh 过期）——手工触发为主（项目无测试基建）
2. E2E（按已确认决策执行）：
   - 后台配重定向 URL → 授权 → 检查 4 列落库 + Base 缓存已清
   - 手动推送 → Base 出现在用户文件夹、owner=用户、表 13 列、行数据正确（API 读回）
   - 二次推送幂等
   - 无 FolderToken 时报配置错误（清空试一次再填回）
   - 删除旧应用空间数据
3. 回落验证：清掉用户 token 列 → 推送走应用身份（现行为不回归）

## 明确不做（YAGNI）

- 心跳保活任务（每日推送已自然续期）
- 多用户授权（单用户场景）
- PKCE（Confidential Client + client_secret 足够）
- v3 token 端点迁移（v2 兼容 JSON 且结构一致）
- 授权链接二维码/飞书内打开（浏览器打开即可）
