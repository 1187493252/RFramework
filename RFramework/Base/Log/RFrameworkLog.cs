using System;
using System.Globalization;

namespace RFramework
{
    /// <summary>
    /// Library 层日志桥接入口。具体输出由 Runtime 安装的 <see cref="ILogHelper"/> 提供。
    /// </summary>
    public static class RFrameworkLog
    {
        private static readonly object Gate = new object();
        private static ILogHelper helper;

        /// <summary>
        /// 获取当前是否已安装日志辅助器。
        /// </summary>
        public static bool IsInitialized
        {
            get
            {
                lock (Gate)
                {
                    return helper != null;
                }
            }
        }

        /// <summary>
        /// 安装日志辅助器，并释放之前的辅助器。
        /// </summary>
        /// <param name="value">新的日志辅助器。</param>
        public static void SetHelper(ILogHelper value)
        {
            if (value == null)
            {
                throw new RFrameworkException("Log helper cannot be null.");
            }

            ILogHelper previous;
            lock (Gate)
            {
                previous = helper;
                helper = value;
            }

            if (!ReferenceEquals(previous, value))
            {
                DisposeHelper(previous);
            }
        }

        /// <summary>
        /// 移除并释放当前日志辅助器。
        /// </summary>
        public static void Clear()
        {
            ILogHelper previous;
            lock (Gate)
            {
                previous = helper;
                helper = null;
            }

            DisposeHelper(previous);
        }

        /// <summary>
        /// 写入一条日志；未安装辅助器时抛出框架异常。
        /// </summary>
        /// <param name="level">日志级别。</param>
        /// <param name="message">日志内容。</param>
        public static void Write(LogLevel level, object message)
        {
            GetRequiredHelper().Write(level, message?.ToString() ?? "null");
        }

        /// <summary>
        /// 格式化并写入一条日志；未安装辅助器时抛出框架异常。
        /// </summary>
        /// <param name="level">日志级别。</param>
        /// <param name="format">复合格式字符串。</param>
        /// <param name="args">格式化参数。</param>
        public static void Write(LogLevel level, string format, params object[] args)
        {
            if (format == null)
            {
                throw new RFrameworkException("Log format cannot be null.");
            }

            GetRequiredHelper().Write(
                level, string.Format(CultureInfo.InvariantCulture, format, args));
        }

        /// <summary>
        /// 尝试写入日志。框架尚未启动、已经关闭或辅助器失败时返回 false。
        /// </summary>
        /// <param name="level">日志级别。</param>
        /// <param name="message">日志内容。</param>
        /// <returns>成功交给辅助器时返回 true。</returns>
        public static bool TryWrite(LogLevel level, object message)
        {
            return TryWriteCore(level, message?.ToString() ?? "null");
        }

        /// <summary>
        /// 尝试格式化并写入日志。
        /// </summary>
        /// <param name="level">日志级别。</param>
        /// <param name="format">复合格式字符串。</param>
        /// <param name="args">格式化参数。</param>
        /// <returns>成功交给辅助器时返回 true。</returns>
        public static bool TryWrite(LogLevel level, string format, params object[] args)
        {
            if (format == null)
            {
                return false;
            }

            string message;
            try
            {
                message = string.Format(CultureInfo.InvariantCulture, format, args);
            }
            catch (FormatException)
            {
                return false;
            }

            return TryWriteCore(level, message);
        }

        private static ILogHelper GetRequiredHelper()
        {
            lock (Gate)
            {
                return helper ?? throw new RFrameworkException(
                    "No log helper is installed. Initialize the framework before writing logs.");
            }
        }

        private static bool TryWriteCore(LogLevel level, string message)
        {
            ILogHelper current;
            lock (Gate)
            {
                current = helper;
            }

            if (current == null)
            {
                return false;
            }

            try
            {
                current.Write(level, message);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void DisposeHelper(ILogHelper value)
        {
            if (value == null)
            {
                return;
            }

            try
            {
                value.Dispose();
            }
            catch
            {
                // 日志后端关闭失败不能阻止框架继续替换或清理全局辅助器。
            }
        }
    }
}
