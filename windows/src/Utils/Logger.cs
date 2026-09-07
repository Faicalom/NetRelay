using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NetRelay.Utils
{
    /// <summary>
    /// Centralized logging utility for the application.
    /// Provides consistent logging across all modules.
    /// </summary>
    public class Logger
    {
        private static readonly Lazy<Logger> _instance = new(() => new Logger());
        public static Logger Instance => _instance.Value;

        public class LogEntry
        {
            public string Timestamp { get; set; } = string.Empty;
            public string Level { get; set; } = string.Empty;
            public string Tag { get; set; } = string.Empty;
            public string Message { get; set; } = string.Empty;
            public Exception? Exception { get; set; }
        }

        private readonly List<LogEntry> _logHistory = new();
        private const int MaxLogHistory = 500;
        private readonly string _logFilePath;
        private volatile bool _detailedLogging;

        private Logger()
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NetRelay",
                "logs");
            Directory.CreateDirectory(logDirectory);
            _logFilePath = Path.Combine(logDirectory, $"netrelay-{DateTime.Now:yyyyMMdd}.log");
        }

        public void Debug(string tag, string message)
        {
            var entry = CreateLogEntry("DEBUG", tag, message);
            System.Diagnostics.Debug.WriteLine(FormatLogLine(entry));
            if (_detailedLogging)
            {
                AddToHistory(entry);
            }
        }

        public void SetDetailedLogging(bool enabled) => _detailedLogging = enabled;

        public void Info(string tag, string message)
        {
            var entry = CreateLogEntry("INFO", tag, message);
            System.Diagnostics.Debug.WriteLine(FormatLogLine(entry));
            AddToHistory(entry);
        }

        public void Warn(string tag, string message, Exception? ex = null)
        {
            var entry = CreateLogEntry("WARN", tag, message, ex);
            System.Diagnostics.Debug.WriteLine(FormatLogLine(entry));
            AddToHistory(entry);
        }

        public void Error(string tag, string message, Exception? ex = null)
        {
            var entry = CreateLogEntry("ERROR", tag, message, ex);
            System.Diagnostics.Debug.WriteLine(FormatLogLine(entry));
            AddToHistory(entry);
        }

        private LogEntry CreateLogEntry(
            string level,
            string tag,
            string message,
            Exception? ex = null)
        {
            return new LogEntry
            {
                Timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
                Level = level,
                Tag = tag,
                Message = message,
                Exception = ex
            };
        }

        private void AddToHistory(LogEntry entry)
        {
            lock (_logHistory)
            {
                _logHistory.Add(entry);
                if (_logHistory.Count > MaxLogHistory)
                {
                    _logHistory.RemoveAt(0);
                }
            }

            WriteToDisk(entry);
        }

        public List<LogEntry> GetHistory()
        {
            lock (_logHistory)
            {
                return new List<LogEntry>(_logHistory);
            }
        }

        public void ClearHistory()
        {
            lock (_logHistory)
            {
                _logHistory.Clear();
            }
        }

        public string ExportLogs()
        {
            lock (_logHistory)
            {
                var lines = new List<string>();
                foreach (var entry in _logHistory)
                {
                    lines.Add(FormatLogLine(entry));
                }
                return string.Join(Environment.NewLine, lines);
            }
        }

        private static string FormatLogLine(LogEntry entry)
        {
            var line = $"[{entry.Timestamp}] {entry.Level} [{entry.Tag}] {entry.Message}";
            if (entry.Exception != null)
            {
                line += $" - {entry.Exception.Message}";
            }

            return line;
        }

        private void WriteToDisk(LogEntry entry)
        {
            try
            {
                File.AppendAllText(
                    _logFilePath,
                    FormatLogLine(entry) + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch
            {
                // Logging must never break the connection workflow.
            }
        }
    }
}
