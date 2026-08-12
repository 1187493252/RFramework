using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RFramework
{
    /// <summary>
    /// WebRequest 模块接口。
    /// 提供 HTTP 通信的完整抽象，支持 GET/POST/PUT/DELETE、multipart 上传、
    /// 下载进度、并发控制、超时重试和 Tag 分组管理。
    /// </summary>
    /// <remarks>
    /// 设计要点：
    /// - 异步统一使用 Task，不依赖 UniTask 或 Coroutine。
    /// - 7 种请求重载覆盖实际业务全场景（参考旧项目 WebRequestComponent）。
    /// - IProgress&lt;float&gt; 提供下载进度，.NET Standard 2.0 内置支持。
    /// - CancellationToken 支持请求取消，Tag 支持批量取消。
    /// </remarks>
    public interface IWebRequestModule
    {
        // ==================== 配置注入 ====================

        /// <summary>
        /// 设置底层 HTTP 辅助器（Component.Awake 中调用）。
        /// 在发送任何请求之前必须调用，否则所有请求方法会抛出 <see cref="RFrameworkException"/>。
        /// </summary>
        /// <param name="helper">辅助器实例，不能为 null。</param>
        void SetHelper(IWebRequestHelper helper);

        /// <summary>
        /// 设置最大并发请求数（0 表示无限制）。
        /// 当活跃请求达到上限时，后续请求将排队等待。
        /// 默认值 5。
        /// </summary>
        /// <param name="max">最大并发数，不能为负数。</param>
        void SetMaxConcurrentRequests(int max);

        /// <summary>
        /// 设置默认超时时间（毫秒）。
        /// 所有未显式指定超时的请求方法使用此值。
        /// 默认值 30000（30 秒）。
        /// </summary>
        /// <param name="timeoutMs">超时毫秒数，0 表示无超时限制。</param>
        void SetDefaultTimeout(int timeoutMs);

        /// <summary>
        /// 设置默认重试次数。
        /// 默认值 0（不重试）。
        /// 重试条件：ConnectionTimeout、RequestTimeout、NetworkError、HTTP 5xx。
        /// 不重试：Aborted（用户取消）、HTTP 4xx（客户端错误）。
        /// </summary>
        /// <param name="retries">重试次数，不能为负数。</param>
        void SetDefaultRetries(int retries);

        // ==================== GET ====================

        /// <summary>
        /// 发送 GET 请求。
        /// queryParams 自动拼接到 URL（空值跳过，键值自动 URL 编码）。
        /// </summary>
        /// <param name="url">请求 URL（不含 query string）。</param>
        /// <param name="queryParams">查询参数字典，可为 null。</param>
        /// <param name="headers">自定义请求头，可为 null。</param>
        /// <param name="tag">请求标签，用于分组批量取消。</param>
        /// <param name="priority">优先级（越大越优先，在有等待队列时生效）。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>HTTP 响应。</returns>
        Task<WebResponse> GetAsync(
            string url,
            Dictionary<string, string> queryParams = null,
            Dictionary<string, string> headers = null,
            string tag = null,
            uint priority = 0,
            CancellationToken ct = default);

        // ==================== POST ====================

        /// <summary>
        /// POST 字符串 Body + 自定义 MIME 类型（JSON/XML/Text 等）。
        /// 最通用的 POST 形式，适用于 RESTful API 的 JSON 交互。
        /// </summary>
        /// <param name="url">请求 URL。</param>
        /// <param name="body">请求体字符串。</param>
        /// <param name="mimeType">Content-Type 类型，默认为 JSON。</param>
        /// <param name="headers">自定义请求头，可为 null。</param>
        /// <param name="tag">请求标签。</param>
        /// <param name="priority">优先级。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>HTTP 响应。</returns>
        Task<WebResponse> PostAsync(
            string url,
            string body,
            MimeType mimeType = MimeType.Json,
            Dictionary<string, string> headers = null,
            string tag = null,
            uint priority = 0,
            CancellationToken ct = default);

        /// <summary>
        /// POST 表单字段（Content-Type: application/x-www-form-urlencoded）。
        /// 等价于 HTML form 提交，字段自动 URL 编码。
        /// </summary>
        /// <param name="url">请求 URL。</param>
        /// <param name="formFields">表单字段字典（键值对）。</param>
        /// <param name="headers">自定义请求头，可为 null。</param>
        /// <param name="tag">请求标签。</param>
        /// <param name="priority">优先级。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>HTTP 响应。</returns>
        Task<WebResponse> PostFormAsync(
            string url,
            Dictionary<string, string> formFields,
            Dictionary<string, string> headers = null,
            string tag = null,
            uint priority = 0,
            CancellationToken ct = default);

        /// <summary>
        /// POST multipart/form-data（文件+文本混合上传）。
        /// 支持上传进度回调（通过 IProgress&lt;float&gt;）。
        /// 自动生成 boundary 并设置正确的 Content-Type。
        /// </summary>
        /// <param name="url">请求 URL。</param>
        /// <param name="fields">multipart 字段列表（可混合文本和文件）。</param>
        /// <param name="headers">自定义请求头，可为 null。</param>
        /// <param name="progress">上传进度报告器（0.0 ~ 1.0），可为 null。</param>
        /// <param name="tag">请求标签。</param>
        /// <param name="priority">优先级。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>HTTP 响应。</returns>
        Task<WebResponse> PostMultipartAsync(
            string url,
            List<MultipartField> fields,
            Dictionary<string, string> headers = null,
            IProgress<float> progress = null,
            string tag = null,
            uint priority = 0,
            CancellationToken ct = default);

        // ==================== PUT ====================

        /// <summary>
        /// 发送 PUT 请求（Body 为字符串 + MIME 类型）。
        /// </summary>
        /// <param name="url">请求 URL。</param>
        /// <param name="body">请求体字符串。</param>
        /// <param name="mimeType">Content-Type 类型，默认 JSON。</param>
        /// <param name="headers">自定义请求头，可为 null。</param>
        /// <param name="tag">请求标签。</param>
        /// <param name="priority">优先级。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>HTTP 响应。</returns>
        Task<WebResponse> PutAsync(
            string url,
            string body,
            MimeType mimeType = MimeType.Json,
            Dictionary<string, string> headers = null,
            string tag = null,
            uint priority = 0,
            CancellationToken ct = default);

        // ==================== DELETE ====================

        /// <summary>
        /// 发送 DELETE 请求（可选 Body）。
        /// body 为 null 时不发送请求体（标准 RESTful DELETE）。
        /// body 非 null 时发送带 Body 的 DELETE（部分后端需要）。
        /// </summary>
        /// <param name="url">请求 URL。</param>
        /// <param name="body">请求体字符串，null 时不发送请求体。</param>
        /// <param name="mimeType">Content-Type 类型，默认 JSON（仅 body 非 null 时有效）。</param>
        /// <param name="headers">自定义请求头，可为 null。</param>
        /// <param name="tag">请求标签。</param>
        /// <param name="priority">优先级。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>HTTP 响应。</returns>
        Task<WebResponse> DeleteAsync(
            string url,
            string body = null,
            MimeType mimeType = MimeType.Json,
            Dictionary<string, string> headers = null,
            string tag = null,
            uint priority = 0,
            CancellationToken ct = default);

        // ==================== 原始请求 ====================

        /// <summary>
        /// 发送原始请求（直接构造 <see cref="WebRequestData"/> 传入）。
        /// 用于非标准 HTTP 方法（HEAD/PATCH 等）或需要完全控制请求参数的场景。
        /// </summary>
        /// <param name="request">完整的请求数据。</param>
        /// <param name="progress">下载进度报告器（0.0 ~ 1.0），可为 null。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>HTTP 响应。</returns>
        Task<WebResponse> SendAsync(
            WebRequestData request,
            IProgress<float> progress = null,
            CancellationToken ct = default);

        // ==================== 便捷方法 ====================

        /// <summary>
        /// 下载文件（二进制数据 + 进度回调）。
        /// 对 GET 的便捷包装，直接返回响应体字节数组。
        /// </summary>
        /// <param name="url">下载 URL。</param>
        /// <param name="progress">下载进度报告器（0.0 ~ 1.0），可为 null。</param>
        /// <param name="headers">自定义请求头，可为 null。</param>
        /// <param name="tag">请求标签。</param>
        /// <param name="priority">优先级。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>文件字节数据。</returns>
        Task<byte[]> DownloadAsync(
            string url,
            IProgress<float> progress = null,
            Dictionary<string, string> headers = null,
            string tag = null,
            uint priority = 0,
            CancellationToken ct = default);

        /// <summary>
        /// 下载文件到磁盘（适用于大文件，避免全量加载到内存）。
        /// </summary>
        /// <param name="url">下载 URL。</param>
        /// <param name="savePath">保存到磁盘的目标路径。</param>
        /// <param name="progress">下载进度报告器（0.0 ~ 1.0），可为 null。</param>
        /// <param name="headers">自定义请求头，可为 null。</param>
        /// <param name="tag">请求标签，用于批量取消。</param>
        /// <param name="priority">请求优先级。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>下载完成 Task。</returns>
        Task DownloadFileAsync(
            string url,
            string savePath,
            IProgress<float> progress = null,
            Dictionary<string, string> headers = null,
            string tag = null,
            uint priority = 0,
            CancellationToken ct = default);

        /// <summary>
        /// 将 HTTP 响应流写入文件，并返回状态码与响应头。
        /// 此接口是下载模块使用的底层能力，不负责重试、校验或最终文件提交。
        /// </summary>
        /// <param name="url">下载 URL。</param>
        /// <param name="savePath">写入路径，通常为 .part 临时文件。</param>
        /// <param name="offset">续传起始字节；大于 0 时自动发送 Range 请求并追加写入。</param>
        /// <param name="progress">文件字节进度报告器，可为 null。</param>
        /// <param name="headers">附加请求头，可为 null；Range 由模块统一设置。</param>
        /// <param name="tag">请求标签。</param>
        /// <param name="priority">请求优先级。</param>
        /// <param name="timeoutMs">请求超时毫秒数；0 表示不启用总时长超时。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>HTTP 响应。</returns>
        Task<WebResponse> DownloadFileRangeAsync(
            string url,
            string savePath,
            long offset,
            IProgress<WebDownloadProgress> progress = null,
            Dictionary<string, string> headers = null,
            string tag = null,
            uint priority = 0,
            int timeoutMs = 0,
            CancellationToken ct = default);

        /// <summary>
        /// GET 请求并直接返回 JSON 反序列化后的对象。
        /// 便捷包装：自动设置 Accept: application/json，失败时抛出异常。
        /// </summary>
        /// <typeparam name="T">目标反序列化类型。</typeparam>
        /// <param name="url">请求 URL。</param>
        /// <param name="queryParams">查询参数字典，可为 null。</param>
        /// <param name="headers">自定义请求头，可为 null。</param>
        /// <param name="tag">请求标签。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>反序列化后的对象。</returns>
        Task<T> GetJsonAsync<T>(
            string url,
            Dictionary<string, string> queryParams = null,
            Dictionary<string, string> headers = null,
            string tag = null,
            CancellationToken ct = default);

        // ==================== 管理 ====================

        /// <summary>
        /// 取消所有指定 Tag 的请求（包括排队中和进行中的）。
        /// </summary>
        /// <param name="tag">请求标签，null 时取消所有请求。</param>
        void CancelAllByTag(string tag);

        /// <summary>
        /// 获取当前活跃请求数（正在网络传输中的请求）。
        /// </summary>
        int ActiveRequestCount { get; }

        /// <summary>
        /// 获取排队中的请求数（等待并发槽位的请求）。
        /// </summary>
        int QueuedRequestCount { get; }
    }
}
