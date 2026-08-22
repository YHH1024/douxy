using System.Text;

namespace dy.net.utils
{
    /// <summary>读取视频字幕文本:优先同名 .txt(纯文本),退化 .srt。失败/超长截断(>32000),返回安全文本。
    /// 供 Excel 导出(VideoController)与飞书推送(FeishuPushService)共用。</summary>
    public static class SubtitleTextReader
    {
        public static async Task<string> ReadAsync(string subtitleFullPath)
        {
            try
            {
                string contentPath = subtitleFullPath;
                string textSibling = Path.ChangeExtension(subtitleFullPath, ".txt");
                if (File.Exists(textSibling))
                {
                    contentPath = textSibling;
                }
                if (!File.Exists(contentPath))
                {
                    return string.Empty;
                }
                var text = await ReadContentAsync(contentPath);
                return text.Length > 32000 ? text.Substring(0, 32000) + "…" : text;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static async Task<string> ReadContentAsync(string filePath)
        {
            try
            {
                return await File.ReadAllTextAsync(filePath, Encoding.UTF8);
            }
            catch (DecoderFallbackException)
            {
                return await File.ReadAllTextAsync(filePath, Encoding.Default);
            }
        }
    }
}
