package com.netrelay.connectivity.util

import android.util.Log
import java.text.SimpleDateFormat
import java.util.concurrent.CopyOnWriteArrayList
import java.util.Date
import java.util.Locale

/**
 * Centralized logging utility for the application.
 * Provides consistent logging across all modules.
 */
object Logger {

    private const val TAG = "NetRelay"
    private val dateFormat = SimpleDateFormat("HH:mm:ss.SSS", Locale.US)

    data class LogEntry(
        val timestamp: String,
        val level: String,
        val tag: String,
        val message: String,
        val throwable: Throwable? = null
    )

    private val logHistory = CopyOnWriteArrayList<LogEntry>()
    private const val MAX_LOG_HISTORY = 1000

    fun debug(tag: String, message: String) {
        val entry = createLogEntry("DEBUG", tag, message)
        Log.d(TAG, entry.toString())
        addToHistory(entry)
    }

    fun info(tag: String, message: String) {
        val entry = createLogEntry("INFO", tag, message)
        Log.i(TAG, entry.toString())
        addToHistory(entry)
    }

    fun warn(tag: String, message: String, throwable: Throwable? = null) {
        val entry = createLogEntry("WARN", tag, message, throwable)
        Log.w(TAG, entry.toString(), throwable)
        addToHistory(entry)
    }

    fun error(tag: String, message: String, throwable: Throwable? = null) {
        val entry = createLogEntry("ERROR", tag, message, throwable)
        Log.e(TAG, entry.toString(), throwable)
        addToHistory(entry)
    }

    private fun createLogEntry(
        level: String,
        tag: String,
        message: String,
        throwable: Throwable? = null
    ): LogEntry {
        return LogEntry(
            timestamp = synchronized(dateFormat) { dateFormat.format(Date()) },
            level = level,
            tag = tag,
            message = message,
            throwable = throwable
        )
    }

    private fun addToHistory(entry: LogEntry) {
        logHistory.add(entry)
        while (logHistory.size > MAX_LOG_HISTORY) {
            logHistory.removeAt(0)
        }
    }

    fun getHistory(): List<LogEntry> = logHistory.toList()

    fun clearHistory() {
        logHistory.clear()
    }

    fun exportLogs(): String {
        return exportLogs(logHistory)
    }

    fun exportLogs(entries: Collection<LogEntry>): String {
        return entries.joinToString("\n") { entry ->
            "[${entry.timestamp}] ${entry.level} [${entry.tag}] ${redact(entry.message)}"
        }
    }

    /** Defensive clipboard/export redaction; runtime Logcat behavior is unchanged. */
    fun redact(value: String): String {
        return value
            .replace(Regex("(?i)(passphrase|password|token|authorization|secret)\\s*[:=]\\s*[^\\s,;]+"), "$1=[REDACTED]")
            .replace(Regex("(?i)(DIRECT-[^\\s,;]+)\\s+([ -~]{8,63})"), "$1 [REDACTED]")
    }
}
