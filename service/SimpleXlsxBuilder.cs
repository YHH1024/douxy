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
                sb.Append($"<row r=\"{r + 1}\">");
                var row = _rows[r];
                for (var c = 0; c < row.Count; c++)
                {
                    var cellRef = $"{ColumnName(c)}{r + 1}";
                    var cell = row[c];
                    if (cell == null)
                    {
                        sb.Append($"<c r=\"{cellRef}\"/>");
                        continue;
                    }
                    if (cell is string s)
                    {
                        sb.Append($"<c r=\"{cellRef}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Escape(s)}</t></is></c>");
                    }
                    else
                    {
                        var num = Convert.ToDouble(cell);
                        sb.Append($"<c r=\"{cellRef}\" t=\"n\"><v>{num.ToString("0", System.Globalization.CultureInfo.InvariantCulture)}</v></c>");
                    }
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
