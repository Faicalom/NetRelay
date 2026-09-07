package com.netrelay.connectivity.presentation

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class AppProductUiPolicyTest {
    @Test
    fun `fresh preferences use required safe defaults`() {
        assertEquals("en", AppPreferences.DEFAULT_LANGUAGE)
        assertEquals(250, AppPreferences.DEFAULT_MAX_LOGS)
        assertEquals(setOf("en", "ar", "fr", "ru"), AppPreferences.SUPPORTED_LANGUAGES)
        assertTrue(AppNotificationCenter.MAX_ITEMS in 50..100)
    }

    @Test
    fun `identical consecutive notification is deduplicated`() {
        val latest = AppNotification(
            category = AppNotificationCategory.INTERNET,
            severity = AppNotificationSeverity.WARNING,
            titleKey = "internet",
            messageKey = "internet_lost"
        )
        assertTrue(AppNotificationCenter.isDuplicate(
            latest,
            AppNotificationCategory.INTERNET,
            "internet",
            "internet_lost",
            emptyList()
        ))
        assertFalse(AppNotificationCenter.isDuplicate(
            latest,
            AppNotificationCategory.INTERNET,
            "internet",
            "internet_available",
            emptyList()
        ))
    }

    @Test
    fun `all Android resource locales contain the same string keys`() {
        val resourceRoot = File("src/main/res")
        val localeFiles = listOf("values", "values-ar", "values-fr", "values-ru")
            .associateWith { locale -> File(resourceRoot, "$locale/strings.xml") }
        localeFiles.values.forEach { file -> assertTrue("Missing ${file.path}", file.isFile) }
        val keyPattern = Regex("<string\\s+name=\"([^\"]+)\"")
        val keys = localeFiles.mapValues { (_, file) -> keyPattern.findAll(file.readText()).map { it.groupValues[1] }.toSet() }
        val expected = keys.getValue("values")
        keys.forEach { (locale, actual) -> assertEquals("String key mismatch for $locale", expected, actual) }
    }

    @Test
    fun `in-app header is clean while launcher assets remain untouched`() {
        val activitySource = File("src/main/kotlin/com/netrelay/connectivity/presentation/MainActivity.kt").readText()
        val headerSource = activitySource.substringAfter("private fun createHeader()").substringBefore("private fun addAppNotification")
        assertTrue(headerSource.contains("text = \"Net Relay\""))
        assertFalse(headerSource.contains("R.mipmap.ic_launcher"))
        assertFalse(activitySource.contains("الرابط بلا حدود"))
        assertTrue(File("src/main/res/mipmap-anydpi-v26/ic_launcher.xml").isFile)
        assertTrue(File("src/main/res/drawable/ic_netrelay_notification.png").isFile)
    }
}
