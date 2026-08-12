using System;

namespace RFramework
{
    /// <summary>
    /// 接收框架已经格式化完成的日志消息。
    /// </summary>
    public interface ILogHelper : IDisposable
    {
        /// <summary>
        /// 写入一条日志。
        /// </summary>
        /// <param name="level">日志级别。</param>
        /// <param name="message">日志文本。</param>
        void Write(LogLevel level, string message);
    }
}
