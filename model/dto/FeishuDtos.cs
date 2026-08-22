using System.Text.Json.Serialization;

namespace dy.net.model.dto
{
    /// <summary>推送到飞书的一行视频记录(与Excel导出13列一致)。</summary>
    public class FeishuVideoRow
    {
        public long SyncTimeMs { get; set; }
        public long? CreateTimeMs { get; set; }
        public string SyncType { get; set; }
        public string Author { get; set; }
        public string VideoKind { get; set; }
        public string Title { get; set; }
        public string DyUser { get; set; }
        public long PlayCount { get; set; }
        public long DiggCount { get; set; }
        public long CommentCount { get; set; }
        public long ShareCount { get; set; }
        public long CollectCount { get; set; }
        public string Subtitle { get; set; }
    }

    /// <summary>推送结果。</summary>
    public class FeishuPushResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public int Count { get; set; }
        public string BaseUrl { get; set; } = string.Empty;
    }

    // ===== 飞书 API 信封(仅 FeishuBitableService 内部使用) =====
    internal class FeishuResp<T>
    {
        [JsonPropertyName("code")] public int Code { get; set; }
        [JsonPropertyName("msg")] public string Msg { get; set; }
        [JsonPropertyName("data")] public T Data { get; set; }
    }
    internal class FeishuTokenData
    {
        [JsonPropertyName("tenant_access_token")] public string Token { get; set; }
        [JsonPropertyName("expire")] public int Expire { get; set; }
    }
    internal class FeishuAppData
    {
        [JsonPropertyName("app")] public FeishuAppInfo App { get; set; }
    }
    internal class FeishuAppInfo
    {
        [JsonPropertyName("app_token")] public string AppToken { get; set; }
    }
    internal class FeishuTableListData
    {
        [JsonPropertyName("items")] public List<FeishuTableInfo> Items { get; set; }
    }
    internal class FeishuTableInfo
    {
        [JsonPropertyName("table_id")] public string TableId { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; }
    }
    internal class FeishuCreateTableData
    {
        [JsonPropertyName("table_id")] public string TableId { get; set; }
    }
    internal class FeishuRecordListData
    {
        [JsonPropertyName("items")] public List<FeishuRecordInfo> Items { get; set; }
        [JsonPropertyName("has_more")] public bool HasMore { get; set; }
        [JsonPropertyName("page_token")] public string PageToken { get; set; }
    }
    internal class FeishuRecordInfo
    {
        [JsonPropertyName("record_id")] public string RecordId { get; set; }
    }
}
