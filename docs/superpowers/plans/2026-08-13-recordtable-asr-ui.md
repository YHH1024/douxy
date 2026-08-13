# 同步记录页 ASR 字幕 UI 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在同步记录页（`RecordTable.vue`）操作列加「生成字幕」按钮、加批量生成、加字幕状态列、加右侧抽屉展示字幕结果，全部复用已有后端接口与 coreapi 封装，零后端改动。

**Architecture:** 纯前端单文件改动。新增模板片段（操作列按钮、批量按钮、状态列 cell、结果抽屉）+ 新增 script 状态与方法。所有 ASR 调用走 `useApiStore()` 的 `GenerateSubtitle` / `GenerateSubtitleBatch` / `GetSubtitleContent`，沿用文件内现有的 `.then(res => res.code === 0 ...)` 调用风格。

**Tech Stack:** Vue 3 `<script setup>` + TypeScript、ant-design-vue（`a-table`/`a-drawer`/`a-tag`/`a-button`/`message`/`Modal`）、@ant-design/icons-vue、Pinia store（`useApiStore`）。

## Global Constraints

- 只改 `app/src/pages/workplace/RecordTable.vue` 一个文件；不改后端、不改 coreapi、不改路由。
- 不引入新依赖；图标用 `@ant-design/icons-vue`（`CopyOutlined` 文件内已 import，其余复用已 import 的）。
- 调用 ASR API 必须用 `useApiStore().GenerateSubtitle(...)` 形式（与文件内 `useApiStore().ReDownViedos(...)` 一致），判定成功用 `res.code === 0`。
- 编码安全：所有新增中文文案直接写中文（文件已是 UTF-8 干净版，`vue-tsc`/`vite build` 会校验）；不要从 git 历史或 GitHub 拷贝含乱码的旧版本。
- 部署遵循 [[dysync-deployment]]：`docker cp` 目标路径**不带尾斜杠**避免嵌套 dist/dist；改完镜像必须 `docker compose up -d --force-recreate`（restart 不加载新镜像）。

## File Structure

- **Modify:** `app/src/pages/workplace/RecordTable.vue`
  - 责任：同步记录列表页。本次在「操作列」「批量按钮区」「columns 定义」「#bodyCell 模板」「script 状态与方法」几处增量改动，并在模板末尾加结果抽屉。集中改动、不大面积重排。

## Interfaces（已有，本次复用，签名以 coreapi.ts 为准）

- `useApiStore().GenerateSubtitle(vid: string, overwrite = false): Promise<Response<any>>` — 调 `GET /api/video/asr/{vid}?overwrite=`，成功 `res.code===0`，`res.data = { subtitlePath, message }`。
- `useApiStore().GenerateSubtitleBatch(param: { ids: string[] }, overwrite = false): Promise<Response<any>>` — 调 `POST /api/video/asr/batch?overwrite=`。
- `useApiStore().GetSubtitleContent(vid: string): Promise<Response<any>>` — 调 `GET /api/video/asr/content/{vid}`，成功 `res.data = { subtitlePath, subtitleCreateTime, statusMessage, content }`。
- 视频行字段：`id`、`videoTitle`、`subtitleSavePath?`、`subtitleStatusMsg?`、`subtitleCreateTime?`（列表接口返回完整实体，字段已带）。

---

### Task 1: script 增量——状态、类型、字幕列、方法

**Files:**
- Modify: `app/src/pages/workplace/RecordTable.vue`（script setup 区，约 200-360 行的 DataItem / columns，及 870 行后新增方法）

**Interfaces:**
- Produces: `subtitleStatusOf(record)`、`handleGenerateSubtitle(record)`、`handleGenerateSubtitleBatch()`、`handleViewSubtitle(record)`、`copySubtitlePath(path)` 供 Task 2 模板调用；ref：`generatingId`、`subtitleDrawerVisible`、`subtitleDrawerLoading`、`subtitleContent`、`generatingBatch`。

- [ ] **Step 1: 在 DataItem 接口补字幕字段**

定位 `interface DataItem {`（约 202 行），在 `isMergeVideo?: boolean;` 后、闭合 `}` 前，插入：

```ts
  // ASR 字幕相关（后端列表已返回）
  subtitleSavePath?: string;       // 非空=已生成
  subtitleStatusMsg?: string;      // 失败原因
  subtitleCreateTime?: string;     // 生成时间
```

- [ ] **Step 2: 在 columns 数组插入「字幕」列**

定位 `const columns = ref([`（约 279 行）内「CK名称」对象之后、「操作」对象之前，插入：

```ts
  {
    title: '字幕',
    dataIndex: 'subtitle',
    align: 'center',
    width: 100,
  },
```

- [ ] **Step 3: 新增字幕相关 ref**

定位批量操作相关状态区（`const isBatchMode = ref(false);` 附近，约 243 行），在其后新增：

```ts
// -------------------------- ASR 字幕相关状态 --------------------------
const generatingId = ref<string>('');        // 当前单条生成中的视频 id（按钮 loading + 状态列"转换中"）
const generatingBatch = ref(false);          // 批量生成中
const subtitleDrawerVisible = ref(false);    // 结果抽屉
const subtitleDrawerLoading = ref(false);
const subtitleContent = ref<{
  content?: string;
  subtitleCreateTime?: string;
  subtitlePath?: string;
}>({});

/** 计算某行字幕状态，供模板 a-tag 使用 */
const subtitleStatusOf = (record: DataItem): 'unprocessed' | 'processing' | 'done' | 'error' => {
  if (generatingId.value && generatingId.value === record.id) return 'processing';
  if (record.subtitleSavePath) return 'done';
  if (record.subtitleStatusMsg) return 'error';
  return 'unprocessed';
};
```

- [ ] **Step 4: 新增单条生成方法 `handleGenerateSubtitle`**

定位批量事件区末尾（`deleteBatch` 函数定义之后，约 893 行后）新增：

```ts
// -------------------------- ASR 字幕：单条生成 --------------------------
const handleGenerateSubtitle = (record: DataItem) => {
  if (!record.id) {
    message.warning('视频信息缺失');
    return;
  }
  const hasSubtitle = !!record.subtitleSavePath;
  const doGen = (overwrite: boolean) => {
    generatingId.value = record.id!;
    useApiStore()
      .GenerateSubtitle(record.id!, overwrite)
      .then((res) => {
        generatingId.value = '';
        if (res.code === 0) {
          message.success('字幕生成成功');
          GetRecords();                 // 刷新表格，拉取最新 subtitleSavePath/状态
          handleViewSubtitle(record);   // 自动打开抽屉看结果
        } else {
          message.error(res.message || record.subtitleStatusMsg || '字幕生成失败');
        }
      })
      .catch((err) => {
        generatingId.value = '';
        message.error('字幕生成失败，请稍后重试');
        console.error('生成字幕失败：', err);
      });
  };

  if (hasSubtitle) {
    Modal.confirm({
      title: '字幕已存在',
      content: '该视频已有字幕，是否重新生成并覆盖？',
      okText: '覆盖生成',
      cancelText: '取消',
      onOk: () => doGen(true),
    });
  } else {
    doGen(false);
  }
};
```

- [ ] **Step 5: 新增查看字幕方法 `handleViewSubtitle` 与 `copySubtitlePath`**

紧接上一步之后新增：

```ts
// -------------------------- ASR 字幕：查看内容 --------------------------
const handleViewSubtitle = (record: DataItem) => {
  if (!record.id) {
    message.warning('视频信息缺失');
    return;
  }
  subtitleDrawerVisible.value = true;
  subtitleDrawerLoading.value = true;
  subtitleContent.value = {};
  useApiStore()
    .GetSubtitleContent(record.id!)
    .then((res) => {
      subtitleDrawerLoading.value = false;
      if (res.code === 0) {
        subtitleContent.value = {
          content: res.data?.content || '',
          subtitleCreateTime: res.data?.subtitleCreateTime || record.subtitleCreateTime || '',
          subtitlePath: res.data?.subtitlePath || record.subtitleSavePath || '',
        };
      } else {
        message.error(res.message || '暂无字幕内容');
        subtitleDrawerVisible.value = false;
      }
    })
    .catch((err) => {
      subtitleDrawerLoading.value = false;
      subtitleDrawerVisible.value = false;
      message.error('加载字幕内容失败');
      console.error('加载字幕失败：', err);
    });
};

const copySubtitlePath = (path?: string) => {
  if (!path) return;
  const text = (path as string).replace(/\\/g, '/');
  navigator.clipboard?.writeText(text).then(
    () => message.success('路径已复制'),
    () => message.warning('复制失败，请手动复制')
  );
};
```

- [ ] **Step 6: 新增批量生成方法 `handleGenerateSubtitleBatch`**

紧接上一步之后新增：

```ts
// -------------------------- ASR 字幕：批量生成 --------------------------
const handleGenerateSubtitleBatch = () => {
  if (selectedRowKeys.value.length === 0) {
    message.warning('请先选择要生成字幕的视频');
    return;
  }
  const ids = selectedRowKeys.value as string[];
  const alreadyCount = dataSource.value.filter(
    (r: DataItem) => ids.includes(r.id || '') && r.subtitleSavePath
  ).length;

  Modal.confirm({
    title: '确认批量生成字幕',
    content:
      `您确定要为选中的 ${ids.length} 条视频生成字幕吗？` +
      (alreadyCount > 0 ? `（其中 ${alreadyCount} 条已有字幕，将覆盖）` : ''),
    okText: '确认生成',
    cancelText: '取消',
    onOk: () => {
      generatingBatch.value = true;
      useApiStore()
        .GenerateSubtitleBatch({ ids }, true)
        .then((res) => {
          generatingBatch.value = false;
          if (res.code === 0) {
            message.success('批量字幕生成完成');
            GetRecords();
            selectedRowKeys.value = [];
          } else {
            message.error(res.message || '批量生成失败');
          }
        })
        .catch((err) => {
          generatingBatch.value = false;
          message.error('批量生成失败，请稍后重试');
          console.error('批量生成字幕失败：', err);
        });
    },
  });
};
```

> 注：`dataSource` 是文件内已有的表格数据 ref（GetRecords 填充），`selectedRowKeys` 已存在。批量 `overwrite=true` 覆盖，confirm 文案已提示。

- [ ] **Step 7: 验证类型与引用无误（不 build，仅自查）**

人工核对：`useApiStore` 已 import（185 行）；`message`、`Modal` 已 import（189 行）；`DataItem`、`GetRecords`、`dataSource`、`selectedRowKeys` 均为文件内已有标识符。无未定义引用。

- [ ] **Step 8: 暂不单独提交（与 Task 2 一起提交，避免半成品）**

本任务无独立可测产物（模板还没引用这些方法），合到 Task 2 后整体提交。

---

### Task 2: 模板增量——操作列按钮、批量按钮、状态列、结果抽屉

**Files:**
- Modify: `app/src/pages/workplace/RecordTable.vue`（template 区）

**Interfaces:**
- Consumes: Task 1 产出的全部方法与 ref。

- [ ] **Step 1: 操作列 `#bodyCell` 增加字幕状态 cell 与操作按钮**

定位 `<template v-if="column.key === 'operation'">`（约 162 行），在其**之前**插入字幕状态列渲染分支：

```html
        <template v-if="column.dataIndex === 'subtitle'">
          <a-tag v-if="subtitleStatusOf(record) === 'processing'" color="processing">转换中</a-tag>
          <a-tag v-else-if="subtitleStatusOf(record) === 'done'" color="success">已生成</a-tag>
          <a-tooltip v-else-if="subtitleStatusOf(record) === 'error'" :title="record.subtitleStatusMsg || '生成失败'">
            <a-tag color="error">失败</a-tag>
          </a-tooltip>
          <a-tag v-else color="default">未生成</a-tag>
        </template>
```

然后在该 `operation` 分支的 `<a-space>` 内，在「重新同步」按钮**之后**、「分享」按钮**之前**，插入字幕按钮（生成 / 重新生成 + 查看两条）：

```html
            <a-button
              type="link"
              :loading="generatingId === record.id"
              :disabled="generatingBatch"
              @click="handleGenerateSubtitle(record)"
            >
              {{ record.subtitleSavePath ? '重新生成字幕' : '生成字幕' }}
            </a-button>
            <a-button
              v-if="record.subtitleSavePath"
              type="link"
              @click="handleViewSubtitle(record)"
            >
              查看字幕
            </a-button>
```

- [ ] **Step 2: 批量按钮区增加「批量生成字幕」**

定位批量按钮 `<a-space class="button-group">`（约 52 行），在「永久删除」按钮**之后**、`</a-space>` 之前插入：

```html
              <a-button
                type="primary"
                :loading="generatingBatch"
                @click="handleGenerateSubtitleBatch"
                v-if="isBatchMode"
                :disabled="selectedRowKeys.length === 0"
              >
                批量生成字幕
              </a-button>
```

- [ ] **Step 3: 模板末尾增加结果抽屉**

定位文件内已有的「已删除视频-抽屉」`<a-drawer ...>` 之后（约 102 行 `</a-drawer>` 之后），插入结果抽屉：

```html
    <!-- 字幕内容抽屉 -->
    <a-drawer
      title="字幕内容"
      placement="right"
      :width="520"
      :visible="subtitleDrawerVisible"
      @close="subtitleDrawerVisible = false"
    >
      <a-spin :spinning="subtitleDrawerLoading">
        <div v-if="subtitleContent.content">
          <p><strong>生成时间：</strong>{{ subtitleContent.subtitleCreateTime || '-' }}</p>
          <p>
            <strong>字幕路径：</strong>
            <span style="word-break: break-all">{{ subtitleContent.subtitlePath || '-' }}</span>
            <a-button type="link" size="small" @click="copySubtitlePath(subtitleContent.subtitlePath)">
              <CopyOutlined /> 复制
            </a-button>
          </p>
          <a-divider />
          <pre class="subtitle-content">{{ subtitleContent.content }}</pre>
        </div>
        <a-empty v-else-if="!subtitleDrawerLoading" description="暂无字幕内容" />
      </a-spin>
    </a-drawer>
```

- [ ] **Step 4: 确认 `CopyOutlined`、`a-divider`、`a-empty` 可用**

`CopyOutlined` 已在 import 块（197 行）import。`a-divider`/`a-empty` 是 ant-design-vue 全局组件（项目已全局注册 antd，无需显式 import；同文件已用 `<a-list>`/`<a-divider>` 等无需 import）。无需新增 import。

- [ ] **Step 5: 本地类型检查 + 构建**

Run:
```bash
cd /d/dysync/dysync.net/app && npm run build
```
Expected: `vue-tsc --noEmit` 通过无类型错误；`vite build` 输出 `✓ built in ...s`。若 vue-tsc 报 `dataSource`/`selectedRowKeys` 类型错，回查 Step 6 的类型注解。

- [ ] **Step 6: 验证新构建产物含 ASR UI 且编码干净**

Run:
```bash
cd /d/dysync/dysync.net/app
echo "ASR按钮文案命中:"; grep -oE "生成字幕|查看字幕|重新生成字幕|批量生成字幕" dist/assets/RecordTable*.js | sort | uniq -c
echo "乱码检查(应空):"; grep -oE "鍚|鏃|璇|鍙|鍗|瑙" dist/assets/RecordTable*.js | sort | uniq -c
```
Expected: 4 种文案各有命中；乱码检查无输出。

- [ ] **Step 7: 提交源码改动**

```bash
cd /d/dysync/dysync.net
git add app/src/pages/workplace/RecordTable.vue
git commit -m "$(cat <<'EOF'
feat: 同步记录页增加 ASR 字幕生成/查看 UI

操作列加「生成字幕/重新生成字幕」按钮（同步等待，成功自动开抽屉看结果）；
批量模式加「批量生成字幕」；表格加「字幕」状态列（未生成/转换中/已生成/失败）；
右侧抽屉展示字幕全文+生成时间+路径+复制。全部复用已有后端 ASR 接口与 coreapi。

Co-Authored-By: Claude <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: 部署到运行容器并端到端验证

**Files:**
- 无源码改动；操作 docker。

- [ ] **Step 1: docker cp 新 dist 进运行容器（目标路径不带尾斜杠）**

Run:
```bash
docker exec dysync2026 sh -c 'rm -rf /app/app/dist/assets /app/app/dist/index.html'
docker cp /d/dysync/dysync.net/app/dist/. dysync2026:/app/app/dist
```
Expected: 两条均无报错。

- [ ] **Step 2: 验证容器内 dist 非嵌套且含 ASR**

Run:
```bash
docker exec dysync2026 sh -c 'ls /app/app/dist/assets/RecordTable*.js; echo "---不在嵌套层---"; ls /app/app/dist/dist 2>/dev/null && echo "❌嵌套了" || echo "✅无嵌套"; echo "---ASR命中---"; grep -oE "生成字幕|查看字幕" /app/app/dist/assets/RecordTable*.js | sort | uniq -c'
```
Expected: 列出 RecordTable chunk；「✅无嵌套」；ASR 文案有命中。若出现「❌嵌套了」，删掉嵌套层 `docker exec dysync2026 sh -c 'cd /app/app/dist && mv dist/* ./ && rmdir dist'`。

- [ ] **Step 3: docker commit 覆盖镜像**

Run:
```bash
docker commit -m "feat: RecordTable ASR UI (生成/查看/批量/状态列)" dysync2026 dysync:asr-local
```
Expected: 输出新 `sha256:...`。

- [ ] **Step 4: force-recreate 加载新镜像**

Run:
```bash
cd /d/dysync && docker compose up -d --force-recreate
sleep 5
# 确认容器镜像 == asr-local
docker inspect dysync2026 --format '{{.Image}}'
docker inspect dysync:asr-local --format '{{.Id}}'
```
Expected: 两条 ID 相同。

- [ ] **Step 5: 端到端验证（带鉴权）**

Run（确认本地 ASR 服务已在 8000 跑，PID 可用之前的）:
```bash
TOKEN=$(curl -s -X POST "http://localhost:10101/api/Auth/Login" -H 'Content-Type: application/json' -d '{"UserName":"douyin","Password":"douyin2026"}' | python -c "import sys,json;print(json.load(sys.stdin).get('token',''))")
echo "token len: ${#TOKEN}"
# 前端可达
curl -s -o /dev/null -w "前端 HTTP %{http_code}\n" http://localhost:10101/
# ASR 端点仍在（401=路由在需鉴权）
curl -s -o /dev/null -w "asr/health HTTP %{http_code}\n" "http://localhost:10101/api/config/asr/health?serviceUrl=http://host.docker.internal:8000" -H "Authorization: Bearer $TOKEN"
```
Expected: token len 约 333；前端 HTTP 200；asr/health HTTP 200 且返回 `available:true`。

- [ ] **Step 6: 人工页面验证清单**

浏览器打开 `http://localhost:10101/#/workplace`（强刷 Ctrl+F5），逐项确认：
1. 表格出现「字幕」列（未生成=灰标签）。
2. 操作列出现「生成字幕」按钮。
3. 选一条视频点「生成字幕」→ 按钮 loading、状态列变「转换中」→ 成功后变「已生成」并自动弹出右侧抽屉显示字幕全文。
4. 抽屉显示生成时间、字幕路径，「复制」按钮可用。
5. 已生成行操作列出现「重新生成字幕」+「查看字幕」，点「重新生成字幕」弹覆盖确认。
6. 开「批量」开关勾选多条 → 出现「批量生成字幕」→ 确认后状态列刷新。

- [ ] **Step 7: 更新记忆**

更新 `asr-integration-status.md`：RecordTable.vue 的 ASR UI 已恢复（单条/批量/状态列/抽屉），不再有「前端按钮缺失」遗留。镜像 `dysync:asr-local` 已含完整前端 ASR UI。

---

## Self-Review

- **Spec coverage:** 设计 4 项交互（单条生成✅Task1.4+Task2.1 / 批量生成✅Task1.6+Task2.2 / 状态列✅Task1.2+Task2.1 / 结果抽屉✅Task1.5+Task2.3）；后端零改动✅；overwrite 覆盖确认✅Task1.4。部署验证✅Task3。
- **Placeholder scan:** 无 TBD/TODO；每个代码块为完整可粘贴内容；命令含 expected。
- **Type consistency:** `subtitleStatusOf` 返回联合类型，Task2.1 模板用 `=== 'processing'|'done'|'error'` 一致；ref/方法名 Task1 定义与 Task2 引用一致（`generatingId`/`subtitleDrawerVisible`/`handleGenerateSubtitle`/`handleViewSubtitle`/`handleGenerateSubtitleBatch`/`copySubtitlePath`）。
