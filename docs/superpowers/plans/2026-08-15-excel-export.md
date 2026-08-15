# 同步数据导出 Excel 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 同步记录页一键导出当天同步数据为真 .xlsx（13 列，统计数字列+字幕全文列），文件名 `2026年8月15日抖小云同步数据.xlsx`，零第三方依赖。

**Architecture:** 后端 `VideoController` 加 `GET /api/video/export/today`——查当天记录、逐条读 `.txt` 字幕、手写 Open XML（ZipArchive+inline string）生成 xlsx 字节流返回；前端 RecordTable 加按钮，coreapi 用原生 `fetch`（js-cookie 取 Authorization）拿 blob 触发下载。

**Tech Stack:** .NET 6（System.IO.Compression.ZipArchive、XmlWriter/Literal string）、Vue3 + js-cookie + ant-design-vue。

## Global Constraints

- 文件名格式：`{DateTime.Now:yyyy年M月d日}抖小云同步数据.xlsx`
- 13 列顺序：同步时间|发布时间|同步类型|博主|视频类型|视频标题|CK名称|播放|点赞|评论|分享|收藏|字幕
- 统计列必须是数字单元格（`t="n"`），NULL 写 0
- 字幕单元格超 32000 字符截断加 `…`（Excel 上限 32767）
- 字符串 XML 转义 `& < > " '`；换行写 `&#10;`
- 「今天」按容器本地时间（TZ=Asia/Shanghai）`DateTime.Today` 起
- 不引任何 NuGet 包；部署走既有链（publish→cp dll→前端 build→cp dist 展平→commit→force-recreate）

---

### Task 1: 后端——xlsx 生成器 + 导出端点

**Files:**
- Create: `D:\dysync\dysync.net\service\SimpleXlsxBuilder.cs`（独立类，单一职责：行数据→xlsx 字节）
- Modify: `D:\dysync\dysync.net\Controllers\VideoController.cs`（抽公共读字幕方法 + 新端点）

**Interfaces:**
- Produces: `SimpleXlsxBuilder`——`AddHeader(IReadOnlyList<string> cols)`、`AddRow(IReadOnlyList<object> cells)`（object 为 string→inlineStr / 数字类型→n / null→空串）、`Build() → byte[]`；`VideoController.ExportTodayExcel()` 端点

- [ ] **Step 1: 新建 SimpleXlsxBuilder.cs**

```csharp
using System.IO.Compression;
using System.Text;

namespace dy.net.service
{
    /// <summary>
    /// 零依赖的最小 xlsx 生成器(Open XML inline string,无 sharedStrings)。
    /// 用法:AddHeader → AddRow* → Build。
    /// </summary>
    public class SimpleXlsxBuilder
    {
        private readonly List<List<object>> _rows = new();

        public void AddHeader(IReadOnlyList<string> cols)
        {
            _rows.Add(cols.Cast<object>().ToList());
        }

        /// <summary>object 为 string(或 null=空)渲染文本单元格;其他数值类型渲染数字单元格。</summary>
        public void AddRow(IReadOnlyList<object> cells)
        {
            _rows.Add(cells.ToList());
        }

        public byte[] Build()
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteEntry(zip, "[Content_Types].xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                    "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                    "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                    "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                    "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                    "</Types>");

                WriteEntry(zip, "_rels/.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                    "</Relationships>");

                WriteEntry(zip, "xl/workbook.xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                    "<sheets><sheet name=\"同步数据\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
                    "</workbook>");

                WriteEntry(zip, "xl/_rels/workbook.xml.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                    "</Relationships>");

                WriteEntry(zip, "xl/worksheets/sheet1.xml", BuildSheetXml());
            }
            return ms.ToArray();
        }

        private string BuildSheetXml()
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            // 列宽:按每列最长内容估算(中文按2字符宽),宽 6~60
            var colCount = _rows.Count > 0 ? _rows.Max(r => r.Count) : 0;
            if (colCount > 0)
            {
                sb.Append("<cols>");
                for (var c = 0; c < colCount; c++)
                {
                    var width = _rows.Count == 0 ? 10 : _rows.Max(r => c < r.Count ? DisplayWidth(r[c]) : 0);
                    width = Math.Clamp(width + 2, 6, 60);
                    sb.Append($"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{width}\" customWidth=\"1\"/>");
                }
                sb.Append("</cols>");
            }
            sb.Append("<sheetData>");
            for (var r = 0; r < _rows.Count; r++)
            {
                var isHeader = r == 0;
                sb.Append($"<row r=\"{r + 1}\">");
                var row = _rows[r];
                for (var c = 0; c < row.Count; c++)
                {
                    var ref32 = $"{ColumnName(c)}{r + 1}";
                    var cell = row[c];
                    if (cell == null)
                    {
                        sb.Append($"<c r=\"{ref32}\"/>");
                        continue;
                    }
                    if (cell is string s)
                    {
                        sb.Append($"<c r=\"{ref32}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Escape(s)}</t></is></c>");
                    }
                    else
                    {
                        var num = Convert.ToDouble(cell);
                        sb.Append($"<c r=\"{ref32}\" t=\"n\"><v>{num.ToString("0", System.Globalization.CultureInfo.InvariantCulture)}</v></c>");
                    }
                }
                if (isHeader)
                {
                    // 表头行需要加粗样式,补 s 属性引用 style——为保持零 rels 复杂度,表头用「【】」视觉强调代替样式表
                }
                sb.Append("</row>");
            }
            sb.Append("</sheetData></worksheet>");
            return sb.ToString();
        }

        private static int DisplayWidth(object cell)
        {
            if (cell == null) return 0;
            var s = cell.ToString() ?? string.Empty;
            // 长文本(字幕)不参与撑宽
            if (s.Length > 40) s = s.Substring(0, 40);
            var w = 0;
            foreach (var ch in s) w += ch > 127 ? 2 : 1;
            return w;
        }

        private static string ColumnName(int index)
        {
            var name = string.Empty;
            index++;
            while (index > 0)
            {
                var mod = (index - 1) % 26;
                name = (char)('A' + mod) + name;
                index = (index - 1) / 26;
            }
            return name;
        }

        private static string Escape(string s) => s
            .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;").Replace("'", "&apos;")
            .Replace("\r\n", "&#10;").Replace("\n", "&#10;").Replace("\r", "&#10;");

        private static void WriteEntry(ZipArchive zip, string path, string content)
        {
            var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }
}
```

- [ ] **Step 2: VideoController 抽公共读字幕方法**

把 `GetSubtitleContent` 中这段（txt 优先逻辑）：
```csharp
            // 优先读取同名纯文本文件（无时间轴、可整段复制）；不存在则退化到 .srt
            string contentPath = subtitleFullPath;
            string textSibling = Path.ChangeExtension(subtitleFullPath, ".txt");
            if (System.IO.File.Exists(textSibling))
            {
                contentPath = textSibling;
            }

            var content = await ReadSubtitleContentAsync(contentPath);
```
替换为调用新私有方法：
```csharp
            var content = await ReadSubtitleTextAsync(subtitleFullPath);
```
并在 `ReadSubtitleContentAsync` 方法旁新增：
```csharp
        /// <summary>读取视频字幕文本:优先同名 .txt(纯文本),退化 .srt。失败返回空串。</summary>
        private static async Task<string> ReadSubtitleTextAsync(string subtitleFullPath)
        {
            try
            {
                string contentPath = subtitleFullPath;
                string textSibling = Path.ChangeExtension(subtitleFullPath, ".txt");
                if (System.IO.File.Exists(textSibling))
                {
                    contentPath = textSibling;
                }
                if (!System.IO.File.Exists(contentPath))
                {
                    return string.Empty;
                }
                var text = await ReadSubtitleContentAsync(contentPath);
                return text.Length > 32000 ? text.Substring(0, 32000) + "…" : text;
            }
            catch
            {
                return string.Empty;
            }
        }
```

- [ ] **Step 3: 加导出端点（BackfillVideoStats 方法后）**

```csharp
        /// <summary>
        /// 导出当天同步数据为 Excel(xlsx)。13 列:同步记录页列标题对应,统计数字列,字幕全文列。
        /// </summary>
        [Authorize]
        [HttpGet("export/today")]
        public async Task<IActionResult> ExportTodayExcel()
        {
            var all = await douyinVideoService.GetAllAsync();
            var today = all.Where(v => v.SyncTime >= DateTime.Today).OrderBy(v => v.SyncTime).ToList();

            var builder = new SimpleXlsxBuilder();
            builder.AddHeader(new[] { "同步时间", "发布时间", "同步类型", "博主", "视频类型", "视频标题", "CK名称", "播放", "点赞", "评论", "分享", "收藏", "字幕" });

            foreach (var v in today)
            {
                string subtitle = string.Empty;
                if (!string.IsNullOrWhiteSpace(v.SubtitleSavePath))
                {
                    try { subtitle = await ReadSubtitleTextAsync(Path.GetFullPath(v.SubtitleSavePath)); }
                    catch { subtitle = string.Empty; }
                }
                builder.AddRow(new object[]
                {
                    v.SyncTime.ToString("yyyy-MM-dd HH:mm"),
                    v.CreateTime?.ToString("yyyy-MM-dd HH:mm") ?? string.Empty,
                    v.ViedoType.GetDesc(),
                    v.Author ?? string.Empty,
                    $"{v.Tag1 ?? string.Empty} {v.Tag2 ?? string.Empty} {v.Tag3 ?? string.Empty}".Trim(),
                    v.VideoTitle ?? string.Empty,
                    v.DyUser ?? string.Empty,
                    v.PlayCount ?? 0,
                    v.DiggCount ?? 0,
                    v.CommentCount ?? 0,
                    v.ShareCount ?? 0,
                    v.CollectCount ?? 0,
                    subtitle,
                });
            }

            var bytes = builder.Build();
            var fileName = $"{DateTime.Now:yyyy年M月d日}抖小云同步数据.xlsx";
            Serilog.Log.Information("[export/today] 导出 {Count} 条, 文件 {File}", today.Count, fileName);
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }
```
注：`v.DyUser` 若实体无此属性（列表页的 CK名称 来自 Cookie 关联），改为空串占位 `string.Empty` 并在验证时确认（查 `DouyinVideo` 实体有无 `DyUser`/`CookieId` 对应名称）。

- [ ] **Step 4: 编译验证**

Run: `cd /d/dysync/dysync.net && /d/dotnet-sdk/dotnet.exe build dy.net.csproj -c Release 2>&1 | tail -3`
Expected: `0 个错误`。若 `DyUser` 不存在按 Step 3 注释修正。

- [ ] **Step 5: 提交**

```bash
cd /d/dysync/dysync.net
git add service/SimpleXlsxBuilder.cs Controllers/VideoController.cs
git commit -m "feat: GET /api/video/export/today 导出当天同步数据 xlsx(零依赖)"
```

---

### Task 2: 前端——导出按钮 + blob 下载 + 部署验证

**Files:**
- Modify: `D:\dysync\dysync.net\app\src\pages\workplace\RecordTable.vue`（查询按钮旁 + handleExportExcel）
- Modify: `D:\dysync\dysync.net\app\src\store\coreapi.ts`（无需改 http 层——导出直接在组件里 fetch，见下）

**Interfaces:**
- Consumes: Task 1 的 `GET /api/video/export/today`；token 在 cookie `Authorization`（js-cookie，见 `src/utils/axiosHttp.ts:151`）

- [ ] **Step 1: RecordTable.vue 查询按钮旁加导出按钮**

定位「查询」按钮：
```html
          <a-button type="primary" @click="GetRecords" class="query-button">
            <SearchOutlined />查询
          </a-button>
```
其后加：
```html
          <a-button @click="handleExportExcel" :loading="exporting" class="query-button">
            <FileExcelOutlined />导出Excel
          </a-button>
```

- [ ] **Step 2: import 图标（@ant-design/icons-vue import 块加）**

```ts
  FileExcelOutlined,
```
（加到现有 `SearchOutlined,` 等 import 列表中）

- [ ] **Step 3: 状态与处理函数（generatingBatch 定义附近加）**

```ts
// -------------------------- 导出 Excel --------------------------
const exporting = ref(false);
const handleExportExcel = async () => {
  exporting.value = true;
  try {
    const token = Cookie.get('Authorization');
    const resp = await fetch('/api/video/export/today', {
      headers: token ? { Authorization: token } : {},
    });
    if (!resp.ok) {
      message.error(`导出失败: HTTP ${resp.status}`);
      return;
    }
    const blob = await resp.blob();
    const dispo = resp.headers.get('Content-Disposition') || '';
    // 从 filename*=UTF-8''... 或 filename=... 提取文件名,失败用默认
    let fileName = `${new Date().getFullYear()}年${new Date().getMonth() + 1}月${new Date().getDate()}日抖小云同步数据.xlsx`;
    try {
      const m = dispo.match(/filename\*=(?:UTF-8'')?([^;]+)/i) || dispo.match(/filename="?([^";]+)"?/i);
      if (m) fileName = decodeURIComponent(m[1]);
    } catch { /* 用默认名 */ }
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
    message.success('导出成功');
  } catch (err) {
    console.error('导出失败:', err);
    message.error('导出失败，请稍后重试');
  } finally {
    exporting.value = false;
  }
};
```
import js-cookie（文件顶部 vue import 附近）：
```ts
import Cookie from 'js-cookie';
```

- [ ] **Step 4: 前端构建**

Run: `cd /d/dysync/dysync.net/app && npm run build 2>&1 | tail -3`
Expected: `✓ built in ...s`

- [ ] **Step 5: 部署（dll + dist + commit + recreate）**

```bash
cd /d/dysync/dysync.net
/d/dotnet-sdk/dotnet.exe publish dy.net.csproj -c Release -r linux-x64 --self-contained false -o /d/dysync/build-context/pub-export
docker cp /d/dysync/build-context/pub-export/dy.net.dll dysync2026:/app/dy.net.dll
docker cp /d/dysync/build-context/pub-export/SimpleXlsxBuilder.dll dysync2026:/app/SimpleXlsxBuilder.dll 2>/dev/null || echo "SimpleXlsxBuilder 并入主 dll,无独立 dll(正常)"
docker exec dysync2026 sh -c 'rm -rf /app/app/dist/assets /app/app/dist/index.html /app/app/dist/logo.png /app/app/dist/dist'
docker cp /d/dysync/dysync.net/app/dist/. dysync2026:/app/app/dist
docker exec dysync2026 sh -c 'if [ -d /app/app/dist/dist ]; then cd /app/app/dist && rm -rf assets index.html logo.png && cp -r dist/* ./ && rm -rf dist && echo "已展平"; else echo "无嵌套"; fi'
docker commit dysync2026 dysync:asr-local
cd /d/dysync && docker compose up -d --force-recreate
```
Expected: recreate 成功、容器 Image == 镜像 Id

- [ ] **Step 6: 端到端验证（curl 下载并检查 xlsx 合法性）**

```bash
sleep 8
TOKEN=$(curl -s -X POST "http://localhost:10101/api/Auth/Login" -H 'Content-Type: application/json' -d '{"UserName":"douyin","Password":"douyin2026"}' | python -c "import sys,json;print(json.load(sys.stdin).get('token',''))")
curl -s -m 120 -o /tmp/dysync_export.xlsx -w "HTTP %{http_code} | %{size_download} bytes\n" "http://localhost:10101/api/video/export/today" -H "Authorization: Bearer $TOKEN"
python -c "
import zipfile
z = zipfile.ZipFile(r'/tmp/dysync_export.xlsx')
names = z.namelist()
print('zip parts:', names)
assert '[Content_Types].xml' in names and 'xl/worksheets/sheet1.xml' in names
xml = z.read('xl/worksheets/sheet1.xml').decode('utf-8')
print('表头含 同步时间:', '同步时间' in xml)
print('表头含 字幕:', '字幕' in xml)
import re; rows = re.findall(r'<row r=', xml)
print('数据行数(含表头):', len(rows))"
```
Expected: HTTP 200、zip parts 含 5 个 part、表头关键词命中、行数 = 当天记录+1

- [ ] **Step 7: 提交 + 记忆更新**

```bash
cd /d/dysync/dysync.net
git add app/src/pages/workplace/RecordTable.vue
git commit -m "feat: 同步记录页「导出Excel」按钮(blob下载当天数据)"
```
更新 `video-stats-fields.md` 记忆或新增：导出端点、SimpleXlsxBuilder 位置、验证方法。

---

## Self-Review

1. **Spec 覆盖**：13列✅T1S3 当天范围✅T1S3(Where Today) 文件名✅T1S3 统计数字✅T1S1(t="n") 字幕全文/截断✅T1S2(32000) 表头✅T1S3 按钮位置✅T2S1 blob下载✅T2S3 空表✅(无数据只有表头) 验证✅T2S6 风险对策（转义✅Escape 中文文件名✅File()自动 截断✅）
2. **占位符**：T1S3 的 DyUser 注释是条件处理（给了明确 fallback），非占位
3. **类型一致**：`SimpleXlsxBuilder.AddRow(object[])` T1S1 定义=T1S3 使用；`ReadSubtitleTextAsync(string)` T1S2 定义=T1S3 使用；端点路径 T1S3=T2S3 一致；cookie key `Authorization` 与 axiosHttp.ts:151 一致
