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

    /// <summary>连通性测试单项结果。</summary>
    public class FeishuTestItem
    {
        public string Name { get; set; }
        public bool Ok { get; set; }
        public string Message { get; set; }
    }

    // ===== 飞书 API 信封(仅 FeishuBitableService 内部使用) =====
    internal class FeishuResp<T>
    {
        [JsonPropertyName("code")] public int Code { get; set; }
        [JsonPropertyName("msg")] public string Msg { get; set; }
        [JsonPropertyName("data")] public T Data { get; set; }
    }
    /// <summary>tenant_access_token/internal 是扁平响应:token/expire 直接在顶层,无 data 包裹(与飞书其他接口的信封不同)。</summary>
    internal class FeishuTokenResp
    {
        [JsonPropertyName("code")] public int Code { get; set; }
        [JsonPropertyName("msg")] public string Msg { get; set; }
        [JsonPropertyName("tenant_access_token")] public string Token { get; set; }
        [JsonPropertyName("expire")] public int Expire {  get; set; }
    }
    internal class FeishuAppData
    {
        [JsonPropertyName("app")] public FeishuAppInfo App { get; set; }
    }
    internal class FeishuAppInfo
    {
        [JsonPropertyName("app_token")] public string AppToken { get; set; }
    }
    /// <summary>drive/v1/files/create_folder 响应:{token,url} 在 data 下。</summary>
    internal class FeishuFolderData
    {
        [JsonPropertyName("token")] public string Token { get; set; }
        [JsonPropertyName("url")] public string Url { get; set; }
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

    /// <summary>OAuth user_access_token 响应(扁平结构,无data包裹;refresh字段仅授予offline_access时返回)。</summary>
    internal class FeishuOAuthTokenResp
    {
        [JsonPropertyName("code")] public int Code { get; set; }
        [JsonPropertyName("access_token")] public string AccessToken { get; set; }
        [JsonPropertyName("expires_in")] public int? ExpiresIn { get; set; }
        [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; }
        [JsonPropertyName("refresh_token_expires_in")] public int? RefreshExpiresIn { get; set; }
        [JsonPropertyName("error")] public string Error { get; set; }
        [JsonPropertyName("error_description")] public string ErrorDescription { get; set; }
    }
}
