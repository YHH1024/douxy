# 同步记录页 ASR 字幕 UI 设计

日期：2026-08-13
范围：`app/src/pages/workplace/RecordTable.vue`（单文件改动）
分支：`asr-windows-test`

## 背景

同步记录页（`http://localhost:10101/#/workplace`，RecordTable 组件）当前没有 ASR 字幕入口。后端 ASR 功能（`LocalAsrSubtitleService`）与三个 HTTP 接口早已实现，前端 API 层（`coreapi.ts`）也已接好，但操作列缺少「生成字幕」按钮，表格也缺字幕状态列。

此前（见 `589c49f` 提交）为修复 `RecordTable.vue` 的 GBK 乱码，用干净版本顶替了含 ASR 按钮的损坏版本，导致 ASR UI 缺失。本设计在已修干净的版本上把 ASR UI 重新加回来。

## 前置条件（均已满足，零后端改动）

| 能力 | 状态 | 位置 |
|------|------|------|
| 单条生成 | ✅ | `GET /api/video/asr/{vid}?overwrite=` |
| 批量生成 | ✅ | `POST /api/video/asr/batch?overwrite=`（body: `{ids:[]}`） |
| 读取字幕内容 | ✅ | `GET /api/video/asr/content/{vid}` |
| 前端 API 封装 | ✅ | `coreapi.ts`: `GenerateSubtitle` / `GenerateSubtitleBatch` / `GetSubtitleContent` |
| 行数据字幕字段 | ✅ | `DouyinVideo`: `SubtitleSavePath` / `SubtitleStatusMsg` / `SubtitleCreateTime`（列表接口返回完整实体，字段已带） |

## 交互设计（4 项已与用户确认）

### 1. 单条生成（操作列按钮）
- 每行操作列新增「生成字幕」按钮（图标 + 文案）。
- 点击 → 同步等待（按钮 loading，禁用其他操作）→ 成功后状态列变绿 + **自动打开右侧抽屉展示结果**。
- 若该行已有字幕（`subtitleSavePath` 非空）：按钮文案改为「重新生成」，点击弹 `Modal.confirm`「字幕已存在，是否覆盖？」，确认后带 `overwrite=true` 调用。
- 失败：`message.error` 提示后端返回的 `SubtitleStatusMsg`，状态列变红。

### 2. 批量生成（批量模式顶部按钮）
- 复用表格已有批量机制：`isBatchMode` / `rowSelection` / `selectedRowKeys`。
- 批量模式下，顶部批量操作区（与「重新下载」「删除」并列）新增「批量生成字幕」按钮。
- 必须先勾选至少一条，否则 `message.warning('请先选择要生成字幕的视频')`。
- 点击弹 `Modal.confirm` 确认数量 → 调 `GenerateSubtitleBatch({ids}, overwrite)`（后台执行）。
- 完成：`message.success` + 重新拉表格数据（`loadData()`）刷新状态列。
- 已选行中若含已生成字幕，confirm 文案提示「其中 N 条已有字幕，将覆盖」。

### 3. 字幕状态列（独立列）
- `columns` 数组新增「字幕」列，位置：「CK名称」之后、「操作」之前。
- 用 `<a-tag>` 彩色标签渲染状态：
  - **未生成**（灰 `default`）：`subtitleSavePath` 为空且无错误信息
  - **转换中**（蓝 `processing`）：本地临时态，单条生成按钮 loading 时该行显示
  - **已生成**（绿 `success`）：`subtitleSavePath` 非空
  - **失败**（红 `error`）：`subtitleStatusMsg` 非空且无 `subtitleSavePath`；hover/tooltip 显示信息
- 列宽约 100px，居中。

### 4. 结果展示（右侧抽屉）
- 新增 `<a-drawer>`，从右侧滑出，宽度约 520px。
- 内容：
  - 字幕全文（`GetSubtitleContent` 返回的 `content`，渲染时保留时间戳行，等宽字体 + 滚动）
  - 生成时间（`subtitleCreateTime`）
  - 字幕文件路径（`subtitlePath`）+ 复制按钮（`CopyOutlined`）
- 触发：单条生成成功后自动打开；或在操作列点「查看字幕」（仅 `subtitleSavePath` 非空时显示此按钮）。
- 加载中：抽屉内 `a-spin`。

## 数据流

```
单条: 点「生成字幕」
  → GenerateSubtitle(vid, overwrite)   // 同步等待
  → 成功: loadData() 刷新该行 + 打开抽屉
         → GetSubtitleContent(vid) → 渲染抽屉
  → 失败: message.error(statusMsg)

批量: 勾选 → 点「批量生成字幕」→ confirm
  → GenerateSubtitleBatch({ids}, overwrite)  // 后台
  → message.success(N 条已处理) + loadData() 刷新状态列

查看: 点「查看字幕」(仅已生成)
  → 打开抽屉 + GetSubtitleContent(vid) → 渲染
```

## 改动清单（单文件）

**文件：`app/src/pages/workplace/RecordTable.vue`**

模板（template）：
- `columns` 定义后、「操作」列 `bodyCell` 模板内：操作区 `<a-space>` 增加「生成字幕」/「查看字幕」按钮
- 批量操作按钮区（模板上方，与重新下载/删除按钮处）：增加「批量生成字幕」按钮
- `<a-table>` 的 `#bodyCell` 内：新增 `v-if="column.dataIndex === 'subtitle'"` 分支渲染状态标签
- 模板末尾：新增 `<a-drawer>` 抽屉组件

脚本（script setup）：
- import：从 `@/store`（`useApiStore`）解构已有，按现有写法调用 `api.GenerateSubtitle` 等（与文件内其他 API 调用风格一致，如 `handleReDownload`）；import `CopyOutlined` 图标
- `DataItem` 接口补字段：`subtitleSavePath?`、`subtitleStatusMsg?`、`subtitleCreateTime?`
- `columns` 数组：在 CK名称后插入字幕列对象
- 新增 ref：`subtitleDrawerVisible`、`subtitleContent`（含 content/createTime/path）、`subtitleDrawerLoading`、`generatingId`（当前单条生成中的行 id，用于按钮 loading + 状态列「转换中」）
- 新增方法：
  - `handleGenerateSubtitle(record)`：单条生成，含 overwrite confirm 与成功后自动开抽屉
  - `handleGenerateSubtitleBatch()`：批量生成
  - `handleViewSubtitle(record)`：查看，开抽屉 + 拉内容
  - `subtitleStatusOf(record)`：返回状态类型 `unprocessed|processing|done|error` 供模板用
- 部署：`npm run build` → `docker cp <dist>/. dysync2026:/app/app/dist`（注意 Windows 下目标路径不带尾斜杠，避免嵌套 dist/dist，见 [[dysync-deployment]]）→ `docker commit` 覆盖 `dysync:asr-local` → `docker compose up -d --force-recreate`

## 不做的事（YAGNI）
- 不改后端（接口已齐全）。
- 不加字幕自动播放/视频字幕轨道叠加（超出范围，仅文本展示）。
- 不加字幕导出下载按钮（路径已在抽屉显示，可复制；后续按需再加）。
- 不引入新依赖（抽屉/标签/复制图标全是 ant-design-vue + 已 import 的图标）。

## 验证标准
1. 刷新 `/workplace`，操作列出现「生成字幕」按钮，「字幕」状态列出现。
2. 选一条视频点「生成字幕」，按钮 loading → 成功后状态列变绿，抽屉自动弹出展示字幕全文。
3. 抽屉显示生成时间、路径，复制按钮可用。
4. 已生成行点「重新生成」弹覆盖确认。
5. 批量模式勾选多条，「批量生成字幕」执行后状态列刷新。
6. 失败行状态列显红 + tooltip 显示原因。

## 风险
- `RecordTable.vue` 已是手工修过编码的大文件（48KB），改动集中在模板操作列、columns、新增抽屉与方法区，避免大面积重排以降低编码/合并风险。改动后 `npm run build`（含 `vue-tsc` 类型检查）会卡住类型错误。
- 单条生成同步等待期间，长视频可能十几秒；已用按钮 loading + 状态列「转换中」给反馈，避免误以为卡死。
