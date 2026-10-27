using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace BatchOptimizer
{
    /// <summary>日志级别。</summary>
    public enum BatchLogLevel
    {
        Debug = 0,
        Info = 1,
        Warning = 2,
        Error = 3,
    }

    /// <summary>
    /// 合批优化器的统一日志工具。
    /// 同时输出到 Unity Console 与日志文件，方便纠错。
    /// 文件路径：编辑器下为 &lt;工程根目录&gt;/Logs/BatchOptimizer.log；运行时为 persistentDataPath/BatchOptimizer.log。
    /// </summary>
    public static class BatchLogger
    {
        /// <summary>是否输出到 Console。</summary>
        public static bool EnableConsole = true;

        /// <summary>是否写入日志文件。</summary>
        public static bool EnableFile = true;

        /// <summary>低于该级别的日志不输出。</summary>
        public static BatchLogLevel MinLevel = BatchLogLevel.Info;

        private const string LogTag = "[BatchOptimizer]";

        private static StreamWriter _writer;
        private static string _logFilePath;

        /// <summary>当前日志文件完整路径。</summary>
        public static string GetLogPath()
        {
            if (_logFilePath == null)
            {
#if UNITY_EDITOR
                _logFilePath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "BatchOptimizer.log"));
#else
                _logFilePath = Path.Combine(Application.persistentDataPath, "BatchOptimizer.log");
#endif
            }
            return _logFilePath;
        }

        private static void EnsureWriter()
        {
            if (_writer != null) return;
            try
            {
                string path = GetLogPath();
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                _writer = new StreamWriter(path, append: true, Encoding.UTF8);
                _writer.AutoFlush = true;
                WriteRaw("========== Session Start: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                         " | Unity " + Application.unityVersion + " ==========");
            }
            catch (Exception e)
            {
                _writer = null;
                if (EnableConsole)
                    Debug.LogWarning(LogTag + " 无法创建日志文件 " + GetLogPath() + "：" + e.Message);
            }
        }

        private static void WriteRaw(string line)
        {
            if (_writer != null)
            {
                _writer.WriteLine(line);
            }
        }

        private static void Log(BatchLogLevel level, object message)
        {
            if ((int)level < (int)MinLevel) return;

            if (EnableFile)
            {
                EnsureWriter();
                WriteRaw(DateTime.Now.ToString("HH:mm:ss.fff") + " [" + level + "] " + message);
            }

            if (EnableConsole)
            {
                switch (level)
                {
                    case BatchLogLevel.Debug:
                    case BatchLogLevel.Info:
                        Debug.Log(LogTag + " " + message);
                        break;
                    case BatchLogLevel.Warning:
                        Debug.LogWarning(LogTag + " " + message);
                        break;
                    case BatchLogLevel.Error:
                        Debug.LogError(LogTag + " " + message);
                        break;
                }
            }
        }

        public static void DebugLog(object message) { Log(BatchLogLevel.Debug, message); }
        public static void Info(object message) { Log(BatchLogLevel.Info, message); }
        public static void Warn(object message) { Log(BatchLogLevel.Warning, message); }
        public static void Error(object message) { Log(BatchLogLevel.Error, message); }

        public static void Flush()
        {
            try { _writer?.Flush(); } catch { /* ignore */ }
        }
    }
}
