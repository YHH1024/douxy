# 同步数据导出 Excel 设计

日期：2026-08-15
分支：`asr-windows-test`
范围：VideoController 导出端点、RecordTable 导出按钮、coreapi 封装

## 背景

用户需要把同步记录 + 字幕转写结果导出成 Excel 归档：文件名 `2026年X月X日抖小云同步数据.xlsx`，列结构对应同步记录页的列标题，统计五项拆独立数字列，字幕全文一列。环境约束：docker/NuGet 外网受限，不引第三方 Excel 库。

## 交互

- 入口：同步记录页（RecordTable）「查询」按钮旁加「导出Excel」按钮
- 范围：**当天同步的记录**（`SyncTime >= 今天 00:00`，容器时区 Asia/Shanghai）
- 文件名：`{yyyy年M月d日}抖小云同步数据.xlsx`（日期=导出当天）
- 点击 → 后端生成 → 浏览器直接下载

## Excel 列结构（13 列）

```
同步时间 | 发布时间 | 同步类型 | 博主 | 视频类型 | 视频标题 | CK名称
| 播放 | 点赞 | 评论 | 分享 | 收藏 | 字幕
```

- 时间列：`yyyy-MM-dd HH:mm`；同步类型用中文描述（如"喜欢的"）
- 统计 5 列：真数字（inline number），可排序可求和；NULL/0 导出为 0
- 字幕列：`.txt` 纯文本全文（复用「优先 .txt 退化 .srt」逻辑）；无字幕留空
- 不导出：操作列、字幕状态列、数据聚合列

## 技术实现

### 后端：`Controllers/VideoController.cs` 新端点

```csharp
[Authorize]
[HttpGet("export/today")]
public async Task<IActionResult> ExportTodayExcel()
```

流程：
1. `douyinVideoService` 查 `SyncTime >= DateTime.Today` 的记录（无则空表只有表头）
2. 逐条读字幕（同 `GetSubtitleContent` 的 txt 优先逻辑，抽公共私有方法复用）
3. 手写 Open XML 生成 .xlsx（`System.IO.Compression.ZipArchive`，零依赖）：
   - 5 个 part：`[Content_Types].xml`、`_rels/.rels`、`xl/workbook.xml`、`xl/_rels/workbook.xml.rels`、`xl/worksheets/sheet1.xml`
   - 字符串全部用 **inline string**（`t="inlineStr"><is><t>`），免 sharedStrings 表
   - 表头行加粗（`<b/>`）；列宽按首行内容估算（中文×2 字符宽）
   - XML 转义：`& < > " '` 五字符；换行保留（`&#10;`）
   - 数字单元格用 `t="n"` 直接写数值
4. 返回 `File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{DateTime.Now:yyyy年M月d日}抖小云同步数据.xlsx")`

### 前端

- `coreapi.ts` 加 `ExportTodayExcel()`：`fetch` 带 Authorization 拿 blob → `URL.createObjectURL` → `<a download>` 点击（沿用 AppSet 导出配置的现成模式；因需自定义文件名与 blob，不走通用 http.request）
- `RecordTable.vue`「查询」按钮旁加「导出Excel」按钮（`FileExcelOutlined` 图标），loading 态，失败 `message.error`；导出 0 条时也下载空表并提示「今日暂无同步记录，已导出空表」

## 不做（YAGNI）

- 不做日期范围选择（就"当天"；扩展改 query 即可）
- 不做定时自动导出、多 sheet、共享字符串优化
- 不做列自定义

## 验证标准

1. 点「导出Excel」下载 `2026年8月15日抖小云同步数据.xlsx`，Excel 双击正常打开
2. 列结构与 13 列设计一致，表头加粗，统计列是数字（可求和）
3. 字幕列含纯文本全文（无时间轴），有字幕的行内容非空
4. 今日无同步时下载空表（仅表头）+ 提示
5. 编译 0 错误、前端 build 过、部署后端到端可下载

## 风险与对策

- Open XML 手写格式错误 → Excel 打不开：严格按 ECMA-376 最小结构；用最小合法模板起手，验证标准 1 兜底
- 字幕全文很长（几万字）→ 单元格上限 32767 字符：超长截断到 32000 加 `…`
- 中文文件名 HTTP 头 → 用 `Content-Disposition` filename* UTF-8 编码（ASP.NET Core File() 自动处理）
