package com.netrelay.connectivity.presentation

import android.content.Context

/**
 * Single persistent source for user-facing product preferences.
 * None of these values changes the transport or data-plane implementation.
 */
class AppPreferences(context: Context) {
    private val preferences = context.applicationContext.getSharedPreferences(FILE_NAME, Context.MODE_PRIVATE)

    var language: String
        get() = preferences.getString(KEY_LANGUAGE, DEFAULT_LANGUAGE)
            ?.takeIf { it in SUPPORTED_LANGUAGES }
            ?: DEFAULT_LANGUAGE
        set(value) = preferences.edit().putString(KEY_LANGUAGE, value.takeIf { it in SUPPORTED_LANGUAGES } ?: DEFAULT_LANGUAGE).apply()

    var keepScreenAwake: Boolean
        get() = preferences.getBoolean(KEY_KEEP_SCREEN_AWAKE, false)
        set(value) = preferences.edit().putBoolean(KEY_KEEP_SCREEN_AWAKE, value).apply()

    var confirmDisconnect: Boolean
        get() = preferences.getBoolean(KEY_CONFIRM_DISCONNECT, true)
        set(value) = preferences.edit().putBoolean(KEY_CONFIRM_DISCONNECT, value).apply()

    var returnDashboardAfterConnect: Boolean
        get() = preferences.getBoolean(KEY_RETURN_DASHBOARD, true)
        set(value) = preferences.edit().putBoolean(KEY_RETURN_DASHBOARD, value).apply()

    var preferredTransport: String
        get() = preferences.getString(KEY_PREFERRED_TRANSPORT, PREFERRED_NONE)
            ?.takeIf { it in PREFERRED_TRANSPORTS }
            ?: PREFERRED_NONE
        set(value) = preferences.edit().putString(KEY_PREFERRED_TRANSPORT, value.takeIf { it in PREFERRED_TRANSPORTS } ?: PREFERRED_NONE).apply()

    var notificationsEnabled: Boolean
        get() = preferences.getBoolean(KEY_NOTIFICATIONS_ENABLED, true)
        set(value) = preferences.edit().putBoolean(KEY_NOTIFICATIONS_ENABLED, value).apply()

    var connectionNotifications: Boolean
        get() = preferences.getBoolean(KEY_CONNECTION_NOTIFICATIONS, true)
        set(value) = preferences.edit().putBoolean(KEY_CONNECTION_NOTIFICATIONS, value).apply()

    var warningNotifications: Boolean
        get() = preferences.getBoolean(KEY_WARNING_NOTIFICATIONS, true)
        set(value) = preferences.edit().putBoolean(KEY_WARNING_NOTIFICATIONS, value).apply()

    var detailedLogs: Boolean
        get() = preferences.getBoolean(KEY_DETAILED_LOGS, false)
        set(value) = preferences.edit().putBoolean(KEY_DETAILED_LOGS, value).apply()

    var maxVisibleLogs: Int
        get() = preferences.getInt(KEY_MAX_VISIBLE_LOGS, DEFAULT_MAX_LOGS)
            .takeIf { it in VISIBLE_LOG_LIMITS }
            ?: DEFAULT_MAX_LOGS
        set(value) = preferences.edit().putInt(KEY_MAX_VISIBLE_LOGS, value.takeIf { it in VISIBLE_LOG_LIMITS } ?: DEFAULT_MAX_LOGS).apply()

    companion object {
        private const val FILE_NAME = "net_relay_product_preferences"
        private const val KEY_LANGUAGE = "language"
        private const val KEY_KEEP_SCREEN_AWAKE = "keep_screen_awake"
        private const val KEY_CONFIRM_DISCONNECT = "confirm_disconnect"
        private const val KEY_RETURN_DASHBOARD = "return_dashboard_after_connect"
        private const val KEY_PREFERRED_TRANSPORT = "preferred_transport"
        private const val KEY_NOTIFICATIONS_ENABLED = "notifications_enabled"
        private const val KEY_CONNECTION_NOTIFICATIONS = "connection_notifications"
        private const val KEY_WARNING_NOTIFICATIONS = "warning_notifications"
        private const val KEY_DETAILED_LOGS = "detailed_logs"
        private const val KEY_MAX_VISIBLE_LOGS = "max_visible_logs"

        const val DEFAULT_LANGUAGE = "en"
        const val PREFERRED_NONE = "none"
        const val PREFERRED_USB = "usb"
        const val PREFERRED_WIFI_DIRECT = "wifiDirect"
        const val DEFAULT_MAX_LOGS = 250

        val SUPPORTED_LANGUAGES = setOf("en", "ar", "fr", "ru")
        val PREFERRED_TRANSPORTS = setOf(PREFERRED_NONE, PREFERRED_USB, PREFERRED_WIFI_DIRECT)
        val VISIBLE_LOG_LIMITS = setOf(100, 250, 500, 1000)
    }
}
