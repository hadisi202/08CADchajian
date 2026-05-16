using System;
using System.IO;
using System.Text;

namespace FurniturePlugin
{
    /// <summary>
    /// 插件日志服务，替代 Debug.WriteLine 和空 catch 块
    /// 日志文件位于 %AppData%/FurniturePlugin/Logs/
    /// </summary>
    public static class PluginLogger
    {
        private static readonly object _lock = new object();
        private static readonly string _logDirectory;
        private static readonly int _maxLogFiles = 10;

        static PluginLogger()
        {
            _logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FurniturePlugin", "Logs");
            try
            {
                if (!Directory.Exists(_logDirectory))
                    Directory.CreateDirectory(_logDirectory);
                CleanupOldLogs();
            }
            catch
            {
                // 日志初始化失败不应阻止插件运行
            }
        }

        /// <summary>
        /// 记录信息级别日志
        /// </summary>
        public static void Info(string message)
        {
            WriteLog("INFO", message);
        }

        /// <summary>
        /// 记录警告级别日志
        /// </summary>
        public static void Warning(string message)
        {
            WriteLog("WARN", message);
        }

        /// <summary>
        /// 记录错误级别日志
        /// </summary>
        public static void Error(string message)
        {
            WriteLog("ERROR", message);
        }

        /// <summary>
        /// 记录错误级别日志（含异常详情）
        /// </summary>
        public static void Error(string message, Exception ex)
        {
            var sb = new StringBuilder();
            sb.AppendLine(message);
            sb.AppendLine($"  异常类型: {ex.GetType().FullName}");
            sb.AppendLine($"  异常消息: {ex.Message}");
            sb.AppendLine($"  堆栈跟踪:\n{ex.StackTrace}");
            if (ex.InnerException != null)
            {
                sb.AppendLine($"  内部异常: {ex.InnerException.Message}");
                sb.AppendLine($"  内部堆栈:\n{ex.InnerException.StackTrace}");
            }
            WriteLog("ERROR", sb.ToString());
        }

        /// <summary>
        /// 记录调试级别日志（仅在 DEBUG 模式下输出到文件）
        /// </summary>
        [System.Diagnostics.Conditional("DEBUG")]
        public static void Debug(string message)
        {
            WriteLog("DEBUG", message);
        }

        private static void WriteLog(string level, string message)
        {
            try
            {
                lock (_lock)
                {
                    var logFile = Path.Combine(_logDirectory, $"log_{DateTime.Now:yyyyMMdd}.txt");
                    var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    var line = $"[{timestamp}] [{level}] {message}{Environment.NewLine}";
                    File.AppendAllText(logFile, line, Encoding.UTF8);
                }
            }
            catch
            {
                // 日志写入失败不应影响主流程
            }
        }

        private static void CleanupOldLogs()
        {
            try
            {
                var files = Directory.GetFiles(_logDirectory, "log_*.txt");
                if (files.Length > _maxLogFiles)
                {
                    Array.Sort(files);
                    var toDelete = files.Length - _maxLogFiles;
                    for (int i = 0; i < toDelete; i++)
                    {
                        try { File.Delete(files[i]); } catch { }
                    }
                }
            }
            catch
            {
                // 清理失败不影响运行
            }
        }
    }
}
