using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RFramework
{
    /// <summary>
    /// WebRequest 模块实现。
    /// 提供 HTTP 通信的并发控制、超时重试、Tag 分组管理和请求优先级调度。
    /// </summary>
    /// <remarks>
    /// 架构要点：
    /// - 优先级调度器控制并发上限，超出的请求按优先级等待。
    /// - 每个请求通过 Linked CancellationTokenSource 实现超时+用户取消的双重控制。
    /// - 重试策略：连接超时、请求超时、网络错误、5xx 错误自动重试；4xx 和用户取消不重试。
    /// - Tag 机制：CancelAllByTag 遍历活跃+排队中的请求，匹配 Tag 则触发 CTS 取消。
    /// - 所有请求对齐到同一个 TAP 异步模型（Task），无 Coroutine 依赖。
    /// </remarks>
    internal sealed class WebRequestModule : RFrameworkModule, IWebRequestModule
    {
        // ====== 配置 ======

        /// <summary>
        /// 底层 HTTP 辅助器实例，由 SetHelper 注入。
        /// 所有请求最终通过此辅助器执行实际网络通信。
        /// </summary>
        private IWebRequestHelper helper;

        /// <summary>
        /// 并发优先级调度器，控制同时进行中的请求数。
        /// 超过上限的请求按优先级等待，同优先级保持 FIFO。
        /// </summary>
        private readonly PriorityRequestScheduler scheduler;

        /// <summary>
        /// 最大并发请求数缓存（用于 SetMaxConcurrentRequests 时重建信号量）。
        /// </summary>
        private int maxConcurrentRequests;

        /// <summary>
        /// 默认超时时间（毫秒），由 SetDefaultTimeout 注入。
        /// 每个请求的 TimeoutMs 为 0 时使用此值。
        /// </summary>
        private int defaultTimeoutMs;

        /// <summary>
        /// 默认重试次数，由 SetDefaultRetries 注入。
        /// </summary>
        private int maxRetries;

        /// <summary>
        /// 重试退避时间（毫秒），每次重试前等待以避免紧密重试压垮服务端。
        /// </summary>
        private const int RetryBackoffMilliseconds = 200;

        // ====== 活跃请求追踪（用于 Tag 管理） ======

        /// <summary>
        /// 活跃请求追踪项列表（包括排队中和进行中的请求）。
        /// 键：请求的 CancellationTokenSource；值：对应的 Tag。
        /// 使用 lock (trackedRequests) 保证线程安全。
        /// </summary>
        private readonly List<TrackedRequest> trackedRequests;

        /// <summary>
        /// 初始化 WebRequest 模块的新实例。
        /// </summary>
        public WebRequestModule()
        {
            maxConcurrentRequests = 5;
            defaultTimeoutMs = 30000;
            maxRetries = 0;
            scheduler = new PriorityRequestScheduler(maxConcurrentRequests);
            trackedRequests = new List<TrackedRequest>();
        }

        /// <summary>
        /// 获取框架模块优先级。
        /// 优先级 15：介于 Resource(50) 和 Timer(10) 之间。
        /// 确保通信层在资源系统初始化之后、定时器和事件系统之前就绪。
        /// </summary>
        internal override int Order
        {
            get { return 15; }
        }

        // ====== 配置注入 ======

        /// <inheritdoc/>
        public void SetHelper(IWebRequestHelper helper)
        {
            if (helper == null)
            {
                throw new RFrameworkException("WebRequestModule: helper is invalid.");
            }

            this.helper = helper;
        }

        /// <inheritdoc/>
        public void SetMaxConcurrentRequests(int max)
        {
            if (max < 0)
            {
                throw new RFrameworkException("WebRequestModule: maxConcurrentRequests must be >= 0.");
            }

            maxConcurrentRequests = max;

            scheduler.SetMaximum(max);
        }

        /// <inheritdoc/>
        public void SetDefaultTimeout(int timeoutMs)
        {
            if (timeoutMs < 0)
            {
                throw new RFrameworkException("WebRequestModule: timeoutMs must be >= 0.");
            }

            defaultTimeoutMs = timeoutMs;
        }

        /// <inheritdoc/>
        public void SetDefaultRetries(int retries)
        {
            if (retries < 0)
            {
                throw new RFrameworkException("WebRequestModule: retries must be >= 0.");
            }

            maxRetries = retries;
        }

        // ====== 请求计数 ======

        /// <inheritdoc/>
        public int ActiveRequestCount
        {
            get
            {
                lock (trackedRequests)
                {
                    int count = 0;
                    for (int i = 0; i < trackedRequests.Count; i++)
                    {
                        if (trackedRequests[i].IsActive)
                        {
                            count++;
                        }
                    }

                    return count;
                }
            }
        }

        /// <inheritdoc/>
        public int QueuedRequestCount
        {
            get
            {
                lock (trackedRequests)
                {
                    int count = 0;
                    for (int i = 0; i < trackedRequests.Count; i++)
                    {
                        if (!trackedRequests[i].IsActive)
                        {
                            count++;
                        }
                    }

                    return count;
                }
            }
        }

        // ====== GET ======

        /// <inheritdoc/>
        public Task<WebResponse> GetAsync(
            string url,
            Dictionary<string, string> queryParams = null,
            Dictionary<string, string> headers = null,
            string tag = null,
            uint priority = 0,
            CancellationToken ct = default)
        {
            string fullUrl = BuildQueryUrl(url, queryParams);
            WebRequestData request = new WebRequestData
            {
                Url = fullUrl,
                Method = HttpMethod.Get,
                Headers = headers,
                Tag = tag,
                Priority = priority,
                TimeoutMs = defaultTimeoutMs
            };
            return SendCoreAsync(request, null, ct);
        }

        // ====== POST ======

        /// <inheritdoc/>
        public Task<WebResponse> PostAsync(
            string url,
            string body,
            MimeType mimeType = MimeType.Json,
            Dictionary<string, string> headers = null,
            string tag = null,
            uint priority = 0,
            CancellationToken ct = default)
        {
            WebRequestData request = new WebRequestData
            {
                Url = url,
                Method = HttpMethod.Post,
                Headers = headers,
                Body = Encoding.UTF8.GetBytes(body ?? string.Empty),
                ContentType = GetMimeTypeString(mimeType),
                Tag = tag,
                Priority = priority,
                TimeoutMs = defaultTimeoutMs
            };
            return SendCoreAsync(request, null, ct);
        }

        /// <inheritdoc/>
        public Task<WebResponse> PostFormAsync(
            string url,
            Dictionary<string, string> formFields,
            Dictionary<string, string> headers = null,
            string tag = null,
            uint priority = 0,
            CancellationToken ct = default)
        {
            if (formFields == null)
            {
                throw new RFrameworkException("WebRequestModule: formFields is invalid.");
            }

            string formBody = BuildFormBody(formFields);
            WebRequestData request = new WebRequestData
            {
                Url = url,
                Method = HttpMethod.Post,
                Headers = headers,
                Body = Encoding.UTF8.GetBytes(formBody),
                ContentType = GetMimeTypeString(MimeType.FormUrlEncoded),
                Tag = tag,
                Priority = priority,
                TimeoutMs = defaultTimeoutMs
            };
            return SendCoreAsync(request, null, ct);
        }

        /// <inheritdoc/>
        public Task<WebResponse> PostMultipartAsync(
            string url,
            List<MultipartField> fields,
            Dictionary<string, string> headers = null,
            IProgress<float> progress = null,
            string tag = null,
            uint priority = 0,
            CancellationToken ct = default)
        {
            if (fields == null || fields.Count == 0)
            {
                throw new RFrameworkException("WebRequestModule: fields is invalid.");
            }

            string boundary = GenerateBoundary();
            byte[] body = BuildMultipartBody(fields, boundary);
            WebRequestData request = new WebRequestData
            {
                Url = url,
                Method = HttpMethod.Post,
                Headers = headers,
                Body = body,
                ContentType = "multipart/form-data; boundary=" + boundary,
                Tag = tag,
                Priority = priority,
                TimeoutMs = defaultTimeoutMs
            };
            return SendCoreAsync(request, progress, ct);
        }

        // ====== PUT ======

        /// <inheritdoc/>
        public Task<WebResponse> PutAsync(
            string url,
            string body,
            MimeType mimeType = MimeType.Json,
            Dictionary<string, string> headers = null,
            string tag = null,
            uint priority = 0,
            CancellationToken ct = default)
        {
            WebRequestData request = new WebRequestData
            {
                Url = url,
                Method = HttpMethod.Put,
                Headers = headers,
                Body = Encoding.UTF8.GetBytes(body ?? string.Empty),
                ContentType = GetMimeTypeString(mimeType),
                Tag = tag,
                Priority = priority,
                TimeoutMs = defaultTimeoutMs
            };
            return SendCoreAsync(request, null, ct);
        }

        // ====== DELETE ======

        /// <inheritdoc/>
        public Task<WebResponse> DeleteAsync(
            string url,
            string body = null,
            MimeType mimeType = MimeType.Json,
            Dictionary<string, string> headers = null,
            string tag = null,
            uint priority = 0,
            CancellationToken ct = default)
        {
            WebRequestData request = new WebRequestData
            {
                Url = url,
                Method = HttpMethod.Delete,
                Headers = headers,
                Body = body != null ? Encoding.UTF8.GetBytes(body) : null,
                ContentType = body != null ? GetMimeTypeString(mimeType) : null,
                Tag = tag,
                Priority = priority,
                TimeoutMs = defaultTimeoutMs
            };
            return SendCoreAsync(request, null, ct);
        }

        // ====== 原始请求 ======

        /// <inheritdoc/>
        public Task<WebResponse> SendAsync(
            WebRequestData request,
            IProgress<float> progress = null,
            CancellationToken ct = default)
        {
            if (request == null)
            {
                throw new RFrameworkException("WebRequestModule: request is invalid.");
            }

            return SendCoreAsync(request, progress, ct);
        }

        // ====== 便捷方法 ======

        /// <inheritdoc/>
        public async Task<byte[]> DownloadAsync(
            string url,
            IProgress<float> progress = null,
            Dictionary<string, string> headers = null,
            string tag = null,
            uint priority = 0,
            CancellationToken ct = default)
        {
            WebResponse response = await GetAsync(url, null, headers, tag, priority, ct);
            if (!response.IsSuccess)
            {
                throw new RFrameworkException(
                    string.Format("WebRequestModule: download failed, status={0}, error={1}",
                    response.StatusCode, response.ErrorMessage));
            }

            return response.Data;
        }

        /// <inheritdoc/>
        public async Task DownloadFileAsync(
            string url,
            string savePath,
            IProgress<float> progress = null,
            Dictionary<string, string> headers = null,
            string tag = null,
            uint priority = 0,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(savePath))
            {
                throw new RFrameworkException("WebRequestModule: savePath is invalid.");
            }

            WebRequestData request = new WebRequestData
            {
                Url = url,
                Method = HttpMethod.Get,
                Headers = headers,
                Tag = tag,
                Priority = priority,
                TimeoutMs = defaultTimeoutMs
            };

            try
            {
                IProgress<WebDownloadProgress> byteProgress = progress == null
                    ? null
                    : new FileProgressAdapter(progress);
                WebResponse response = await SendCoreDownloadAsync(request, savePath, false, byteProgress, ct);
                if (response == null || !response.IsSuccess)
                {
                    throw new RFrameworkException(string.Format(
                        "WebRequestModule: download file failed, status={0}, error={1}",
                        response?.StatusCode ?? 0,
                        response?.ErrorMessage ?? "No response"));
                }
            }
            catch (OperationCanceledException ex)
            {
                TryDeleteFile(savePath);
                throw new RFrameworkException("WebRequestModule: download file cancelled or timeout.", ex);
            }
            catch
            {
                TryDeleteFile(savePath);
                throw;
            }
        }

        /// <inheritdoc/>
        public Task<WebResponse> DownloadFileRangeAsync(
            string url,
            string savePath,
            long offset,
            IProgress<WebDownloadProgress> progress = null,
            Dictionary<string, string> headers = null,
            string tag = null,
            uint priority = 0,
            int timeoutMs = 0,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                throw new RFrameworkException("WebRequestModule: url is invalid.");
            }

            if (string.IsNullOrWhiteSpace(savePath))
            {
                throw new RFrameworkException("WebRequestModule: savePath is invalid.");
            }

            if (offset < 0)
            {
                throw new RFrameworkException("WebRequestModule: offset must be >= 0.");
            }

            if (timeoutMs < 0)
            {
                throw new RFrameworkException("WebRequestModule: timeoutMs must be >= 0.");
            }

            Dictionary<string, string> requestHeaders = headers == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
            if (offset > 0)
            {
                requestHeaders["Range"] = string.Format("bytes={0}-", offset);
            }
            else
            {
                requestHeaders.Remove("Range");
            }

            WebRequestData request = new WebRequestData
            {
                Url = url,
                Method = HttpMethod.Get,
                Headers = requestHeaders,
                Tag = tag,
                Priority = priority,
                TimeoutMs = timeoutMs
            };

            return SendCoreDownloadAsync(request, savePath, offset > 0, progress, ct);
        }

        /// <inheritdoc/>
        public async Task<T> GetJsonAsync<T>(
            string url,
            Dictionary<string, string> queryParams = null,
            Dictionary<string, string> headers = null,
            string tag = null,
            CancellationToken ct = default)
        {
            WebResponse response = await GetAsync(url, queryParams, headers, tag, 0, ct);
            if (!response.IsSuccess)
            {
                throw new RFrameworkException(
                    string.Format("WebRequestModule: GetJsonAsync failed, status={0}, error={1}",
                    response.StatusCode, response.ErrorMessage));
            }

            return Utility.Json.ToObject<T>(response.Text);
        }

        // ====== Tag 管理 ======

        /// <inheritdoc/>
        public void CancelAllByTag(string tag)
        {
            lock (trackedRequests)
            {
                for (int i = trackedRequests.Count - 1; i >= 0; i--)
                {
                    TrackedRequest tracked = trackedRequests[i];
                    if (tag == null || string.Equals(tracked.Tag, tag, StringComparison.Ordinal))
                    {
                        try
                        {
                            tracked.Cts.Cancel();
                        }
                        catch (ObjectDisposedException)
                        {
                            // CTS 已释放，忽略
                        }
                    }
                }
            }
        }

        // ====== 核心发送逻辑 ======

        /// <summary>
        /// 发送请求的核心方法，所有公开 API 最终汇聚于此。
        /// 负责：并发控制 → 超时+重试 → 请求执行 → 结果返回。
        /// </summary>
        private async Task<WebResponse> SendCoreAsync(
            WebRequestData request,
            IProgress<float> progress,
            CancellationToken userCt)
        {
            if (helper == null)
            {
                throw new RFrameworkException("WebRequestModule: helper is not set. Call SetHelper first.");
            }

            CancellationTokenSource mainCts = null;
            CancellationTokenSource linkedCts = null;
            CancellationTokenSource timeoutCts = null;
            TrackedRequest tracked = null;
            bool acquiredSlot = false;
            PriorityRequestScheduler requestScheduler = null;

            try
            {
                // 1. 创建主 CTS（用于 CancelAllByTag）
                mainCts = new CancellationTokenSource();
                tracked = new TrackedRequest(mainCts, request.Tag);

                // 2. 注册到追踪列表
                lock (trackedRequests)
                {
                    trackedRequests.Add(tracked);
                }

                // 3. 链接用户 CTS + 超时 CTS + 主 CTS
                if (request.TimeoutMs > 0)
                {
                    timeoutCts = new CancellationTokenSource(request.TimeoutMs);
                    linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                        userCt, mainCts.Token, timeoutCts.Token);
                }
                else
                {
                    linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                        userCt, mainCts.Token);
                }

                // 4. 等待并发槽位。PriorityRequestScheduler 按优先级、同级 FIFO 调度。
                requestScheduler = scheduler;
                await requestScheduler.WaitAsync(request.Priority, linkedCts.Token);
                acquiredSlot = true;
                tracked.SetActive(true);

                // 5. 带重试的请求循环
                WebResponse lastResponse = null;
                int remainingRetries = maxRetries;

                while (true)
                {
                    try
                    {
                        lastResponse = await helper.SendAsync(request, progress, linkedCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        WebRequestError cancelError;
                        if (userCt.IsCancellationRequested)
                        {
                            cancelError = WebRequestError.Aborted;
                        }
                        else if (mainCts.IsCancellationRequested)
                        {
                            cancelError = WebRequestError.Aborted;
                        }
                        else
                        {
                            cancelError = WebRequestError.RequestTimeout;
                        }

                        lastResponse = new WebResponse(0, null, null, null,
                            cancelError, "Request was cancelled or timed out.");
                        // 取消不重试
                        return lastResponse;
                    }

                    // 判断是否需要重试
                    if (lastResponse != null && ShouldRetry(lastResponse.Error, lastResponse.StatusCode, ref remainingRetries))
                    {
                        // 重试前退避，避免紧密重试压垮服务端
                        await Task.Delay(RetryBackoffMilliseconds, linkedCts.Token);
                        continue;
                    }

                    break;
                }

                return lastResponse;
            }
            finally
            {
                // 6. 清理
                if (linkedCts != null)
                {
                    linkedCts.Dispose();
                }

                // 超时 CTS 独立创建，需显式释放，否则每次带超时的请求都泄漏一个带定时器的 CTS
                timeoutCts?.Dispose();

                // 从追踪列表移除
                lock (trackedRequests)
                {
                    if (tracked != null)
                    {
                        trackedRequests.Remove(tracked);
                    }
                }

                // 仅当成功获取槽位时才释放，避免释放未持有的槽位污染计数
                if (acquiredSlot && requestScheduler != null)
                {
                    requestScheduler.Release();
                }

                // 释放主 CTS（与槽位释放解耦，无论是否获取槽位都需清理）
                if (mainCts != null)
                {
                    try
                    {
                        mainCts.Dispose();
                    }
                    catch
                    {
                        // 忽略
                    }
                }
            }
        }

        /// <summary>
        /// 判断是否应该重试。
        /// 重试条件：连接超时、请求超时、网络错误、HTTP 5xx。
        /// 不重试：用户取消、HTTP 4xx（客户端错误无意义重试）。
        /// </summary>
        private bool ShouldRetry(WebRequestError error, int statusCode, ref int remainingRetries)
        {
            if (remainingRetries <= 0)
            {
                return false;
            }

            switch (error)
            {
                case WebRequestError.ConnectionTimeout:
                case WebRequestError.RequestTimeout:
                case WebRequestError.NetworkError:
                    remainingRetries--;
                    return true;

                case WebRequestError.HttpError:
                    // 仅 5xx 重试
                    if (statusCode >= 500 && statusCode < 600)
                    {
                        remainingRetries--;
                        return true;
                    }

                    return false;

                default:
                    return false;
            }
        }

        // ====== URL 构建工具 ======

        /// <summary>
        /// 将查询参数字典拼接到 URL。
        /// </summary>
        private static string BuildQueryUrl(string url, Dictionary<string, string> queryParams)
        {
            if (queryParams == null || queryParams.Count == 0)
            {
                return url;
            }

            StringBuilder sb = new StringBuilder(url);
            bool hasQuery = url.Contains("?");

            foreach (KeyValuePair<string, string> kv in queryParams)
            {
                if (string.IsNullOrEmpty(kv.Key))
                {
                    continue;
                }

                sb.Append(hasQuery ? "&" : "?");
                hasQuery = true;
                sb.Append(Uri.EscapeDataString(kv.Key));
                sb.Append("=");
                sb.Append(kv.Value != null ? Uri.EscapeDataString(kv.Value) : string.Empty);
            }

            return sb.ToString();
        }

        /// <summary>
        /// 将表单字段字典构建为 application/x-www-form-urlencoded 字符串。
        /// </summary>
        private static string BuildFormBody(Dictionary<string, string> formFields)
        {
            StringBuilder sb = new StringBuilder();
            bool first = true;

            foreach (KeyValuePair<string, string> kv in formFields)
            {
                if (!first)
                {
                    sb.Append("&");
                }

                first = false;
                sb.Append(Uri.EscapeDataString(kv.Key));
                sb.Append("=");
                sb.Append(kv.Value != null ? Uri.EscapeDataString(kv.Value) : string.Empty);
            }

            return sb.ToString();
        }

        /// <summary>
        /// 将 MultipartField 列表构建为 multipart/form-data 字节数组。
        /// </summary>
        private static byte[] BuildMultipartBody(List<MultipartField> fields, string boundary)
        {
            List<byte> bodyBytes = new List<byte>();
            string boundaryLine = "--" + boundary;
            string crlf = "\r\n";
            byte[] crlfBytes = Encoding.UTF8.GetBytes(crlf);

            for (int i = 0; i < fields.Count; i++)
            {
                MultipartField field = fields[i];

                // --boundary
                byte[] boundaryBytes = Encoding.UTF8.GetBytes(boundaryLine + crlf);
                bodyBytes.AddRange(boundaryBytes);

                if (field.IsFile)
                {
                    // Content-Disposition: form-data; name="xxx"; filename="xxx"
                    string disposition = string.Format(
                        "Content-Disposition: form-data; name=\"{0}\"; filename=\"{1}\"{2}",
                        field.Name, field.FileName, crlf);
                    bodyBytes.AddRange(Encoding.UTF8.GetBytes(disposition));

                    // Content-Type: application/pdf
                    string contentType = string.Format("Content-Type: {0}{1}{2}",
                        field.ContentType ?? "application/octet-stream", crlf, crlf);
                    bodyBytes.AddRange(Encoding.UTF8.GetBytes(contentType));

                    // 文件数据
                    bodyBytes.AddRange(field.Data);
                    bodyBytes.AddRange(crlfBytes);
                }
                else
                {
                    // Content-Disposition: form-data; name="xxx"
                    string disposition = string.Format(
                        "Content-Disposition: form-data; name=\"{0}\"{1}{2}{3}",
                        field.Name, crlf, crlf, field.Value ?? string.Empty);
                    bodyBytes.AddRange(Encoding.UTF8.GetBytes(disposition));
                    bodyBytes.AddRange(crlfBytes);
                }
            }

            // 结束 boundary
            byte[] endBytes = Encoding.UTF8.GetBytes(boundaryLine + "--" + crlf);
            bodyBytes.AddRange(endBytes);

            return bodyBytes.ToArray();
        }

        /// <summary>
        /// 生成唯一的 multipart boundary 字符串。
        /// </summary>
        private static string GenerateBoundary()
        {
            return "----WebRequestBoundary" + Guid.NewGuid().ToString("N");
        }

        /// <summary>
        /// 将 <see cref="MimeType"/> 枚举映射为标准 MIME 字符串。
        /// </summary>
        private static string GetMimeTypeString(MimeType mimeType)
        {
            switch (mimeType)
            {
                case MimeType.Json:
                    return "application/json";
                case MimeType.Xml:
                    return "application/xml";
                case MimeType.FormUrlEncoded:
                    return "application/x-www-form-urlencoded";
                case MimeType.TextPlain:
                    return "text/plain";
                case MimeType.OctetStream:
                    return "application/octet-stream";
                default:
                    return "application/json";
            }
        }

        // ====== 生命周期 ======

        /// <summary>
        /// 模块轮询。WebRequest 模块不需要 Update 驱动，空实现。
        /// </summary>
        internal override void Tick(float elapseSeconds, float realElapseSeconds)
        {
            // WebRequest 使用 Task 异步，不需要轮询驱动
        }

        /// <summary>
        /// 关闭模块，取消所有活跃请求并释放资源。
        /// </summary>
        internal override void Stop()
        {
            CancelAllByTag(null);

            lock (trackedRequests)
            {
                trackedRequests.Clear();
            }

            scheduler.Dispose();
            helper = null;
        }

        // ====== 下载核心 ======

        /// <summary>
        /// 流式下载核心：受优先级调度器并发控制，直接写入磁盘。
        /// 不支持重试（大文件重试浪费流量）。
        /// </summary>
        private async Task<WebResponse> SendCoreDownloadAsync(
            WebRequestData request,
            string savePath,
            bool append,
            IProgress<WebDownloadProgress> progress,
            CancellationToken userCt)
        {
            if (helper == null)
            {
                throw new RFrameworkException("WebRequestModule: helper is not set. Call SetHelper first.");
            }

            CancellationTokenSource mainCts = null;
            CancellationTokenSource linkedCts = null;
            CancellationTokenSource timeoutCts = null;
            TrackedRequest tracked = null;
            bool acquiredSlot = false;
            PriorityRequestScheduler requestScheduler = null;

            try
            {
                mainCts = new CancellationTokenSource();
                tracked = new TrackedRequest(mainCts, request.Tag);

                lock (trackedRequests)
                {
                    trackedRequests.Add(tracked);
                }

                if (request.TimeoutMs > 0)
                {
                    timeoutCts = new CancellationTokenSource(request.TimeoutMs);
                    linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                        userCt, mainCts.Token, timeoutCts.Token);
                }
                else
                {
                    linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                        userCt, mainCts.Token);
                }

                requestScheduler = scheduler;
                await requestScheduler.WaitAsync(request.Priority, linkedCts.Token);
                acquiredSlot = true;
                tracked.SetActive(true);

                return await helper.DownloadFileAsync(request, savePath, append, progress, linkedCts.Token);
            }
            finally
            {
                if (acquiredSlot && requestScheduler != null)
                {
                    requestScheduler.Release();
                }
                mainCts?.Dispose();
                linkedCts?.Dispose();
                // 超时 CTS 独立创建，需显式释放
                timeoutCts?.Dispose();
                lock (trackedRequests)
                {
                    if (tracked != null)
                    {
                        trackedRequests.Remove(tracked);
                    }
                }
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (System.IO.File.Exists(path))
                {
                    System.IO.File.Delete(path);
                }
            }
            catch
            {
                // 原始下载 API 保持尽力清理语义，删除失败不覆盖主异常。
            }
        }

        private sealed class FileProgressAdapter : IProgress<WebDownloadProgress>
        {
            private readonly IProgress<float> progress;

            public FileProgressAdapter(IProgress<float> progress)
            {
                this.progress = progress;
            }

            public void Report(WebDownloadProgress value)
            {
                progress.Report(value?.Progress ?? 0f);
            }
        }

        // ====== 内部调度与追踪结构 ======

        private sealed class PriorityRequestScheduler : IDisposable
        {
            private readonly object syncRoot = new object();
            private readonly List<Waiter> waiters = new List<Waiter>();
            private int maximum;
            private int activeCount;
            private long sequence;
            private bool disposed;

            public PriorityRequestScheduler(int maximum)
            {
                this.maximum = maximum;
            }

            public Task WaitAsync(uint priority, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                lock (syncRoot)
                {
                    ThrowIfDisposed();
                    if (maximum <= 0 || activeCount < maximum)
                    {
                        activeCount++;
                        return Task.CompletedTask;
                    }

                    Waiter waiter = new Waiter(priority, sequence++);
                    waiters.Add(waiter);
                    waiter.Registration = ct.Register(() => Cancel(waiter));
                    if (waiter.Completion.Task.IsCanceled)
                    {
                        waiter.Registration.Dispose();
                    }

                    return waiter.Completion.Task;
                }
            }

            public void Release()
            {
                Waiter next = null;
                lock (syncRoot)
                {
                    if (activeCount <= 0)
                    {
                        throw new RFrameworkException("WebRequest scheduler released a slot that was not acquired.");
                    }

                    activeCount--;
                    next = TakeNextUnsafe();
                    if (next != null)
                    {
                        activeCount++;
                    }
                }

                Complete(next);
            }

            public void SetMaximum(int value)
            {
                List<Waiter> ready = new List<Waiter>();
                lock (syncRoot)
                {
                    ThrowIfDisposed();
                    maximum = value;
                    while (maximum <= 0 || activeCount < maximum)
                    {
                        Waiter next = TakeNextUnsafe();
                        if (next == null)
                        {
                            break;
                        }

                        activeCount++;
                        ready.Add(next);
                    }
                }

                for (int i = 0; i < ready.Count; i++)
                {
                    Complete(ready[i]);
                }
            }

            public void Dispose()
            {
                List<Waiter> cancelled;
                lock (syncRoot)
                {
                    if (disposed)
                    {
                        return;
                    }

                    disposed = true;
                    cancelled = new List<Waiter>(waiters);
                    waiters.Clear();
                }

                for (int i = 0; i < cancelled.Count; i++)
                {
                    cancelled[i].Registration.Dispose();
                    cancelled[i].Completion.TrySetException(
                        new ObjectDisposedException(nameof(PriorityRequestScheduler)));
                }
            }

            private void Cancel(Waiter waiter)
            {
                bool removed;
                lock (syncRoot)
                {
                    removed = waiters.Remove(waiter);
                }

                if (removed)
                {
                    waiter.Completion.TrySetCanceled();
                    waiter.Registration.Dispose();
                }
            }

            private Waiter TakeNextUnsafe()
            {
                if (waiters.Count == 0)
                {
                    return null;
                }

                int selectedIndex = 0;
                for (int i = 1; i < waiters.Count; i++)
                {
                    Waiter candidate = waiters[i];
                    Waiter selected = waiters[selectedIndex];
                    if (candidate.Priority > selected.Priority ||
                        (candidate.Priority == selected.Priority && candidate.Sequence < selected.Sequence))
                    {
                        selectedIndex = i;
                    }
                }

                Waiter next = waiters[selectedIndex];
                waiters.RemoveAt(selectedIndex);
                return next;
            }

            private static void Complete(Waiter waiter)
            {
                if (waiter == null)
                {
                    return;
                }

                waiter.Registration.Dispose();
                waiter.Completion.TrySetResult(true);
            }

            private void ThrowIfDisposed()
            {
                if (disposed)
                {
                    throw new ObjectDisposedException(nameof(PriorityRequestScheduler));
                }
            }

            private sealed class Waiter
            {
                public readonly uint Priority;
                public readonly long Sequence;
                public readonly TaskCompletionSource<bool> Completion =
                    new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                public CancellationTokenRegistration Registration;

                public Waiter(uint priority, long sequence)
                {
                    Priority = priority;
                    Sequence = sequence;
                }
            }
        }

        /// <summary>
        /// 内部请求追踪项，用于 Tag 分组取消和活跃状态统计。
        /// </summary>
        private sealed class TrackedRequest
        {
            /// <summary>该请求的 CancellationTokenSource。</summary>
            public CancellationTokenSource Cts { get; }

            /// <summary>请求标签（可为 null）。</summary>
            public string Tag { get; }

            /// <summary>是否已进入活跃状态（获得并发槽位）。</summary>
            public bool IsActive { get; private set; }

            public TrackedRequest(CancellationTokenSource cts, string tag)
            {
                Cts = cts;
                Tag = tag;
                IsActive = false;
            }

            public void SetActive(bool active)
            {
                IsActive = active;
            }
        }
    }
}
