package com.netrelay.connectivity.presentation

import android.content.Context
import java.util.UUID
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json

@Serializable
enum class AppNotificationSeverity { INFO, SUCCESS, WARNING, ERROR }

@Serializable
enum class AppNotificationCategory { USB, WIFI_DIRECT, CONNECTION, INTERNET, SERVICE, SYSTEM }

@Serializable
data class AppNotification(
    val id: String = UUID.randomUUID().toString(),
    val timestamp: Long = System.currentTimeMillis(),
    val category: AppNotificationCategory,
    val severity: AppNotificationSeverity,
    val titleKey: String,
    val messageKey: String,
    val messageArgs: List<String> = emptyList(),
    val isRead: Boolean = false
)

/** Persistent, bounded and deduplicated in-app notification history. */
class AppNotificationCenter(context: Context) {
    private val preferences = context.applicationContext.getSharedPreferences(FILE_NAME, Context.MODE_PRIVATE)
    private val json = Json { ignoreUnknownKeys = true; encodeDefaults = true }
    private val _items = MutableStateFlow(load())
    val items = _items.asStateFlow()

    fun add(
        category: AppNotificationCategory,
        severity: AppNotificationSeverity,
        titleKey: String,
        messageKey: String,
        messageArgs: List<String> = emptyList()
    ) {
        val current = _items.value
        val latest = current.firstOrNull()
        if (isDuplicate(latest, category, titleKey, messageKey, messageArgs)) {
            return
        }
        update(listOf(AppNotification(
            category = category,
            severity = severity,
            titleKey = titleKey,
            messageKey = messageKey,
            messageArgs = messageArgs
        )) + current)
    }

    fun markRead(id: String) {
        update(_items.value.map { item -> if (item.id == id) item.copy(isRead = true) else item })
    }

    fun markAllRead() {
        update(_items.value.map { item -> if (item.isRead) item else item.copy(isRead = true) })
    }

    fun clear() = update(emptyList())

    private fun update(items: List<AppNotification>) {
        val bounded = items.take(MAX_ITEMS)
        _items.value = bounded
        preferences.edit().putString(KEY_ITEMS, json.encodeToString(bounded)).apply()
    }

    private fun load(): List<AppNotification> {
        val serialized = preferences.getString(KEY_ITEMS, null) ?: return emptyList()
        return runCatching { json.decodeFromString<List<AppNotification>>(serialized) }
            .getOrDefault(emptyList())
            .take(MAX_ITEMS)
    }

    companion object {
        private const val FILE_NAME = "net_relay_notification_center"
        private const val KEY_ITEMS = "items"
        const val MAX_ITEMS = 75

        internal fun isDuplicate(
            latest: AppNotification?,
            category: AppNotificationCategory,
            titleKey: String,
            messageKey: String,
            messageArgs: List<String>
        ): Boolean = latest != null &&
            latest.category == category &&
            latest.titleKey == titleKey &&
            latest.messageKey == messageKey &&
            latest.messageArgs == messageArgs
    }
}
