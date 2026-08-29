using System;

namespace dy.net.service
{
    /// <summary>抖音接口网络层失败(TLS握手被掐/超时/连接拒绝等)。
    /// 与"Cookie失效"区分:网络失败是瞬时抖动,下轮自愈,不应误导用户去重新登录Cookie,
    /// 也不应把Cookie状态码改坏。调用方按类型分型处理。</summary>
    public class DouyinNetworkException : Exception
    {
        public DouyinNetworkException(string message, Exception inner) : base(message, inner) { }
    }
}
