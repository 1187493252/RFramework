using System;
using System.Threading;
using System.Threading.Tasks;

namespace RFramework
{
    /// <summary>
    /// WebRequest 辅助器接口（跨引擎桥接层）。
    /// 封装底层 HTTP 实现（UnityWebRequest / HttpClient / Godot HTTPRequest），
    /// 向上层 Library 模块暴露统一的 TAP 异步接口。
    /// </summary>
    /// <remarks>
    /// 接口设计约束：
    /// - 纯 C#，零 UnityEngine 或第三方 HTTP 库依赖。
    /// - 使用 Task 和 .NET Standard 2.0 内置类型，保证跨引擎兼容。
    /// - Runtime 层通过 <see cref="WebRequestHelperBase"/> 实现此接口并注入到 WebRequestModule。
    /// </remarks>
    public interface IWebRequestHelper
    {
        /// <summary>
        /// 执行一次原始 HTTP 请求并返回响应。
        /// 此方法由 WebRequestModule 的并发调度器调用，Helper 实现必须是线程安全的。
        /// </summary>
        /// <param name="request">完整的请求数据（URL、方法、头、Body 等）。</param>
        /// <param name="append">是否追加到已有文件，用于 HTTP Range 断点续传。</param>
        /// <param name="progress">文件字节进度报告器，可为 null。</param>
        /// <param name="ct">取消令牌，在超时或用户取消时触发。</param>
        /// <returns>HTTP 响应，包含状态码、头、响应体和错误分类。</returns>
        Task<WebResponse> SendAsync(WebRequestData request, IProgress<float> progress, CancellationToken ct);

        /// <summary>
        /// 流式下载文件到磁盘（不经过内存缓存，适合大文件）。
        /// </summary>
        /// <param name="request">完整的请求数据。</param>
        /// <param name="savePath">保存到磁盘的目标路径。</param>
        /// <param name="progress">下载进度报告器（0.0 ~ 1.0），可为 null。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>HTTP 响应，包含状态码和响应头。</returns>
        Task<WebResponse> DownloadFileAsync(
            WebRequestData request,
            string savePath,
            bool append,
            IProgress<WebDownloadProgress> progress,
            CancellationToken ct);
    }
}
