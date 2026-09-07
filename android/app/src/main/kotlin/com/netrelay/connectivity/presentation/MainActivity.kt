package com.netrelay.connectivity.presentation

import android.Manifest
import android.app.AlertDialog
import android.content.BroadcastReceiver
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.content.res.ColorStateList
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.ColorDrawable
import android.graphics.drawable.GradientDrawable
import android.location.LocationManager
import android.net.Uri
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.TrafficStats
import android.hardware.usb.UsbManager
import android.os.BatteryManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.PowerManager
import android.os.SystemClock
import android.provider.Settings
import android.text.InputType
import android.text.TextUtils
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.view.WindowManager
import android.view.inputmethod.InputMethodManager
import android.widget.AdapterView
import android.widget.ArrayAdapter
import android.widget.Button
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ListView
import android.widget.SearchView
import android.widget.ScrollView
import android.widget.Spinner
import android.widget.TextView
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.appcompat.app.AppCompatDelegate
import androidx.appcompat.widget.SwitchCompat
import androidx.core.content.ContextCompat
import androidx.core.os.LocaleListCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.repeatOnLifecycle
import com.netrelay.connectivity.R
import com.netrelay.connectivity.service.ConnectionService
import com.netrelay.connectivity.service.ConnectionRuntimeStore
import com.netrelay.connectivity.service.TransportUiState
import com.netrelay.connectivity.service.TransportUiStatePolicy
import com.netrelay.connectivity.service.TransportPrerequisitePolicy
import com.netrelay.connectivity.transport.TransportFactory
import com.netrelay.connectivity.util.Logger
import com.google.android.material.snackbar.Snackbar
import com.google.android.material.bottomsheet.BottomSheetDialog
import java.text.DateFormat
import java.util.Date
import java.net.InetSocketAddress
import java.net.Socket
import kotlinx.coroutines.launch

/**
 * Main activity - polished phone dashboard for connection control and monitoring.
 */
class MainActivity : AppCompatActivity() {

    private data class BottomNavItemRefs(
        val container: LinearLayout,
        val icon: ImageView,
        val label: TextView
    )

    private lateinit var viewModel: ConnectionViewModel
    private lateinit var mainContentContainer: LinearLayout
    private lateinit var selectedModeValueText: TextView
    private lateinit var statusValueText: TextView
    private lateinit var serviceStateValueText: TextView
    private lateinit var wifiEndpointValueText: TextView
    private lateinit var wifiNetworkValueText: TextView
    private lateinit var wifiPassphraseValueText: TextView
    private lateinit var wifiNetworkEditText: EditText
    private lateinit var wifiPassphraseEditText: EditText
    private lateinit var wifiClientValueText: TextView
    private lateinit var proxyEndpointValueText: TextView
    private lateinit var proxyStatusValueText: TextView
    private lateinit var protocolValueText: TextView
    private lateinit var transportsValueText: TextView
    private lateinit var logsValueText: TextView
    private lateinit var usbStateSummaryText: TextView
    private lateinit var appPreferences: AppPreferences
    private lateinit var notificationCenter: AppNotificationCenter
    private var bellBadgeText: TextView? = null
    private var logsListView: ListView? = null
    private var logsAdapter: ArrayAdapter<String>? = null
    private var logSearchQuery = ""
    private var selectedLogLevel = "ALL"
    private var logAutoScroll = true
    private var visibleLogEntries: List<Logger.LogEntry> = emptyList()

    private lateinit var topStatusChipText: TextView
    private lateinit var heroTitleText: TextView
    private lateinit var heroSubtitleText: TextView
    private lateinit var bridgeStateText: TextView
    private lateinit var connectionDurationText: TextView
    private lateinit var phoneAddressText: TextView
    private lateinit var computerAddressText: TextView
    private lateinit var downloadSpeedValueText: TextView
    private lateinit var uploadSpeedValueText: TextView
    private lateinit var pingValueText: TextView

    private val bottomNavItems = mutableMapOf<String, BottomNavItemRefs>()
    private val modeCards = mutableMapOf<String, LinearLayout>()
    private val modeStatusTexts = mutableMapOf<String, TextView>()
    private val durationHandler = Handler(Looper.getMainLooper())
    private var connectedSinceMillis: Long? = null
    private var activeTab = "home"
    private var latestMode = "wifiDirect"
    private var latestWifiEndpoint = "Unavailable"
    private var latestWifiNetwork = "Unavailable"
    private var latestWifiPassphrase = "Unavailable"
    private var latestWifiClient = "No device connected"
    private var latestProxyEndpoint = "Unavailable"
    private var latestProxyStatus = "Stopped"
    private var latestServiceState = "Stopped"
    private var latestProtocol = "No connection activity yet"
    private var latestUsbTransportState = TransportUiState.INACTIVE
    private var latestWifiDirectTransportState = TransportUiState.INACTIVE
    private var appLanguage = AppPreferences.DEFAULT_LANGUAGE
    private var pendingWifiDirectActivation = false
    private var pendingWifiDirectSettings: Pair<String, String>? = null
    private var usbStateReceiverRegistered = false
    private var connectivityCallbackRegistered = false
    private var lastInternetAvailable: Boolean? = null
    private var lastUiState: UiState? = null
    private var observedUsbState: TransportUiState? = null
    private var observedWifiState: TransportUiState? = null
    private var explicitDisconnectPending = false

    private val connectivityCallback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) = updateInternetAvailability()
        override fun onLost(network: Network) = updateInternetAvailability()
        override fun onCapabilitiesChanged(network: Network, networkCapabilities: NetworkCapabilities) =
            updateInternetAvailability()
    }

    private val usbStateReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) {
            when (intent?.action) {
                android.hardware.usb.UsbManager.ACTION_USB_DEVICE_DETACHED,
                Intent.ACTION_POWER_DISCONNECTED -> {
                    if (!isUsbCableAttached()) {
                        ConnectionRuntimeStore.updateTransportUiState("usb", TransportUiState.INACTIVE)
                    }
                }
            }
            refreshUsbState()
        }
    }

    private var lastRxBytes = -1L
    private var lastTxBytes = -1L
    private var lastTrafficSampleMillis = 0L
    private var lastPingSampleMillis = 0L
    private var pingInFlight = false

    private val logger = Logger
    private val tag = "MainActivity"

    private val durationRunnable = object : Runnable {
        override fun run() {
            updateConnectionDuration()
            updateNetworkMetrics()
            durationHandler.postDelayed(this, 1000)
        }
    }

    private val permissionLauncher =
        registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { result ->
            val deniedPermissions = result.filterValues { granted -> !granted }.keys
            if (deniedPermissions.isEmpty()) {
                logger.info(tag, "All requested runtime permissions granted")
            } else {
                logger.warn(tag, "Some permissions were denied: ${deniedPermissions.joinToString()}")
            }
            renderLogs()
            if (pendingWifiDirectActivation && hasWifiDirectRuntimePermissions()) {
                continueWifiDirectActivation()
            } else if (pendingWifiDirectActivation) {
                pendingWifiDirectActivation = false
                ConnectionRuntimeStore.updateTransportUiState("wifiDirect", TransportUiState.INACTIVE)
            }
        }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        appPreferences = AppPreferences(this)
        notificationCenter = AppNotificationCenter(this)
        appLanguage = appPreferences.language
        val requestedLocales = LocaleListCompat.forLanguageTags(appLanguage)
        if (AppCompatDelegate.getApplicationLocales().toLanguageTags() != requestedLocales.toLanguageTags()) {
            AppCompatDelegate.setApplicationLocales(requestedLocales)
        }
        configureSystemBars()
        setContentView(buildContentView())

        logger.info(tag, "MainActivity created")

        viewModel = ViewModelProvider(this)[ConnectionViewModel::class.java]
        registerTransports()
        observeViewModel()
        requestMissingPermissions()
        applyLaunchTransportOverride(intent)
        renderLogs()
        durationHandler.post(durationRunnable)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        applyLaunchTransportOverride(intent)
    }

    override fun onStart() {
        super.onStart()
        logger.debug(tag, "Activity started")
        registerUsbStateReceiver()
        registerConnectivityCallback()
        refreshTransportUiState()
        renderLogs()
    }

    override fun onResume() {
        super.onResume()
        refreshTransportUiState()
        if (pendingWifiDirectActivation && hasWifiDirectRuntimePermissions() && isLocationServiceEnabled()) {
            startWifiDirectActivation()
        }
    }

    override fun onStop() {
        unregisterUsbStateReceiver()
        unregisterConnectivityCallback()
        super.onStop()
        logger.debug(tag, "Activity stopped")
    }

    override fun onDestroy() {
        durationHandler.removeCallbacks(durationRunnable)
        logger.info(tag, "Activity destroyed")
        super.onDestroy()
    }

    private fun configureSystemBars() {
        window.statusBarColor = PageBg
        window.navigationBarColor = BottomBarBg
    }

    private fun buildContentView(): View {
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(PageBg)
            layoutDirection = uiLayoutDirection()
        }

        val scrollView = ScrollView(this).apply {
            isFillViewport = false
            overScrollMode = View.OVER_SCROLL_NEVER
            setBackgroundColor(PageBg)
            layoutDirection = uiLayoutDirection()
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                0,
                1f
            )
        }

        mainContentContainer = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            layoutDirection = uiLayoutDirection()
            setPadding(dp(10), dp(8), dp(10), dp(6))
        }

        scrollView.addView(mainContentContainer)
        root.addView(scrollView)
        root.addView(createBottomNavigation())
        renderActiveTab()
        return root
    }

    private fun selectBottomTab(tab: String) {
        if (activeTab == tab && mainContentContainer.childCount > 0) return
        activeTab = tab
        renderActiveTab()
    }

    private fun renderActiveTab() {
        if (!::mainContentContainer.isInitialized) return
        mainContentContainer.removeAllViews()
        mainContentContainer.addView(createHeader())

        when (activeTab) {
            "connection" -> {
                mainContentContainer.addView(createNetworkMetricsCard())
                mainContentContainer.addView(createWifiDirectSettingsCard())
            }
            "service" -> {
                mainContentContainer.addView(createConnectionStatusCard())
                mainContentContainer.addView(createModeCard())
                mainContentContainer.addView(createQuickActionsCard())
            }
            "logs" -> {
                mainContentContainer.addView(createLogsCard())
            }
            "settings" -> {
                mainContentContainer.addView(createSettingsCard())
                mainContentContainer.addView(createQuickActionsCard())
            }
            else -> {
                mainContentContainer.addView(createConnectionStatusCard())
                mainContentContainer.addView(createModeCard())
                mainContentContainer.addView(createConnectionInfoCard())
                mainContentContainer.addView(createQuickActionsCard())
                mainContentContainer.addView(createLogsCard())
            }
        }

        updateBottomNavSelection()
        updateNetworkMetrics()
        refreshTransportUiState()
        renderLogs()
    }

    private fun createHeader(): View {
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            layoutDirection = uiLayoutDirection()
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT
            ).apply {
                bottomMargin = dp(8)
            }
        }

        row.addView(LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            layoutDirection = uiLayoutDirection()
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
            addView(TextView(this@MainActivity).apply {
                text = "Net Relay"
                textSize = 21f
                setTypeface(typeface, Typeface.BOLD)
                setTextColor(TextStrong)
                includeFontPadding = false
            })
        })

        row.addView(FrameLayout(this).apply {
            background = rounded(Surface2, dp(24), StrokeSoft, 1)
            layoutParams = LinearLayout.LayoutParams(dp(38), dp(38))
            addView(createIconImage(R.drawable.ic_bell_24, TextBase, 21).apply {
                layoutParams = FrameLayout.LayoutParams(dp(21), dp(21), Gravity.CENTER)
            })
            addView(TextView(this@MainActivity).apply {
                textSize = 8f
                setTypeface(typeface, Typeface.BOLD)
                setTextColor(PageBg)
                gravity = Gravity.CENTER
                background = oval(Danger)
                visibility = View.GONE
                layoutParams = FrameLayout.LayoutParams(dp(18), dp(18), Gravity.TOP or Gravity.RIGHT).apply {
                    topMargin = dp(2)
                    rightMargin = dp(1)
                }
                bellBadgeText = this
            })
            setOnClickListener { showNotificationCenter() }
        })

        updateBellBadge()

        return row
    }

    private fun addAppNotification(
        category: AppNotificationCategory,
        severity: AppNotificationSeverity,
        titleKey: String,
        messageKey: String,
        args: List<String> = emptyList()
    ) {
        if (!appPreferences.notificationsEnabled) return
        if (category in setOf(AppNotificationCategory.USB, AppNotificationCategory.WIFI_DIRECT, AppNotificationCategory.CONNECTION) &&
            !appPreferences.connectionNotifications
        ) return
        if (severity in setOf(AppNotificationSeverity.WARNING, AppNotificationSeverity.ERROR) &&
            !appPreferences.warningNotifications
        ) return
        notificationCenter.add(category, severity, titleKey, messageKey, args)
        updateBellBadge()
    }

    private fun updateBellBadge() {
        val unread = if (::notificationCenter.isInitialized) notificationCenter.items.value.count { !it.isRead } else 0
        bellBadgeText?.apply {
            text = if (unread > 99) "99+" else unread.toString()
            visibility = if (unread == 0) View.GONE else View.VISIBLE
        }
    }

    private fun showNotificationCenter() {
        val dialog = BottomSheetDialog(this)
        val content = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(18), dp(14), dp(18), dp(24))
            background = rounded(CardBg, dp(22), StrokeSoft, 1)
            layoutDirection = if (appLanguage == "ar") View.LAYOUT_DIRECTION_RTL else View.LAYOUT_DIRECTION_LTR
        }
        val header = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        header.addView(TextView(this).apply {
            text = tr("الإشعارات", "Notifications", "Notifications", "Уведомления")
            textSize = 20f
            setTypeface(typeface, Typeface.BOLD)
            setTextColor(TextStrong)
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
        })
        header.addView(createCompactTextAction(tr("قراءة الكل", "Mark all read", "Tout lire", "Прочитать все")) {
            notificationCenter.markAllRead()
            updateBellBadge()
            dialog.dismiss()
            showNotificationCenter()
        })
        header.addView(createCompactTextAction(tr("مسح الكل", "Clear all", "Tout effacer", "Очистить")) {
            notificationCenter.clear()
            updateBellBadge()
            dialog.dismiss()
            showNotificationCenter()
        })
        content.addView(header)

        val itemsContainer = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        val items = notificationCenter.items.value
        if (items.isEmpty()) {
            itemsContainer.addView(TextView(this).apply {
                text = tr("لا توجد إشعارات بعد", "No notifications yet", "Aucune notification", "Уведомлений пока нет")
                textSize = 14f
                setTextColor(TextMuted)
                gravity = Gravity.CENTER
                setPadding(dp(12), dp(42), dp(12), dp(42))
            })
        } else {
            items.forEach { item -> itemsContainer.addView(createNotificationRow(item, dialog)) }
        }
        content.addView(ScrollView(this).apply {
            isFillViewport = false
            addView(itemsContainer)
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                if (items.isEmpty()) LinearLayout.LayoutParams.WRAP_CONTENT
                else (resources.displayMetrics.heightPixels * 0.58f).toInt()
            ).apply {
                topMargin = dp(10)
            }
        })
        dialog.setContentView(content)
        dialog.setOnShowListener {
            dialog.window?.apply {
                setBackgroundDrawable(ColorDrawable(Color.TRANSPARENT))
                navigationBarColor = CardBg
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                    insetsController?.setSystemBarsAppearance(
                        0,
                        android.view.WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS
                    )
                } else if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                    decorView.systemUiVisibility = decorView.systemUiVisibility and
                        View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR.inv()
                }
            }
            dialog.findViewById<FrameLayout>(com.google.android.material.R.id.design_bottom_sheet)?.apply {
                setBackgroundColor(Color.TRANSPARENT)
                backgroundTintList = ColorStateList.valueOf(Color.TRANSPARENT)
                elevation = 0f

                var ancestor = parent as? View
                repeat(2) {
                    ancestor?.setBackgroundColor(Color.TRANSPARENT)
                    ancestor?.backgroundTintList = ColorStateList.valueOf(Color.TRANSPARENT)
                    ancestor = ancestor?.parent as? View
                }
            }
        }
        dialog.show()
    }

    private fun createNotificationRow(item: AppNotification, dialog: BottomSheetDialog): View {
        val color = when (item.severity) {
            AppNotificationSeverity.SUCCESS -> AccentGreen
            AppNotificationSeverity.INFO -> AccentBlue
            AppNotificationSeverity.WARNING -> Color.parseColor("#FFB74D")
            AppNotificationSeverity.ERROR -> Danger
        }
        return LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(12), dp(10), dp(12), dp(10))
            background = rounded(if (item.isRead) Surface2 else Surface3, dp(12), color, 1)
            layoutParams = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT).apply {
                bottomMargin = dp(8)
            }
            addView(TextView(this@MainActivity).apply {
                text = notificationTitle(item.titleKey)
                textSize = 14f
                setTypeface(typeface, Typeface.BOLD)
                setTextColor(color)
            })
            addView(TextView(this@MainActivity).apply {
                text = notificationMessage(item.messageKey, item.messageArgs)
                textSize = 12f
                setTextColor(TextBase)
                setPadding(0, dp(3), 0, 0)
            })
            addView(TextView(this@MainActivity).apply {
                text = DateFormat.getDateTimeInstance(DateFormat.SHORT, DateFormat.SHORT).format(Date(item.timestamp))
                textSize = 10f
                setTextColor(TextMuted)
                setPadding(0, dp(4), 0, 0)
            })
            setOnClickListener {
                notificationCenter.markRead(item.id)
                updateBellBadge()
                dialog.dismiss()
                showNotificationCenter()
            }
        }
    }

    private fun createCompactTextAction(label: String, action: () -> Unit): TextView = TextView(this).apply {
        text = label
        textSize = 10f
        setTextColor(AccentBlue)
        setPadding(dp(8), dp(8), dp(8), dp(8))
        setOnClickListener { action() }
    }

    private fun notificationTitle(key: String): String = when (key) {
        "usb" -> tr("USB", "USB", "USB", "USB")
        "wfd" -> tr("Wi-Fi Direct", "Wi-Fi Direct", "Wi-Fi Direct", "Wi-Fi Direct")
        "connection" -> tr("الاتصال", "Connection", "Connexion", "Подключение")
        "internet" -> tr("الإنترنت", "Internet", "Internet", "Интернет")
        "service" -> tr("الخدمة", "Service", "Service", "Служба")
        else -> tr("النظام", "System", "Système", "Система")
    }

    private fun notificationMessage(key: String, args: List<String>): String = when (key) {
        "ready" -> tr("وسيلة النقل جاهزة", "Transport is ready", "Le transport est prêt", "Транспорт готов")
        "connected" -> tr("تم الاتصال بنجاح", "Connected successfully", "Connexion réussie", "Подключение выполнено")
        "disconnected" -> tr("تم قطع الاتصال", "Disconnected", "Déconnecté", "Отключено")
        "activating" -> tr("جارٍ تجهيز وسيلة النقل", "Preparing transport", "Préparation du transport", "Подготовка транспорта")
        "failed" -> tr("فشل تشغيل وسيلة النقل", "Transport startup failed", "Échec du démarrage du transport", "Не удалось запустить транспорт")
        "internet_available" -> tr("أصبح الإنترنت متاحًا", "Internet became available", "Internet est disponible", "Интернет доступен")
        "internet_lost" -> tr("فُقد الإنترنت بينما التطبيق يعمل", "Internet was lost while the app is active", "Internet a été perdu pendant l’activité", "Интернет-соединение потеряно")
        "group_lost" -> tr("فُقدت مجموعة Wi-Fi Direct بشكل غير متوقع", "Wi-Fi Direct group was lost unexpectedly", "Le groupe Wi-Fi Direct a été perdu", "Группа Wi-Fi Direct неожиданно потеряна")
        "location_required" -> tr("يجب تشغيل الموقع لاستخدام Wi-Fi Direct", "Location must be enabled for Wi-Fi Direct", "La localisation est requise pour Wi-Fi Direct", "Для Wi-Fi Direct требуется геолокация")
        "usb_debugging" -> tr("فعّل تصحيح USB للاتصال بالكمبيوتر", "Enable USB debugging to connect to the computer", "Activez le débogage USB pour connecter l’ordinateur", "Включите отладку USB для подключения к компьютеру")
        else -> args.firstOrNull().orEmpty()
    }

    private fun createConnectionStatusCard(): View {
        val card = createCard().apply {
            layoutDirection = View.LAYOUT_DIRECTION_LTR
        }

        card.addView(createCardHeader(tr("حالة الاتصال", "Connection status", "État de la connexion", "Состояние подключения"), tr("متصل", "Connected", "Connecté", "Подключено"), AccentGreen).also {
            topStatusChipText = it.second
        }.first)

        val route = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            layoutDirection = View.LAYOUT_DIRECTION_LTR
            setPadding(0, dp(4), 0, dp(4))
        }

        val phoneEndpoint = createEndpointBlock(R.drawable.ic_phone_24, tr("الهاتف", "Phone", "Téléphone", "Телефон"), "192.168.49.1", AccentGreen)
        phoneAddressText = phoneEndpoint.second
        route.addView(phoneEndpoint.first)
        route.addView(createDashedLine())
        route.addView(LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 0.8f)
            addView(createIconImage(R.drawable.ic_shield_check_24, AccentGreen, 30).apply {
                layoutParams = LinearLayout.LayoutParams(dp(30), dp(30))
            })
            addView(TextView(this@MainActivity).apply {
                text = tr("الاتصال نشط", "Connection active", "Connexion active", "Подключение активно")
                textSize = 11f
                setTypeface(typeface, Typeface.BOLD)
                setTextColor(AccentGreen)
                gravity = Gravity.CENTER
            }.also {
                heroTitleText = it
            })
            addView(TextView(this@MainActivity).apply {
                text = tr("الاتصال نشط وجسر الإنترنت يعمل", "The connection and Internet bridge are active", "La connexion et le pont Internet sont actifs", "Подключение и интернет-мост активны")
                textSize = 8f
                setTextColor(TextMuted)
                gravity = Gravity.CENTER
                maxLines = 2
            }.also {
                heroSubtitleText = it
            })
        })
        route.addView(createDashedLine())
        val computerEndpoint = createEndpointBlock(R.drawable.ic_monitor_24, tr("الكمبيوتر", "Computer", "Ordinateur", "Компьютер"), tr("بانتظار الاتصال", "Waiting", "En attente", "Ожидание"), AccentBlue)
        computerAddressText = computerEndpoint.second
        route.addView(computerEndpoint.first)
        card.addView(route)

        card.addView(divider())

        selectedModeValueText = createDetailRow(R.drawable.ic_wifi_24, tr("وضع الاتصال", "Connection mode", "Mode de connexion", "Режим подключения"), modeLabelFor(latestMode)).also {
            card.addView(it.first)
        }.second
        connectionDurationText = createDetailRow(R.drawable.ic_clock_24, tr("مدة الاتصال", "Session duration", "Durée de session", "Длительность сеанса"), "--:--").also {
            card.addView(it.first)
        }.second
        protocolValueText = createDetailRow(R.drawable.ic_refresh_24, tr("آخر تحديث", "Last activity", "Dernière activité", "Последняя активность"), tr("لا يوجد نشاط اتصال بعد", "No connection activity yet", "Aucune activité de connexion", "Активности подключения пока нет")).also {
            card.addView(it.first)
        }.second
        bridgeStateText = createDetailRow(R.drawable.ic_check_circle_24, tr("حالة الجسر", "Bridge status", "État du pont", "Состояние моста"), tr("جاهز", "Ready", "Prêt", "Готово")).also {
            card.addView(it.first)
        }.second

        statusValueText = heroTitleText
        serviceStateValueText = bridgeStateText
        proxyStatusValueText = bridgeStateText

        return card
    }

    private fun createModeCard(): View {
        val card = createCard(tr("وضع الاتصال", "Connection mode", "Mode de connexion", "Режим подключения"))
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER
            layoutDirection = View.LAYOUT_DIRECTION_LTR
            setPadding(0, dp(5), 0, 0)
        }

        row.addView(createTransportModeButton("usb", R.drawable.ic_usb_24, "USB"))
        row.addView(createTransportModeButton("wifiDirect", R.drawable.ic_wifi_24, "Wi‑Fi Direct"))
        card.addView(row)

        val usbRefreshRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            layoutDirection = View.LAYOUT_DIRECTION_LTR
            setPadding(dp(4), dp(8), dp(4), 0)
        }
        usbStateSummaryText = TextView(this).apply {
            text = tr("USB: لم يتم التحقق بعد", "USB: not checked yet", "USB : non vérifié", "USB: ещё не проверено")
            textSize = 10f
            setTextColor(TextMuted)
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
        }
        usbRefreshRow.addView(usbStateSummaryText)
        usbRefreshRow.addView(createQuickButton(
            tr("تحديث USB", "Refresh USB", "Actualiser USB", "Обновить USB"),
            R.drawable.ic_refresh_24
        ) { refreshUsbState(showFeedback = true) }.apply {
            layoutParams = LinearLayout.LayoutParams(dp(118), dp(36)).apply {
                leftMargin = dp(6)
            }
        })
        card.addView(usbRefreshRow)
        updateModeCards()
        refreshUsbState()
        return card
    }

    private fun createConnectionInfoCard(): View {
        val card = createCard(tr("معلومات الاتصال", "Connection information", "Informations de connexion", "Сведения о подключении"))

        wifiNetworkValueText = addInfoRow(card, R.drawable.ic_wifi_24, "Wi‑Fi Direct (SSID)", unavailableText()) {
            copyToClipboard("Wi-Fi Direct SSID", wifiNetworkValueText.text.toString())
        }
        wifiPassphraseValueText = addInfoRow(card, R.drawable.ic_lock_24, tr("كلمة سر Wi‑Fi Direct", "Wi-Fi Direct password", "Mot de passe Wi-Fi Direct", "Пароль Wi-Fi Direct"), unavailableText()) {
            copyToClipboard("Wi-Fi Direct Password", wifiPassphraseValueText.text.toString())
        }
        wifiEndpointValueText = addInfoRow(card, R.drawable.ic_server_24, tr("عنوان المستمع", "Listener endpoint", "Point d’écoute", "Адрес слушателя"), unavailableText()) {
            copyToClipboard("Listener endpoint", wifiEndpointValueText.text.toString())
        }
        proxyEndpointValueText = addInfoRow(card, R.drawable.ic_proxy_24, tr("عنوان البروكسي", "Proxy endpoint", "Point de terminaison proxy", "Адрес прокси"), unavailableText()) {
            copyToClipboard("Proxy endpoint", proxyEndpointValueText.text.toString())
        }
        wifiClientValueText = addInfoRow(card, R.drawable.ic_phone_24, tr("عنوان الهاتف", "Phone address", "Adresse du téléphone", "Адрес телефона"), unavailableText()) {
            copyToClipboard("Phone address", wifiClientValueText.text.toString())
        }

        card.addView(TextView(this).apply {
            text = tr("تعديل Wi‑Fi Direct", "Edit Wi-Fi Direct", "Modifier Wi-Fi Direct", "Изменить Wi-Fi Direct")
            textSize = 14f
            setTypeface(typeface, Typeface.BOLD)
            setTextColor(TextStrong)
            visibility = View.GONE
            setPadding(0, dp(14), 0, dp(8))
        })

        wifiNetworkEditText = createDarkInput("DIRECT-NR-SOG01-NetRelay").apply {
            visibility = View.GONE
        }
        card.addView(wifiNetworkEditText)
        wifiPassphraseEditText = createDarkInput("NetRelay123").apply {
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD
            visibility = View.GONE
        }
        card.addView(wifiPassphraseEditText)

        val saveButton = createSolidButton(tr("حفظ وإعادة تشغيل Wi‑Fi Direct", "Save and restart Wi-Fi Direct", "Enregistrer et redémarrer Wi-Fi Direct", "Сохранить и перезапустить Wi-Fi Direct"), AccentGreen, PageBg) {
            saveWifiDirectSettings()
        }
        saveButton.visibility = View.GONE
        card.addView(saveButton)

        val disconnectButton = createOutlineButton(tr("قطع الاتصال", "Disconnect", "Déconnecter", "Отключить"), Danger, DangerSurface) {
            disconnectNow()
        }
        disconnectButton.layoutParams = (disconnectButton.layoutParams as LinearLayout.LayoutParams).apply {
            topMargin = dp(12)
        }
        card.addView(disconnectButton)
        return card
    }

    private fun createNetworkMetricsCard(): View {
        val card = createCard(tr("سرعة الاتصال", "Connection speed", "Vitesse de connexion", "Скорость подключения"))
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER
            layoutDirection = View.LAYOUT_DIRECTION_LTR
            setPadding(0, dp(8), 0, 0)
        }

        val downloadBlock = createMetricBlock(R.drawable.ic_chart_24, tr("تحميل", "Download", "Réception", "Загрузка"), AccentBlue).also {
            downloadSpeedValueText = it.second
        }
        val uploadBlock = createMetricBlock(R.drawable.ic_proxy_24, tr("رفع", "Upload", "Envoi", "Отдача"), AccentGreen).also {
            uploadSpeedValueText = it.second
        }
        val pingBlock = createMetricBlock(R.drawable.ic_clock_24, "Ping", TextStrong).also {
            pingValueText = it.second
        }

        row.addView(downloadBlock.first)
        row.addView(uploadBlock.first)
        row.addView(pingBlock.first)
        card.addView(row)
        return card
    }

    private fun createWifiDirectSettingsCard(): View {
        val card = createCard(tr("إعدادات Wi‑Fi Direct", "Wi‑Fi Direct settings", "Paramètres Wi‑Fi Direct", "Настройки Wi‑Fi Direct"))

        card.addView(createFormLabel(tr("اسم الشبكة", "Network name", "Nom du réseau", "Имя сети")))
        wifiNetworkEditText = createDarkInput("DIRECT-NR-SOG01-NetRelay").apply {
            setText(latestWifiNetwork.takeIf { it.isNotBlank() && it.startsWith("DIRECT-") } ?: "")
        }
        card.addView(wifiNetworkEditText)

        card.addView(createFormLabel(tr("كلمة السر", "Password", "Mot de passe", "Пароль")))
        wifiPassphraseEditText = createDarkInput("NetRelay123").apply {
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD
            setText(latestWifiPassphrase.takeIf { isValidWifiDirectPassphrase(it) } ?: "")
        }
        card.addView(wifiPassphraseEditText)

        val buttons = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER
            layoutDirection = View.LAYOUT_DIRECTION_LTR
            setPadding(0, dp(8), 0, 0)
        }
        buttons.addView(createCompactActionButton(tr("حفظ وتشغيل", "Save and start", "Enregistrer et démarrer", "Сохранить и запустить"), AccentGreen, PageBg) {
            saveWifiDirectSettings()
        })
        buttons.addView(createCompactActionButton(tr("فتح إعدادات الشبكة", "Open network settings", "Ouvrir le réseau", "Открыть сеть"), Surface2, TextBase) {
            openWifiSettings()
        })
        card.addView(buttons)
        return card
    }

    private fun createSettingsCard(): View {
        val card = createCard(tr("الإعدادات", "Settings", "Paramètres", "Настройки"))
        card.addView(createSettingsSectionTitle(tr("عام", "GENERAL", "GÉNÉRAL", "ОБЩИЕ")))
        card.addView(createLanguageSelector())
        card.addView(createSwitchSetting(
            tr("إبقاء الشاشة مضاءة أثناء الاتصال", "Keep screen awake while connected", "Garder l’écran allumé pendant la connexion", "Не выключать экран при подключении"),
            appPreferences.keepScreenAwake
        ) { appPreferences.keepScreenAwake = it; updateBridgeSummary() })
        card.addView(createSwitchSetting(
            tr("تأكيد قبل قطع الاتصال", "Confirm before disconnect", "Confirmer avant la déconnexion", "Подтверждать отключение"),
            appPreferences.confirmDisconnect
        ) { appPreferences.confirmDisconnect = it })
        card.addView(createSwitchSetting(
            tr("العودة للوحة التحكم بعد نجاح الاتصال", "Return to Dashboard after connection", "Revenir au tableau après connexion", "Возвращаться на панель после подключения"),
            appPreferences.returnDashboardAfterConnect
        ) { appPreferences.returnDashboardAfterConnect = it })

        card.addView(createSettingsSectionTitle(tr("الاتصال", "CONNECTION", "CONNEXION", "ПОДКЛЮЧЕНИЕ")))
        card.addView(createPreferredTransportSelector())

        card.addView(createSettingsSectionTitle(tr("الإشعارات", "NOTIFICATIONS", "NOTIFICATIONS", "УВЕДОМЛЕНИЯ")))
        card.addView(createSwitchSetting(
            tr("تفعيل الإشعارات داخل التطبيق", "Enable in-app notifications", "Activer les notifications internes", "Включить уведомления в приложении"),
            appPreferences.notificationsEnabled
        ) { appPreferences.notificationsEnabled = it })
        card.addView(createSwitchSetting(
            tr("إشعارات الاتصال", "Connection notifications", "Notifications de connexion", "Уведомления о подключении"),
            appPreferences.connectionNotifications
        ) { appPreferences.connectionNotifications = it })
        card.addView(createSwitchSetting(
            tr("إشعارات التحذير والخطأ", "Warning and error notifications", "Avertissements et erreurs", "Предупреждения и ошибки"),
            appPreferences.warningNotifications
        ) { appPreferences.warningNotifications = it })
        card.addView(TextView(this).apply {
            text = tr(
                "إشعار الخدمة الأمامية يبقى حسب متطلبات Android.",
                "The foreground-service notification remains enabled as required by Android.",
                "La notification du service de premier plan reste active selon Android.",
                "Уведомление фоновой службы остаётся включённым по требованиям Android."
            )
            textSize = 10f
            setTextColor(TextMuted)
            setPadding(dp(6), dp(4), dp(6), dp(6))
        })

        card.addView(createSettingsSectionTitle(tr("السجلات", "LOGS", "JOURNAUX", "ЖУРНАЛЫ")))
        card.addView(createSwitchSetting(
            tr("سجل تفصيلي آمن", "Safe detailed log view", "Vue détaillée sûre", "Безопасный подробный журнал"),
            appPreferences.detailedLogs
        ) { appPreferences.detailedLogs = it; renderLogs() })
        card.addView(createMaxLogsSelector())
        card.addView(createActionRow(
            createCompactActionButton(tr("مسح السجلات", "Clear logs", "Effacer les journaux", "Очистить журналы"), DangerSurface, Danger) { confirmClearLogs() },
            createCompactActionButton(tr("نسخ التشخيص", "Copy diagnostics", "Copier le diagnostic", "Копировать диагностику"), Surface2, TextBase) { copyDiagnosticsSummary() }
        ))

        card.addView(createSettingsSectionTitle(tr("التطبيق", "ABOUT / APP", "À PROPOS", "О ПРИЛОЖЕНИИ")))
        card.addView(TextView(this).apply {
            text = "Net Relay\n${tr("الإصدار", "Version", "Version", "Версия")}: ${com.netrelay.connectivity.BuildConfig.VERSION_NAME}\n" +
                "${tr("الحزمة", "Package", "Paquet", "Пакет")}: $packageName\n" +
                "${tr("وسائل النقل", "Transports", "Transports", "Транспорты")}: USB, Wi-Fi Direct"
            textSize = 12f
            setTextColor(TextBase)
            typeface = Typeface.MONOSPACE
            setPadding(dp(6), dp(8), dp(6), dp(8))
        })

        card.addView(createSettingsSectionTitle(tr("أدوات النظام", "SYSTEM TOOLS", "OUTILS SYSTÈME", "СИСТЕМНЫЕ ИНСТРУМЕНТЫ")))
        card.addView(createCompactActionButton(tr("طلب الصلاحيات", "Request permissions", "Demander les autorisations", "Запросить разрешения"), Surface2, TextBase) {
            requestMissingPermissions(forcePrompt = true)
        })
        card.addView(createCompactActionButton(tr("استثناء البطارية", "Ignore battery optimization", "Ignorer l'optimisation batterie", "Отключить оптимизацию батареи"), Surface2, TextBase) {
            requestBatteryOptimizationExemption()
        })
        card.addView(createCompactActionButton(tr("فتح إعدادات Wi‑Fi", "Open Wi‑Fi settings", "Ouvrir le Wi‑Fi", "Открыть Wi‑Fi"), Surface2, TextBase) {
            openWifiSettings()
        })
        card.addView(createCompactActionButton(tr("إيقاف الخدمة", "Stop service", "Arrêter le service", "Остановить службу"), DangerSurface, Danger) {
            disconnectNow()
        })
        return card
    }

    private fun createQuickActionsCard(): View {
        val card = createCard(tr("إجراءات سريعة", "Quick actions", "Actions rapides", "Быстрые действия"))
        val grid = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            layoutDirection = View.LAYOUT_DIRECTION_LTR
            setPadding(0, dp(8), 0, 0)
        }

        grid.addView(createQuickButton(tr("تحديث", "Refresh", "Actualiser", "Обновить"), R.drawable.ic_refresh_24) { renderLogs() })
        grid.addView(createQuickButton(tr("نسخ", "Copy", "Copier", "Копировать"), R.drawable.ic_copy_24) { copyAllConnectionInfo() })
        grid.addView(createQuickButton(tr("الشبكة", "Network", "Réseau", "Сеть"), R.drawable.ic_settings_24) { openWifiSettings() })
        grid.addView(createQuickButton(tr("مساعدة", "Help", "Aide", "Помощь"), R.drawable.ic_help_24) { requestMissingPermissions(forcePrompt = true) })

        transportsValueText = TextView(this).apply {
            text = tr("الأوضاع المسجلة: --", "Registered transports: --", "Transports enregistrés : --", "Зарегистрированные транспорты: --")
            textSize = 11f
            setTextColor(TextMuted)
            visibility = View.GONE
            setPadding(0, dp(6), 0, 0)
        }

        card.addView(grid)
        card.addView(transportsValueText)
        return card
    }

    private fun createLanguageSelector(): View {
        val panel = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            layoutDirection = View.LAYOUT_DIRECTION_LTR
            setPadding(0, 0, 0, dp(8))
        }
        panel.addView(createFormLabel(tr("لغة التطبيق", "App language", "Langue de l'application", "Язык приложения")))

        val languages = listOf(
            "en" to "English",
            "ar" to "العربية",
            "fr" to "Français",
            "ru" to "Русский"
        )
        val labels = languages.map { it.second }
        val adapter = object : ArrayAdapter<String>(this, android.R.layout.simple_spinner_item, labels) {
            override fun getView(position: Int, convertView: View?, parent: ViewGroup): View {
                return (super.getView(position, convertView, parent) as TextView).apply {
                    setTextColor(TextStrong)
                    textSize = 14f
                    gravity = Gravity.CENTER_VERTICAL
                    setPadding(dp(12), 0, dp(12), 0)
                }
            }

            override fun getDropDownView(position: Int, convertView: View?, parent: ViewGroup): View {
                return (super.getDropDownView(position, convertView, parent) as TextView).apply {
                    setTextColor(TextStrong)
                    setBackgroundColor(Surface2)
                    textSize = 15f
                    setPadding(dp(14), dp(12), dp(14), dp(12))
                }
            }
        }

        panel.addView(Spinner(this).apply {
            this.adapter = adapter
            setSelection(languages.indexOfFirst { it.first == appLanguage }.coerceAtLeast(0), false)
            background = rounded(InputBg, dp(12), StrokeSoft, 1)
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                dp(48)
            ).apply {
                topMargin = dp(6)
            }
            onItemSelectedListener = object : AdapterView.OnItemSelectedListener {
                override fun onItemSelected(parent: AdapterView<*>?, view: View?, position: Int, id: Long) {
                    val selectedLanguage = languages.getOrNull(position)?.first ?: return
                    if (selectedLanguage != appLanguage) {
                        setAppLanguage(selectedLanguage)
                    }
                }

                override fun onNothingSelected(parent: AdapterView<*>?) = Unit
            }
        })
        return panel
    }

    private fun createSettingsSectionTitle(title: String): TextView = TextView(this).apply {
        text = title
        textSize = 11f
        setTypeface(typeface, Typeface.BOLD)
        setTextColor(AccentGreen)
        setPadding(dp(4), dp(16), dp(4), dp(5))
        letterSpacing = 0.08f
    }

    private fun createSwitchSetting(label: String, checked: Boolean, onChanged: (Boolean) -> Unit): View {
        return LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            background = rounded(Surface2, dp(12), StrokeSoft, 1)
            setPadding(dp(12), dp(5), dp(8), dp(5))
            layoutParams = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, dp(50)).apply {
                topMargin = dp(5)
            }
            addView(TextView(this@MainActivity).apply {
                text = label
                textSize = 12f
                setTextColor(TextBase)
                maxLines = 2
                layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
            })
            addView(SwitchCompat(this@MainActivity).apply {
                isChecked = checked
                setOnCheckedChangeListener { _, value -> onChanged(value) }
            })
        }
    }

    private fun createPreferredTransportSelector(): View {
        val values = listOf(
            AppPreferences.PREFERRED_NONE to tr("لا شيء", "None", "Aucun", "Нет"),
            AppPreferences.PREFERRED_USB to "USB",
            AppPreferences.PREFERRED_WIFI_DIRECT to "Wi-Fi Direct"
        )
        return createSettingsSpinner(
            tr("وسيلة النقل المفضلة (اختيار فقط)", "Preferred transport (selection only)", "Transport préféré (sélection uniquement)", "Предпочтительный транспорт (только выбор)"),
            values.map { it.second },
            values.indexOfFirst { it.first == appPreferences.preferredTransport }.coerceAtLeast(0)
        ) { position -> appPreferences.preferredTransport = values[position].first }
    }

    private fun createMaxLogsSelector(): View {
        val values = listOf(100, 250, 500, 1000)
        return createSettingsSpinner(
            tr("أقصى عدد للسجلات المرئية", "Maximum visible log entries", "Nombre maximal de journaux visibles", "Максимум видимых записей"),
            values.map(Int::toString),
            values.indexOf(appPreferences.maxVisibleLogs).coerceAtLeast(0)
        ) { position -> appPreferences.maxVisibleLogs = values[position]; renderLogs() }
    }

    private fun createSettingsSpinner(label: String, values: List<String>, selected: Int, onSelected: (Int) -> Unit): View {
        return LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(0, dp(4), 0, dp(4))
            addView(createFormLabel(label))
            addView(Spinner(this@MainActivity).apply {
                adapter = object : ArrayAdapter<String>(this@MainActivity, android.R.layout.simple_spinner_item, values) {
                    override fun getView(position: Int, convertView: View?, parent: ViewGroup): View =
                        (super.getView(position, convertView, parent) as TextView).apply {
                            setTextColor(TextStrong); textSize = 13f; setPadding(dp(12), 0, dp(12), 0)
                        }
                    override fun getDropDownView(position: Int, convertView: View?, parent: ViewGroup): View =
                        (super.getDropDownView(position, convertView, parent) as TextView).apply {
                            setTextColor(TextStrong); setBackgroundColor(Surface2); setPadding(dp(12), dp(10), dp(12), dp(10))
                        }
                }
                setSelection(selected, false)
                background = rounded(InputBg, dp(12), StrokeSoft, 1)
                layoutParams = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, dp(46))
                onItemSelectedListener = object : AdapterView.OnItemSelectedListener {
                    override fun onItemSelected(parent: AdapterView<*>?, view: View?, position: Int, id: Long) = onSelected(position)
                    override fun onNothingSelected(parent: AdapterView<*>?) = Unit
                }
            })
        }
    }

    private fun createLogToolbar(): View {
        val container = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            isFocusableInTouchMode = true
        }
        val searchView = SearchView(this).apply {
            queryHint = tr("بحث في السجلات", "Search logs", "Rechercher", "Поиск в журнале")
            isIconified = false
            setOnQueryTextListener(object : SearchView.OnQueryTextListener {
                override fun onQueryTextSubmit(query: String?): Boolean = true
                override fun onQueryTextChange(newText: String?): Boolean {
                    logSearchQuery = newText.orEmpty()
                    renderLogs()
                    return true
                }
            })
        }
        styleLogSearchView(searchView, container)
        container.addView(searchView)
        val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
        val levels = listOf("ALL", "INFO", "WARN", "ERROR", "DEBUG")
        row.addView(Spinner(this).apply {
            val labels = listOf(
                tr("الكل", "All", "Tous", "Все"),
                "Info",
                tr("تحذير", "Warning", "Avertissement", "Предупреждение"),
                tr("خطأ", "Error", "Erreur", "Ошибка"),
                "Debug"
            )
            adapter = object : ArrayAdapter<String>(this@MainActivity, android.R.layout.simple_spinner_item, labels) {
                override fun getView(position: Int, convertView: View?, parent: ViewGroup): View =
                    (super.getView(position, convertView, parent) as TextView).apply {
                        setTextColor(TextStrong)
                        textSize = 13f
                        gravity = Gravity.CENTER_VERTICAL
                        setPadding(dp(12), 0, dp(8), 0)
                        setCompoundDrawablesRelativeWithIntrinsicBounds(0, 0, R.drawable.ic_expand_more_24, 0)
                        compoundDrawableTintList = ColorStateList.valueOf(TextMuted)
                        compoundDrawablePadding = dp(8)
                    }

                override fun getDropDownView(position: Int, convertView: View?, parent: ViewGroup): View =
                    (super.getDropDownView(position, convertView, parent) as TextView).apply {
                        setTextColor(TextStrong)
                        setBackgroundColor(Surface2)
                        textSize = 14f
                        setPadding(dp(14), dp(12), dp(14), dp(12))
                    }
            }
            setSelection(levels.indexOf(selectedLogLevel).coerceAtLeast(0), false)
            background = rounded(InputBg, dp(10), StrokeSoft, 1)
            onItemSelectedListener = object : AdapterView.OnItemSelectedListener {
                override fun onItemSelected(parent: AdapterView<*>?, view: View?, position: Int, id: Long) {
                    selectedLogLevel = levels[position]
                    renderLogs()
                }
                override fun onNothingSelected(parent: AdapterView<*>?) = Unit
            }
            layoutParams = LinearLayout.LayoutParams(0, dp(42), 1f)
        })
        row.addView(createQuickButton(tr("تلقائي", "Auto", "Auto", "Авто"), R.drawable.ic_refresh_24) {
            logAutoScroll = !logAutoScroll
            if (logAutoScroll) renderLogs()
        })
        row.addView(createQuickButton(tr("نسخ", "Copy", "Copier", "Копировать"), R.drawable.ic_copy_24) {
            copyToClipboard("Net Relay logs", Logger.exportLogs(visibleLogEntries))
        })
        row.addView(createQuickButton(tr("مسح", "Clear", "Effacer", "Очистить"), R.drawable.ic_chart_24) { confirmClearLogs() })
        container.addView(row)
        return container
    }

    private fun styleLogSearchView(searchView: SearchView, focusOwner: View) {
        val queryTextId = resources.getIdentifier("search_src_text", "id", "android")
        val queryText = searchView.findViewById<EditText>(queryTextId)
        queryText?.apply {
            setTextColor(TextStrong)
            setHintTextColor(TextMuted)
            textSize = 15f
            showSoftInputOnFocus = false

            // Opening the Logs tab must never summon the keyboard. The field
            // becomes focusable only after the user explicitly touches it.
            isFocusable = false
            isFocusableInTouchMode = false
            setOnTouchListener { view, _ ->
                showSoftInputOnFocus = true
                view.isFocusableInTouchMode = true
                view.isFocusable = true
                view.requestFocus()
                (getSystemService(Context.INPUT_METHOD_SERVICE) as InputMethodManager)
                    .showSoftInput(view, InputMethodManager.SHOW_IMPLICIT)
                false
            }
            setOnFocusChangeListener { _, hasFocus ->
                if (!hasFocus) showSoftInputOnFocus = false
            }
        }

        listOf("search_mag_icon", "search_close_btn", "search_button", "search_voice_btn")
            .map { resources.getIdentifier(it, "id", "android") }
            .filter { it != 0 }
            .forEach { id ->
                searchView.findViewById<ImageView>(id)?.imageTintList =
                    ColorStateList.valueOf(TextMuted)
            }

        searchView.clearFocus()
        focusOwner.requestFocus()
        searchView.post {
            searchView.clearFocus()
            focusOwner.requestFocus()
            (getSystemService(Context.INPUT_METHOD_SERVICE) as InputMethodManager)
                .hideSoftInputFromWindow(searchView.windowToken, 0)
        }
    }

    private fun confirmClearLogs() {
        AlertDialog.Builder(this)
            .setTitle(tr("مسح السجلات", "Clear logs", "Effacer les journaux", "Очистить журналы"))
            .setMessage(tr("هل تريد مسح سجل التطبيق الحالي؟", "Clear the current application log?", "Effacer le journal actuel ?", "Очистить текущий журнал приложения?"))
            .setPositiveButton(tr("مسح", "Clear", "Effacer", "Очистить")) { _, _ -> Logger.clearHistory(); renderLogs() }
            .setNegativeButton(tr("إلغاء", "Cancel", "Annuler", "Отмена"), null)
            .show()
    }

    private fun copyDiagnosticsSummary() {
        val summary = buildString {
            appendLine("Net Relay ${com.netrelay.connectivity.BuildConfig.VERSION_NAME}")
            appendLine("Package: $packageName")
            appendLine("Android: ${Build.VERSION.RELEASE} (API ${Build.VERSION.SDK_INT})")
            appendLine("Device: ${Build.MANUFACTURER} ${Build.MODEL}")
            appendLine("Selected transport: ${modeLabelFor(latestMode)}")
            appendLine("USB state: $latestUsbTransportState")
            appendLine("Wi-Fi Direct state: $latestWifiDirectTransportState")
            appendLine("Service state: $latestServiceState")
        }
        copyToClipboard("Net Relay diagnostics", summary)
    }

    private fun createLogsCard(): View {
        val card = createCard()
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            layoutDirection = View.LAYOUT_DIRECTION_LTR
        }
        row.addView(TextView(this).apply {
            text = if (activeTab == "logs") tr("السجلات", "Logs", "Journaux", "Журналы") else tr("السجلات الأخيرة", "Recent logs", "Journaux récents", "Последние записи")
            textSize = 15f
            setTypeface(typeface, Typeface.BOLD)
            setTextColor(TextStrong)
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
        })
        row.addView(TextView(this).apply {
            text = if (activeTab == "logs") tr("تحديث", "Refresh", "Actualiser", "Обновить") else tr("عرض الكل", "View all", "Tout afficher", "Показать все")
            textSize = 10f
            setTypeface(typeface, Typeface.BOLD)
            setTextColor(AccentGreen)
            setOnClickListener {
                if (activeTab == "logs") renderLogs() else selectBottomTab("logs")
            }
        })
        card.addView(row)

        if (activeTab == "logs") {
            card.addView(createLogToolbar())
            logsValueText = TextView(this).apply { visibility = View.GONE }
            card.addView(logsValueText)
            logsAdapter = object : ArrayAdapter<String>(this, android.R.layout.simple_list_item_1, mutableListOf()) {
                override fun getView(position: Int, convertView: View?, parent: ViewGroup): View {
                    return (super.getView(position, convertView, parent) as TextView).apply {
                        textSize = 10.5f
                        setTextColor(TextBase)
                        typeface = Typeface.MONOSPACE
                        setPadding(dp(8), dp(5), dp(8), dp(5))
                        maxLines = 8
                    }
                }
            }
            logsListView = ListView(this).apply {
                adapter = logsAdapter
                divider = null
                isNestedScrollingEnabled = true
                setBackgroundColor(Surface2)
                val availableHeight = (resources.displayMetrics.heightPixels - dp(190)).coerceAtLeast(dp(430))
                layoutParams = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, availableHeight).apply {
                    topMargin = dp(8)
                }
                setOnScrollListener(object : android.widget.AbsListView.OnScrollListener {
                    override fun onScrollStateChanged(view: android.widget.AbsListView?, scrollState: Int) {
                        if (scrollState == android.widget.AbsListView.OnScrollListener.SCROLL_STATE_TOUCH_SCROLL) {
                            logAutoScroll = false
                        }
                    }
                    override fun onScroll(view: android.widget.AbsListView?, firstVisibleItem: Int, visibleItemCount: Int, totalItemCount: Int) = Unit
                })
            }
            card.addView(logsListView)
            return card
        }

        logsValueText = TextView(this).apply {
            text = tr("لا يوجد سجل بعد", "No logs yet", "Aucun journal", "Журнал пуст")
            textSize = 11f
            setTextColor(TextBase)
            setLineSpacing(dp(1).toFloat(), 1f)
            typeface = Typeface.MONOSPACE
            setPadding(dp(8), dp(7), dp(8), dp(7))
            background = rounded(Surface2, dp(12), StrokeSoft, 1)
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT
            ).apply {
                topMargin = dp(8)
            }
        }
        card.addView(logsValueText)
        return card
    }

    private fun createBottomNavigation(): View {
        val bar = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER
            layoutDirection = uiLayoutDirection()
            background = rounded(BottomBarBg, dp(18), StrokeSoft, 1)
            setPadding(dp(8), dp(5), dp(8), dp(5))
        }
        bottomNavItems.clear()
        bar.addView(createBottomNavItem("home", R.drawable.ic_home_24, tr("لوحة التحكم", "Dashboard", "Tableau", "Панель")))
        bar.addView(createBottomNavItem("connection", R.drawable.ic_globe_24, tr("الاتصال", "Connection", "Connexion", "Подключение")))
        bar.addView(createBottomNavItem("service", R.drawable.ic_shield_check_24, tr("الخدمة", "Service", "Service", "Служба")))
        bar.addView(createBottomNavItem("logs", R.drawable.ic_chart_24, tr("السجلات", "Logs", "Journaux", "Журналы")))
        bar.addView(createBottomNavItem("settings", R.drawable.ic_settings_24, tr("الإعدادات", "Settings", "Paramètres", "Настройки")))
        return bar
    }

    private fun createEndpointBlock(iconRes: Int, title: String, initialValue: String, color: Int): Pair<View, TextView> {
        val valueText = TextView(this).apply {
            text = initialValue
            textSize = 11f
            setTextColor(color)
            gravity = Gravity.CENTER
            maxLines = 1
            ellipsize = TextUtils.TruncateAt.END
            textDirection = View.TEXT_DIRECTION_LTR
        }
        val block = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
            layoutDirection = View.LAYOUT_DIRECTION_LTR
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
            addView(createIconCircle(iconRes, color, dp(54), dp(27)))
            addView(TextView(this@MainActivity).apply {
                text = title
                textSize = 11f
                setTextColor(TextBase)
                gravity = Gravity.CENTER
                setPadding(0, dp(4), 0, 0)
            })
            addView(valueText)
        }
        return block to valueText
    }

    private fun createDashedLine(): View {
        return TextView(this).apply {
            text = "• - - -"
            textSize = 12f
            gravity = Gravity.CENTER
            setTextColor(TextMuted)
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 0.75f)
        }
    }

    private fun createDetailRow(iconRes: Int, label: String, value: String): Pair<View, TextView> {
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            layoutDirection = View.LAYOUT_DIRECTION_LTR
            setPadding(0, dp(5), 0, dp(5))
            minimumHeight = dp(30)
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT
            )
        }
        row.addView(createIconImage(iconRes, AccentGreen, 15).apply {
            layoutParams = LinearLayout.LayoutParams(dp(30), dp(18))
        })
        row.addView(TextView(this).apply {
            text = label
            textSize = 11f
            setTextColor(TextMuted)
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
        })
        val valueText = TextView(this).apply {
            text = value
            textSize = 11f
            setTextColor(TextStrong)
            gravity = Gravity.RIGHT
            maxLines = 2
            ellipsize = TextUtils.TruncateAt.END
            setLineSpacing(0f, 1.05f)
            textDirection = View.TEXT_DIRECTION_LTR
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1.35f)
        }
        row.addView(valueText)
        return row to valueText
    }

    private fun createTransportModeButton(mode: String, iconRes: Int, title: String): View {
        val card = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
            layoutDirection = View.LAYOUT_DIRECTION_LTR
            setPadding(dp(5), dp(6), dp(5), dp(6))
            background = rounded(Surface2, dp(14), StrokeSoft, 1)
            layoutParams = LinearLayout.LayoutParams(0, dp(58), 1f).apply {
                leftMargin = dp(4)
                rightMargin = dp(4)
            }
            setOnClickListener { selectTransportMode(mode) }
        }

        card.addView(createIconImage(iconRes, TextMuted, 18).apply {
            layoutParams = LinearLayout.LayoutParams(dp(20), dp(20))
        })
        card.addView(TextView(this).apply {
            text = title
            textSize = 11f
            setTypeface(typeface, Typeface.BOLD)
            setTextColor(TextStrong)
            gravity = Gravity.CENTER
            includeFontPadding = false
        })
        val status = TextView(this).apply {
            text = tr("غير متصل", "Disconnected", "Déconnecté", "Отключено")
            textSize = 9f
            setTextColor(TextMuted)
            gravity = Gravity.CENTER
            includeFontPadding = false
        }
        card.addView(status)
        modeCards[mode] = card
        modeStatusTexts[mode] = status
        return card
    }

    private fun addInfoRow(parent: LinearLayout, iconRes: Int, label: String, initialValue: String, onCopy: () -> Unit): TextView {
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            layoutDirection = View.LAYOUT_DIRECTION_LTR
            setPadding(dp(7), dp(2), dp(5), dp(2))
            background = rounded(Surface2, dp(11), StrokeSoft, 1)
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                dp(27)
            ).apply {
                topMargin = dp(4)
            }
        }
        row.addView(createIconImage(iconRes, TextMuted, 15).apply {
            layoutParams = LinearLayout.LayoutParams(dp(24), dp(18))
        })
        row.addView(TextView(this).apply {
            text = label
            textSize = 10f
            setTextColor(TextMuted)
            maxLines = 1
            ellipsize = TextUtils.TruncateAt.END
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
        })
        val valueText = TextView(this).apply {
            text = initialValue
            textSize = 11f
            setTextColor(AccentBlue)
            gravity = Gravity.RIGHT
            maxLines = 1
            ellipsize = TextUtils.TruncateAt.END
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1.15f)
        }
        row.addView(valueText)
        row.addView(FrameLayout(this).apply {
            background = rounded(Surface3, dp(9), StrokeSoft, 1)
            layoutParams = LinearLayout.LayoutParams(dp(34), dp(22)).apply {
                leftMargin = dp(8)
            }
            addView(createIconImage(R.drawable.ic_copy_24, TextBase, 14).apply {
                layoutParams = FrameLayout.LayoutParams(dp(14), dp(14), Gravity.CENTER)
            })
            setOnClickListener { onCopy() }
        })
        parent.addView(row)
        return valueText
    }

    private fun createCard(title: String? = null): LinearLayout {
        return LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            layoutDirection = uiLayoutDirection()
            background = rounded(CardBg, dp(16), StrokeSoft, 1)
            elevation = dp(2).toFloat()
            setPadding(dp(9), dp(9), dp(9), dp(9))
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT
            ).apply {
                topMargin = dp(6)
            }
            if (!title.isNullOrBlank()) {
                addView(TextView(this@MainActivity).apply {
                    text = title
                    textSize = 15f
                    setTypeface(typeface, Typeface.BOLD)
                    setTextColor(TextStrong)
                    gravity = Gravity.LEFT
                })
            }
        }
    }

    private fun createCardHeader(title: String, status: String, statusColor: Int): Pair<View, TextView> {
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        row.addView(TextView(this).apply {
            text = title
            textSize = 13f
            setTypeface(typeface, Typeface.BOLD)
            setTextColor(TextStrong)
            gravity = Gravity.LEFT
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
        })
        val statusText = TextView(this).apply {
            text = status
            textSize = 11f
            setTypeface(typeface, Typeface.BOLD)
            setTextColor(statusColor)
            gravity = Gravity.CENTER
            background = rounded(Surface3, dp(18), statusColor, 1)
            setPadding(dp(10), dp(4), dp(10), dp(4))
        }
        row.addView(statusText)
        return row to statusText
    }

    private fun createMetricBlock(iconRes: Int, label: String, color: Int): Pair<View, TextView> {
        val valueText = TextView(this).apply {
            text = "--"
            textSize = 16f
            setTypeface(typeface, Typeface.BOLD)
            setTextColor(color)
            gravity = Gravity.CENTER
            includeFontPadding = false
        }

        val block = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
            background = rounded(Surface2, dp(14), StrokeSoft, 1)
            setPadding(dp(8), dp(8), dp(8), dp(8))
            layoutParams = LinearLayout.LayoutParams(0, dp(82), 1f).apply {
                leftMargin = dp(4)
                rightMargin = dp(4)
            }
            addView(createIconImage(iconRes, color, 20).apply {
                layoutParams = LinearLayout.LayoutParams(dp(22), dp(22))
            })
            addView(valueText)
            addView(TextView(this@MainActivity).apply {
                text = label
                textSize = 11f
                setTextColor(TextMuted)
                gravity = Gravity.CENTER
                includeFontPadding = false
            })
        }

        return block to valueText
    }

    private fun createFormLabel(label: String): TextView {
        return TextView(this).apply {
            text = label
            textSize = 12f
            setTextColor(TextMuted)
            setPadding(0, dp(8), 0, dp(3))
        }
    }

    private fun createCompactActionButton(label: String, backgroundColor: Int, textColor: Int, onClick: () -> Unit): View {
        return TextView(this).apply {
            text = label
            textSize = 12f
            setTypeface(typeface, Typeface.BOLD)
            setTextColor(textColor)
            gravity = Gravity.CENTER
            background = rounded(backgroundColor, dp(12), StrokeSoft, 1)
            layoutParams = LinearLayout.LayoutParams(0, dp(40), 1f).apply {
                leftMargin = dp(4)
                rightMargin = dp(4)
                topMargin = dp(6)
            }
            setOnClickListener { onClick() }
        }
    }

    private fun createDarkInput(hintValue: String): EditText {
        return EditText(this).apply {
            hint = hintValue
            setSingleLine(true)
            textSize = 14f
            setTextColor(TextStrong)
            setHintTextColor(TextMuted)
            inputType = InputType.TYPE_CLASS_TEXT
            background = rounded(InputBg, dp(12), StrokeSoft, 1)
            setPadding(dp(12), 0, dp(12), 0)
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                dp(46)
            ).apply {
                topMargin = dp(8)
            }
        }
    }

    private fun createActionRow(left: View, right: View): View {
        return LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            layoutDirection = View.LAYOUT_DIRECTION_LTR
            addView(left)
            addView(right)
        }
    }

    private fun createQuickButton(label: String, iconRes: Int, onClick: () -> Unit): View {
        return LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER
            layoutDirection = View.LAYOUT_DIRECTION_LTR
            background = rounded(Surface2, dp(12), StrokeSoft, 1)
            setPadding(dp(6), 0, dp(6), 0)
            layoutParams = LinearLayout.LayoutParams(0, dp(36), 1f).apply {
                leftMargin = dp(4)
                rightMargin = dp(4)
                topMargin = dp(5)
            }
            addView(createIconImage(iconRes, TextMuted, 14).apply {
                layoutParams = LinearLayout.LayoutParams(dp(15), dp(15)).apply {
                    rightMargin = dp(5)
                }
            })
            addView(TextView(this@MainActivity).apply {
                text = label
                textSize = 11f
                setTextColor(TextBase)
                gravity = Gravity.CENTER
                includeFontPadding = false
            })
            setOnClickListener { onClick() }
        }
    }

    private fun createSolidButton(label: String, backgroundColor: Int, textColor: Int, onClick: () -> Unit): Button {
        return Button(this).apply {
            text = label
            textSize = 14f
            isAllCaps = false
            setTypeface(typeface, Typeface.BOLD)
            setTextColor(textColor)
            backgroundTintList = ColorStateList.valueOf(backgroundColor)
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                dp(52)
            ).apply {
                topMargin = dp(12)
            }
            setOnClickListener { onClick() }
        }
    }

    private fun createOutlineButton(label: String, color: Int, fill: Int, onClick: () -> Unit): Button {
        return Button(this).apply {
            text = label
            textSize = 15f
            isAllCaps = false
            setTypeface(typeface, Typeface.BOLD)
            setTextColor(color)
            background = rounded(fill, dp(13), color, 1)
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                dp(34)
            )
            setOnClickListener { onClick() }
        }
    }

    private fun createBottomNavItem(tab: String, iconRes: Int, label: String): View {
        val selected = tab == activeTab
        val iconView = createIconImage(iconRes, if (selected) AccentGreen else TextMuted, 20).apply {
            layoutParams = LinearLayout.LayoutParams(dp(22), dp(22))
        }
        val labelView = TextView(this).apply {
            text = label
            textSize = 9f
            gravity = Gravity.CENTER
            setTextColor(if (selected) AccentGreen else TextMuted)
            maxLines = 1
            ellipsize = TextUtils.TruncateAt.END
        }

        return LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
            layoutDirection = View.LAYOUT_DIRECTION_LTR
            layoutParams = LinearLayout.LayoutParams(0, dp(48), 1f)
            addView(iconView)
            addView(labelView)
            setOnClickListener { selectBottomTab(tab) }
            bottomNavItems[tab] = BottomNavItemRefs(this, iconView, labelView)
        }
    }

    private fun updateBottomNavSelection() {
        bottomNavItems.forEach { (tab, refs) ->
            val selected = tab == activeTab
            val color = if (selected) AccentGreen else TextMuted
            refs.icon.imageTintList = ColorStateList.valueOf(color)
            refs.label.setTextColor(color)
            refs.container.background = if (selected) {
                rounded(Color.TRANSPARENT, dp(12), Color.TRANSPARENT, 0)
            } else {
                null
            }
        }
    }

    private fun divider(): View {
        return View(this).apply {
            setBackgroundColor(StrokeSoft)
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                dp(1)
            ).apply {
                topMargin = dp(4)
                bottomMargin = dp(4)
            }
        }
    }

    private fun dp(value: Int): Int {
        return (value * resources.displayMetrics.density + 0.5f).toInt()
    }

    private fun uiLayoutDirection(): Int =
        if (appLanguage == "ar") View.LAYOUT_DIRECTION_RTL else View.LAYOUT_DIRECTION_LTR

    private fun createIconImage(resourceId: Int, color: Int, sizeDp: Int): ImageView {
        return ImageView(this).apply {
            setImageResource(resourceId)
            imageTintList = ColorStateList.valueOf(color)
            scaleType = ImageView.ScaleType.FIT_CENTER
            layoutParams = ViewGroup.LayoutParams(dp(sizeDp), dp(sizeDp))
        }
    }

    private fun createIconCircle(resourceId: Int, color: Int, sizePx: Int, iconSizePx: Int): View {
        return FrameLayout(this).apply {
            background = rounded(Color.TRANSPARENT, sizePx / 2, color, 2)
            layoutParams = LinearLayout.LayoutParams(sizePx, sizePx)
            addView(createIconImage(resourceId, color, 1).apply {
                layoutParams = FrameLayout.LayoutParams(iconSizePx, iconSizePx, Gravity.CENTER)
            })
        }
    }

    private fun rounded(color: Int, radiusPx: Int, strokeColor: Int = Color.TRANSPARENT, strokeDp: Int = 0): GradientDrawable {
        return GradientDrawable().apply {
            shape = GradientDrawable.RECTANGLE
            cornerRadius = radiusPx.toFloat()
            setColor(color)
            if (strokeDp > 0) {
                setStroke(dp(strokeDp), strokeColor)
            }
        }
    }

    private fun oval(color: Int): GradientDrawable {
        return GradientDrawable().apply {
            shape = GradientDrawable.OVAL
            setColor(color)
        }
    }

    private fun gradient(start: Int, end: Int, radiusPx: Int): GradientDrawable {
        return GradientDrawable(GradientDrawable.Orientation.TL_BR, intArrayOf(start, end)).apply {
            cornerRadius = radiusPx.toFloat()
        }
    }

    private fun gradientOval(start: Int, end: Int): GradientDrawable {
        return GradientDrawable(GradientDrawable.Orientation.TL_BR, intArrayOf(start, end)).apply {
            shape = GradientDrawable.OVAL
        }
    }

    private fun registerTransports() {
        TransportFactory.createDefaultTransports(this).forEach { (transportType, transport) ->
            viewModel.registerTransport(transportType, transport)
        }
        logger.info(tag, "Registered transports: ${viewModel.availableTransports.value.joinToString()}")
    }

    private fun observeViewModel() {
        lifecycleScope.launch {
            repeatOnLifecycle(Lifecycle.State.STARTED) {
                launch {
                    viewModel.uiState.collect { state ->
                        lastUiState = state
                        renderUiState(state)
                        renderLogs()
                    }
                }

                launch {
                    viewModel.availableTransports.collect { transports ->
                        transportsValueText.text = if (transports.isEmpty()) {
                            tr("الأوضاع المسجلة: لا يوجد", "Registered transports: none", "Transports enregistrés : aucun", "Зарегистрированные транспорты: нет")
                        } else {
                            tr("الأوضاع المسجلة: ${transports.joinToString()}", "Registered transports: ${transports.joinToString()}", "Transports enregistrés : ${transports.joinToString()}", "Зарегистрированные транспорты: ${transports.joinToString()}")
                        }
                    }
                }

                launch {
                    ConnectionRuntimeStore.serviceState.collect { serviceState ->
                        latestServiceState = serviceState.ifBlank { stoppedText() }
                        serviceStateValueText.text = latestServiceState
                        updateBridgeSummary()
                    }
                }

                launch {
                    ConnectionRuntimeStore.selectedMode.collect { selectedMode ->
                        latestMode = selectedMode
                        selectedModeValueText.text = modeLabelFor(selectedMode)
                        updateModeCards()
                        updateBridgeSummary()
                    }
                }

                launch {
                    ConnectionRuntimeStore.usbTransportState.collect { state ->
                        handleTransportTransition(TransportUiStatePolicy.USB, observedUsbState, state)
                        observedUsbState = state
                        latestUsbTransportState = state
                        updateModeCards()
                        renderConnectionInfo()
                        updateBridgeSummary()
                        refreshUsbState()
                    }
                }

                launch {
                    ConnectionRuntimeStore.wifiDirectTransportState.collect { state ->
                        handleTransportTransition(TransportUiStatePolicy.WIFI_DIRECT, observedWifiState, state)
                        observedWifiState = state
                        latestWifiDirectTransportState = state
                        updateModeCards()
                        renderConnectionInfo()
                        updateBridgeSummary()
                    }
                }

                launch {
                    ConnectionRuntimeStore.wifiListenerEndpoint.collect { endpoint ->
                        latestWifiEndpoint = endpoint
                        renderConnectionInfo()
                        updateBridgeSummary()
                    }
                }

                launch {
                    ConnectionRuntimeStore.wifiNetworkName.collect { networkName ->
                        latestWifiNetwork = networkName
                        wifiNetworkValueText.text = networkName
                        if (!wifiNetworkEditText.hasFocus() && networkName.isNotBlank() && networkName.startsWith("DIRECT-")) {
                            wifiNetworkEditText.setText(networkName)
                        }
                        renderConnectionInfo()
                    }
                }

                launch {
                    ConnectionRuntimeStore.wifiPassphrase.collect { passphrase ->
                        latestWifiPassphrase = passphrase
                        wifiPassphraseValueText.text = passphrase
                        if (!wifiPassphraseEditText.hasFocus() && isValidWifiDirectPassphrase(passphrase)) {
                            wifiPassphraseEditText.setText(passphrase)
                        }
                        renderConnectionInfo()
                    }
                }

                launch {
                    ConnectionRuntimeStore.wifiClientEndpoint.collect { client ->
                        latestWifiClient = client.ifBlank { noDeviceText() }
                        renderConnectionInfo()
                        updateBridgeSummary()
                    }
                }

                launch {
                    ConnectionRuntimeStore.proxyEndpoint.collect { endpoint ->
                        latestProxyEndpoint = endpoint
                        renderConnectionInfo()
                        updateBridgeSummary()
                    }
                }

                launch {
                    ConnectionRuntimeStore.proxyStatus.collect { status ->
                        latestProxyStatus = status.ifBlank { stoppedText() }
                        proxyStatusValueText.text = latestProxyStatus
                        updateBridgeSummary()
                    }
                }

                launch {
                    ConnectionRuntimeStore.lastProtocolMessage.collect { protocolActivity ->
                        latestProtocol = protocolActivity.ifBlank {
                            tr("لا يوجد نشاط اتصال بعد", "No connection activity yet", "Aucune activité de connexion", "Активности подключения пока нет")
                        }
                        protocolValueText.text = latestProtocol
                        renderLogs()
                    }
                }
            }
        }
    }

    private fun renderUiState(state: UiState) {
        when (state) {
            UiState.Idle -> {
                topStatusChipText.text = tr("جاهز", "Ready", "Prêt", "Готово")
                heroTitleText.text = tr("جاهز", "Ready", "Prêt", "Готово")
                heroSubtitleText.text = tr("اختر وضع الاتصال ثم شغل الخدمة", "Choose a transport to start", "Choisissez un transport", "Выберите транспорт")
                if (connectedSinceMillis == null) connectionDurationText.text = "--:--"
            }
            UiState.Connecting -> {
                topStatusChipText.text = tr("جاري الاتصال", "Connecting", "Connexion", "Подключение")
                heroTitleText.text = tr("جاري الاتصال", "Connecting", "Connexion", "Подключение")
                heroSubtitleText.text = tr("يتم تجهيز قناة الاتصال", "Preparing the connection channel", "Préparation du canal", "Подготовка канала")
            }
            is UiState.Connected -> {
                topStatusChipText.text = tr("متصل", "Connected", "Connecté", "Подключено")
                heroTitleText.text = tr("متصل", "Connected", "Connecté", "Подключено")
                heroSubtitleText.text = tr("الاتصال نشط وجسر الإنترنت يعمل", "Connection and Internet bridge are active", "La connexion et le pont Internet sont actifs", "Подключение и интернет-мост активны")
                if (connectedSinceMillis == null) {
                    connectedSinceMillis = System.currentTimeMillis()
                }
            }
            UiState.Disconnected -> {
                topStatusChipText.text = tr("غير متصل", "Disconnected", "Déconnecté", "Отключено")
                heroTitleText.text = tr("غير متصل", "Disconnected", "Déconnecté", "Отключено")
                heroSubtitleText.text = tr("الهاتف غير متصل", "Phone is not connected", "Téléphone non connecté", "Телефон не подключён")
                connectedSinceMillis = null
                connectionDurationText.text = "--:--"
            }
            is UiState.Error -> {
                topStatusChipText.text = tr("خطأ", "Error", "Erreur", "Ошибка")
                heroTitleText.text = tr("خطأ", "Error", "Erreur", "Ошибка")
                heroSubtitleText.text = state.message
            }
        }
        updateBridgeSummary()
    }

    private fun updateBridgeSummary() {
        if (!::topStatusChipText.isInitialized) return
        val activeMode = when {
            latestUsbTransportState == TransportUiState.ACTIVE -> TransportUiStatePolicy.USB
            latestWifiDirectTransportState == TransportUiState.ACTIVE -> TransportUiStatePolicy.WIFI_DIRECT
            else -> null
        }
        val activatingMode = when {
            latestUsbTransportState == TransportUiState.ACTIVATING -> TransportUiStatePolicy.USB
            latestWifiDirectTransportState == TransportUiState.ACTIVATING -> TransportUiStatePolicy.WIFI_DIRECT
            else -> null
        }
        val hasError = latestUsbTransportState == TransportUiState.ERROR ||
            latestWifiDirectTransportState == TransportUiState.ERROR

        if (activeMode != null && connectedSinceMillis == null) {
            connectedSinceMillis = System.currentTimeMillis()
        }
        if (activeMode == null) {
            connectedSinceMillis = null
            if (::connectionDurationText.isInitialized) connectionDurationText.text = "--:--"
        }

        val status = when {
            activeMode == TransportUiStatePolicy.USB -> tr("USB متصل", "USB connected", "USB connecté", "USB подключён")
            activeMode == TransportUiStatePolicy.WIFI_DIRECT -> tr("Wi‑Fi Direct متصل", "Wi-Fi Direct connected", "Wi-Fi Direct connecté", "Wi-Fi Direct подключён")
            activatingMode == TransportUiStatePolicy.USB -> tr("جارٍ تفعيل USB...", "Activating USB…", "Activation USB…", "Активация USB…")
            activatingMode == TransportUiStatePolicy.WIFI_DIRECT -> tr("جارٍ تفعيل Wi‑Fi Direct...", "Activating Wi-Fi Direct…", "Activation Wi-Fi Direct…", "Активация Wi-Fi Direct…")
            hasError -> tr("فشل التفعيل", "Activation failed", "Échec de l’activation", "Ошибка активации")
            else -> tr("غير متصل", "Disconnected", "Déconnecté", "Отключено")
        }
        topStatusChipText.text = status
        heroTitleText.text = status
        heroSubtitleText.text = when {
            activeMode != null -> tr("النقل الفعلي نشط ويمكن للكمبيوتر استخدام خدمة الهاتف", "Transport is active and available to the computer", "Le transport est actif et disponible pour l’ordinateur", "Транспорт активен и доступен компьютеру")
            activatingMode != null -> tr("يتم تجهيز قناة الاتصال", "Preparing the connection channel", "Préparation du canal", "Подготовка канала")
            hasError -> tr("تعذر تفعيل وسيلة النقل", "Transport activation failed", "Échec de l’activation du transport", "Не удалось активировать транспорт")
            else -> tr("لا توجد وسيلة نقل نشطة", "No active transport", "Aucun transport actif", "Нет активного транспорта")
        }
        if (::bridgeStateText.isInitialized) {
            bridgeStateText.text = tr(
                "الخدمة: $latestServiceState — النقل: $status",
                "Service: $latestServiceState — transport: $status",
                "Service : $latestServiceState — transport : $status",
                "Служба: $latestServiceState — транспорт: $status"
            )
        }
        updateKeepScreenAwake(activeMode != null)
    }

    private fun handleTransportTransition(mode: String, previous: TransportUiState?, current: TransportUiState) {
        if (previous == null || previous == current) return
        val category = if (mode == TransportUiStatePolicy.USB) {
            AppNotificationCategory.USB
        } else {
            AppNotificationCategory.WIFI_DIRECT
        }
        val title = if (mode == TransportUiStatePolicy.USB) "usb" else "wfd"
        when (current) {
            TransportUiState.ACTIVATING -> addAppNotification(category, AppNotificationSeverity.INFO, title, "activating")
            TransportUiState.ACTIVE -> {
                addAppNotification(category, AppNotificationSeverity.SUCCESS, title, "ready")
                if (appPreferences.returnDashboardAfterConnect && activeTab != "home") {
                    selectBottomTab("home")
                }
            }
            TransportUiState.ERROR -> addAppNotification(category, AppNotificationSeverity.ERROR, title, "failed")
            TransportUiState.INACTIVE -> {
                if (previous in setOf(TransportUiState.ACTIVE, TransportUiState.ACTIVATING)) {
                    if (!explicitDisconnectPending) {
                        addAppNotification(
                            category,
                            if (mode == TransportUiStatePolicy.WIFI_DIRECT && previous == TransportUiState.ACTIVE) AppNotificationSeverity.WARNING else AppNotificationSeverity.INFO,
                            title,
                            if (mode == TransportUiStatePolicy.WIFI_DIRECT && previous == TransportUiState.ACTIVE) "group_lost" else "disconnected"
                        )
                    }
                }
            }
        }
    }

    private fun updateKeepScreenAwake(isConnected: Boolean) {
        if (appPreferences.keepScreenAwake && isConnected) {
            window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        } else {
            window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        }
    }

    private fun registerConnectivityCallback() {
        if (connectivityCallbackRegistered) return
        val manager = getSystemService(ConnectivityManager::class.java) ?: return
        runCatching { manager.registerDefaultNetworkCallback(connectivityCallback) }
            .onSuccess { connectivityCallbackRegistered = true }
        updateInternetAvailability()
    }

    private fun unregisterConnectivityCallback() {
        if (!connectivityCallbackRegistered) return
        val manager = getSystemService(ConnectivityManager::class.java) ?: return
        runCatching { manager.unregisterNetworkCallback(connectivityCallback) }
        connectivityCallbackRegistered = false
    }

    private fun updateInternetAvailability() {
        val manager = getSystemService(ConnectivityManager::class.java) ?: return
        val network = manager.activeNetwork
        val capabilities = network?.let { manager.getNetworkCapabilities(it) }
        val available = capabilities?.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) == true &&
            capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED)
        runOnUiThread {
            val previous = lastInternetAvailable
            lastInternetAvailable = available
            if (previous != null && previous != available) {
                addAppNotification(
                    AppNotificationCategory.INTERNET,
                    if (available) AppNotificationSeverity.SUCCESS else AppNotificationSeverity.WARNING,
                    "internet",
                    if (available) "internet_available" else "internet_lost"
                )
            }
        }
    }

    private fun renderConnectionInfo() {
        val wfdActive = TransportUiStatePolicy.shouldExposeWifiDirectInfo(latestWifiDirectTransportState)
        val anyActive = latestUsbTransportState == TransportUiState.ACTIVE || wfdActive
        if (::wifiNetworkValueText.isInitialized) {
            wifiNetworkValueText.text = if (wfdActive) latestWifiNetwork else unavailableText()
        }
        if (::wifiPassphraseValueText.isInitialized) {
            wifiPassphraseValueText.text = if (wfdActive) latestWifiPassphrase else unavailableText()
        }
        if (::wifiEndpointValueText.isInitialized) {
            wifiEndpointValueText.text = if (anyActive && endpointLooksAvailable(latestWifiEndpoint)) {
                latestWifiEndpoint
            } else {
                unavailableText()
            }
        }
        if (::wifiClientValueText.isInitialized) {
            wifiClientValueText.text = if (anyActive && clientLooksConnected(latestWifiClient)) latestWifiClient else noDeviceText()
        }
        if (::proxyEndpointValueText.isInitialized) {
            proxyEndpointValueText.text = if (anyActive && endpointLooksAvailable(latestProxyEndpoint)) {
                latestProxyEndpoint
            } else {
                unavailableText()
            }
        }
        if (::phoneAddressText.isInitialized) {
            phoneAddressText.text = when {
                wfdActive -> extractHost(latestWifiEndpoint).ifBlank { unavailableText() }
                latestUsbTransportState == TransportUiState.ACTIVE -> "USB / ADB"
                else -> unavailableText()
            }
        }
        if (::computerAddressText.isInitialized) {
            computerAddressText.text = if (anyActive && clientLooksConnected(latestWifiClient)) {
                latestWifiClient
            } else {
                tr("بانتظار الاتصال", "Waiting for connection", "En attente de connexion", "Ожидание подключения")
            }
        }
    }

    private fun updateConnectionDuration() {
        val start = connectedSinceMillis ?: return
        val elapsedSeconds = ((System.currentTimeMillis() - start) / 1000).coerceAtLeast(0)
        val hours = elapsedSeconds / 3600
        val minutes = (elapsedSeconds % 3600) / 60
        val seconds = elapsedSeconds % 60
        connectionDurationText.text = if (hours > 0) {
            "%02d:%02d:%02d".format(hours, minutes, seconds)
        } else {
            "%02d:%02d".format(minutes, seconds)
        }
    }

    private fun updateNetworkMetrics() {
        if (!::downloadSpeedValueText.isInitialized || !::uploadSpeedValueText.isInitialized) {
            return
        }

        val now = SystemClock.elapsedRealtime()
        val rxBytes = readTrafficBytes(download = true)
        val txBytes = readTrafficBytes(download = false)

        if (lastTrafficSampleMillis > 0L && rxBytes >= 0L && txBytes >= 0L) {
            val elapsedSeconds = ((now - lastTrafficSampleMillis).coerceAtLeast(1L)) / 1000.0
            val downRate = ((rxBytes - lastRxBytes).coerceAtLeast(0L) / elapsedSeconds).toLong()
            val upRate = ((txBytes - lastTxBytes).coerceAtLeast(0L) / elapsedSeconds).toLong()
            downloadSpeedValueText.text = formatBytesPerSecond(downRate)
            uploadSpeedValueText.text = formatBytesPerSecond(upRate)
        }

        lastRxBytes = rxBytes
        lastTxBytes = txBytes
        lastTrafficSampleMillis = now

        if (::pingValueText.isInitialized && now - lastPingSampleMillis > 5000L && !pingInFlight) {
            lastPingSampleMillis = now
            measurePingAsync()
        }
    }

    private fun readTrafficBytes(download: Boolean): Long {
        val uidValue = if (download) {
            TrafficStats.getUidRxBytes(applicationInfo.uid)
        } else {
            TrafficStats.getUidTxBytes(applicationInfo.uid)
        }
        if (uidValue != TrafficStats.UNSUPPORTED.toLong()) {
            return uidValue
        }
        return if (download) TrafficStats.getTotalRxBytes() else TrafficStats.getTotalTxBytes()
    }

    private fun formatBytesPerSecond(bytesPerSecond: Long): String {
        val kbps = bytesPerSecond * 8.0 / 1000.0
        return when {
            kbps >= 1000.0 -> "%.1f Mbps".format(kbps / 1000.0)
            kbps > 0.0 -> "%.0f Kbps".format(kbps)
            else -> "0 Kbps"
        }
    }

    private fun measurePingAsync() {
        pingInFlight = true
        Thread {
            val started = SystemClock.elapsedRealtime()
            val result = try {
                Socket().use { socket ->
                    socket.connect(InetSocketAddress("8.8.8.8", 53), 1500)
                }
                "${SystemClock.elapsedRealtime() - started} ms"
            } catch (_: Exception) {
                unavailableText()
            }

            runOnUiThread {
                if (::pingValueText.isInitialized) {
                    pingValueText.text = result
                }
                pingInFlight = false
            }
        }.start()
    }

    private fun updateModeCards() {
        modeCards.forEach { (mode, card) ->
            val state = when (mode) {
                TransportUiStatePolicy.USB -> latestUsbTransportState
                TransportUiStatePolicy.WIFI_DIRECT -> latestWifiDirectTransportState
                else -> TransportUiState.INACTIVE
            }
            card.background = if (state == TransportUiState.ACTIVE) {
                GradientDrawable(
                    GradientDrawable.Orientation.TL_BR,
                    intArrayOf(SelectedModeA, SelectedModeB)
                ).apply {
                    cornerRadius = dp(14).toFloat()
                    setStroke(dp(1), AccentGreen)
                }
            } else if (state == TransportUiState.ERROR) {
                rounded(DangerSurface, dp(14), Danger, 1)
            } else {
                rounded(Surface2, dp(14), StrokeSoft, 1)
            }
            modeStatusTexts[mode]?.apply {
                text = when (state) {
                    TransportUiState.INACTIVE -> tr("غير متصل", "Disconnected", "Déconnecté", "Отключено")
                    TransportUiState.ACTIVATING -> tr("جارٍ التفعيل...", "Activating…", "Activation…", "Активация…")
                    TransportUiState.ACTIVE -> tr("متصل", "Connected", "Connecté", "Подключено")
                    TransportUiState.ERROR -> tr("فشل التفعيل", "Activation failed", "Échec de l’activation", "Ошибка активации")
                }
                setTextColor(
                    when (state) {
                        TransportUiState.ACTIVE -> AccentGreen
                        TransportUiState.ACTIVATING -> AccentBlue
                        TransportUiState.ERROR -> Danger
                        TransportUiState.INACTIVE -> TextMuted
                    }
                )
            }
        }
    }

    private fun loadAppLanguage(): String {
        return if (::appPreferences.isInitialized) appPreferences.language else AppPreferences.DEFAULT_LANGUAGE
    }

    private fun setAppLanguage(languageCode: String) {
        if (languageCode == appLanguage) {
            return
        }

        appLanguage = languageCode
        appPreferences.language = languageCode
        AppCompatDelegate.setApplicationLocales(LocaleListCompat.forLanguageTags(languageCode))
    }

    private fun tr(ar: String, en: String, fr: String, ru: String): String {
        return when (appLanguage) {
            "en" -> en
            "fr" -> fr
            "ru" -> ru
            else -> ar
        }
    }

    private fun unavailableText(): String = tr("غير متاح", "Unavailable", "Indisponible", "Недоступно")

    private fun noDeviceText(): String = tr("لا يوجد جهاز متصل", "No device connected", "Aucun appareil connecté", "Нет подключённых устройств")

    private fun stoppedText(): String = tr("متوقف", "Stopped", "Arrêté", "Остановлено")

    private fun requestBatteryOptimizationExemption() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.M) {
            Toast.makeText(this, tr("النظام لا يحتاج هذا الإعداد", "This Android version does not need it", "Cette version Android n'en a pas besoin", "Этой версии Android это не нужно"), Toast.LENGTH_SHORT).show()
            return
        }

        val powerManager = getSystemService(PowerManager::class.java)
        if (powerManager?.isIgnoringBatteryOptimizations(packageName) == true) {
            Toast.makeText(this, tr("التطبيق مستثنى من تحسين البطارية", "Battery optimization is already ignored", "L'optimisation batterie est déjà ignorée", "Оптимизация батареи уже отключена"), Toast.LENGTH_SHORT).show()
            return
        }

        val intent = Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS).apply {
            data = Uri.parse("package:$packageName")
        }
        startActivity(intent)
    }

    private fun requestMissingPermissions(forcePrompt: Boolean = false) {
        val missingPermissions = buildList {
            add(Manifest.permission.ACCESS_FINE_LOCATION)

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                add(Manifest.permission.NEARBY_WIFI_DEVICES)
                add(Manifest.permission.POST_NOTIFICATIONS)
            }
        }.filter { permission ->
            ContextCompat.checkSelfPermission(this, permission) != PackageManager.PERMISSION_GRANTED
        }

        if (missingPermissions.isEmpty()) {
            logger.info(tag, "Runtime permissions already granted")
            if (forcePrompt) {
                Toast.makeText(this, tr("الصلاحيات جاهزة", "Permissions are ready", "Autorisations prêtes", "Разрешения предоставлены"), Toast.LENGTH_SHORT).show()
            }
            renderLogs()
            return
        }

        if (forcePrompt || Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            permissionLauncher.launch(missingPermissions.toTypedArray())
        }
    }

    private fun renderLogs() {
        if (!::appPreferences.isInitialized) return
        val filtered = Logger.getHistory().asSequence()
            .filter { appPreferences.detailedLogs || it.level != "DEBUG" }
            .filter { selectedLogLevel == "ALL" || it.level == selectedLogLevel }
            .filter { logSearchQuery.isBlank() || "${it.tag} ${it.message}".contains(logSearchQuery, ignoreCase = true) }
            .toList()
            .takeLast(appPreferences.maxVisibleLogs)
        visibleLogEntries = filtered
        logsAdapter?.apply {
            clear()
            addAll(filtered.map { entry -> "${entry.timestamp}  ${entry.level.padEnd(5)}  ${Logger.redact(entry.message)}" })
            notifyDataSetChanged()
        }
        if (logAutoScroll && filtered.isNotEmpty()) {
            logsListView?.post { logsListView?.setSelection(filtered.lastIndex) }
        }
        if (::logsValueText.isInitialized && logsValueText.visibility != View.GONE) {
            logsValueText.text = filtered.takeLast(5).joinToString("\n") { entry ->
                "${entry.timestamp}   ${entry.level}   ${Logger.redact(entry.message)}"
            }.ifBlank { tr("لا يوجد سجل بعد", "No logs yet", "Aucun journal", "Журнал пуст") }
        }
    }

    private fun ensureConnectionServiceRunning(mode: String) {
        val serviceIntent = Intent(this, ConnectionService::class.java)
        serviceIntent.putExtra(ConnectionService.EXTRA_TRANSPORT_MODE, mode)
        ContextCompat.startForegroundService(this, serviceIntent)
        logger.info(tag, "Connection service start requested for mode: $mode")
    }

    private fun registerUsbStateReceiver() {
        if (usbStateReceiverRegistered) return
        val filter = IntentFilter().apply {
            addAction(UsbManager.ACTION_USB_DEVICE_ATTACHED)
            addAction(UsbManager.ACTION_USB_DEVICE_DETACHED)
            addAction(Intent.ACTION_POWER_CONNECTED)
            addAction(Intent.ACTION_POWER_DISCONNECTED)
            addAction(ACTION_USB_STATE)
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            registerReceiver(usbStateReceiver, filter, Context.RECEIVER_NOT_EXPORTED)
        } else {
            @Suppress("DEPRECATION")
            registerReceiver(usbStateReceiver, filter)
        }
        usbStateReceiverRegistered = true
    }

    private fun unregisterUsbStateReceiver() {
        if (!usbStateReceiverRegistered) return
        runCatching { unregisterReceiver(usbStateReceiver) }
        usbStateReceiverRegistered = false
    }

    /** Canonical state reconciliation used by lifecycle, navigation and manual refresh. */
    private fun refreshTransportUiState() {
        latestUsbTransportState = ConnectionRuntimeStore.usbTransportState.value
        latestWifiDirectTransportState = ConnectionRuntimeStore.wifiDirectTransportState.value
        latestMode = ConnectionRuntimeStore.selectedMode.value
        refreshUsbState()
        updateModeCards()
        renderConnectionInfo()
        updateBridgeSummary()
    }

    private fun refreshUsbState(showFeedback: Boolean = false) {
        val attached = isUsbCableAttached()
        val adbEnabled = isUsbDebuggingEnabled()
        if (!attached && latestUsbTransportState != TransportUiState.ACTIVE) {
            ConnectionRuntimeStore.updateTransportUiState(TransportUiStatePolicy.USB, TransportUiState.INACTIVE)
            latestUsbTransportState = TransportUiState.INACTIVE
        }
        if (::usbStateSummaryText.isInitialized) {
            val cableText = if (attached) tr("الكابل متصل", "cable connected", "câble connecté", "кабель подключён") else tr("الكابل غير متصل", "cable disconnected", "câble déconnecté", "кабель отключён")
            val adbText = when (adbEnabled) {
                true -> tr("تصحيح USB مفعّل", "USB debugging enabled", "débogage USB activé", "отладка USB включена")
                false -> tr("تصحيح USB متوقف", "USB debugging disabled", "débogage USB désactivé", "отладка USB выключена")
                null -> tr("تعذر قراءة تصحيح USB", "USB debugging state unavailable", "état du débogage USB indisponible", "состояние отладки USB недоступно")
            }
            usbStateSummaryText.text = "USB: $cableText — $adbText"
        }
        updateModeCards()
        if (showFeedback) {
            Toast.makeText(this, tr("تم تحديث حالة USB", "USB status refreshed", "État USB actualisé", "Состояние USB обновлено"), Toast.LENGTH_SHORT).show()
        }
    }

    private fun isUsbCableAttached(): Boolean {
        val usbState = runCatching {
            registerReceiver(null, IntentFilter(ACTION_USB_STATE))
        }.getOrNull()
        if (usbState?.hasExtra("connected") == true) {
            return usbState.getBooleanExtra("connected", false)
        }
        val battery = runCatching {
            registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
        }.getOrNull()
        return battery?.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0) == BatteryManager.BATTERY_PLUGGED_USB
    }

    private fun isUsbDebuggingEnabled(): Boolean? {
        return runCatching {
            Settings.Global.getInt(contentResolver, Settings.Global.ADB_ENABLED, 0) == 1
        }.getOrNull()
    }

    private fun requestUsbActivation() {
        val adbEnabled = isUsbDebuggingEnabled()
        if (!TransportPrerequisitePolicy.mayStartUsb(adbEnabled)) {
            ConnectionRuntimeStore.updateTransportUiState(TransportUiStatePolicy.USB, TransportUiState.INACTIVE)
            latestUsbTransportState = TransportUiState.INACTIVE
            Snackbar.make(
                findViewById(android.R.id.content),
                tr("فعّل تصحيح أخطاء USB للاتصال بالكمبيوتر", "Enable USB debugging to connect to the computer", "Activez le débogage USB pour connecter l’ordinateur", "Включите отладку USB для подключения к компьютеру"),
                Snackbar.LENGTH_LONG
            ).show()
            addAppNotification(AppNotificationCategory.USB, AppNotificationSeverity.WARNING, "usb", "usb_debugging")
            refreshUsbState()
            return
        }
        ConnectionRuntimeStore.updateSelectedMode(TransportUiStatePolicy.USB)
        ConnectionRuntimeStore.markTransportActivating(TransportUiStatePolicy.USB)
        ensureConnectionServiceRunning(TransportUiStatePolicy.USB)
    }

    private fun requiredWifiDirectPermissions(): List<String> = buildList {
        add(Manifest.permission.ACCESS_FINE_LOCATION)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            add(Manifest.permission.NEARBY_WIFI_DEVICES)
        }
    }

    private fun hasWifiDirectRuntimePermissions(): Boolean {
        return requiredWifiDirectPermissions().all { permission ->
            ContextCompat.checkSelfPermission(this, permission) == PackageManager.PERMISSION_GRANTED
        }
    }

    private fun requestWifiDirectActivation() {
        pendingWifiDirectActivation = true
        if (!hasWifiDirectRuntimePermissions()) {
            permissionLauncher.launch(requiredWifiDirectPermissions().toTypedArray())
            return
        }
        continueWifiDirectActivation()
    }

    private fun continueWifiDirectActivation() {
        if (!pendingWifiDirectActivation) return
        if (TransportPrerequisitePolicy.needsLocationDialog(
                hasPermission = hasWifiDirectRuntimePermissions(),
                locationEnabled = isLocationServiceEnabled()
            )
        ) {
            showLocationRequiredDialog()
            return
        }
        startWifiDirectActivation()
    }

    private fun isLocationServiceEnabled(): Boolean {
        val locationManager = getSystemService(LocationManager::class.java) ?: return false
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            locationManager.isLocationEnabled
        } else {
            @Suppress("DEPRECATION")
            locationManager.isProviderEnabled(LocationManager.GPS_PROVIDER) ||
                locationManager.isProviderEnabled(LocationManager.NETWORK_PROVIDER)
        }
    }

    private fun showLocationRequiredDialog() {
        if (isFinishing || isDestroyed) return
        AlertDialog.Builder(this)
            .setTitle(tr("تشغيل الموقع", "Enable location", "Activer la localisation", "Включить геолокацию"))
            .setMessage(tr("يحتاج Wi-Fi Direct إلى تشغيل الموقع لإنشاء الاتصال.", "Wi-Fi Direct requires location to establish a connection.", "Wi-Fi Direct nécessite la localisation pour établir la connexion.", "Для подключения Wi-Fi Direct требуется геолокация."))
            .setPositiveButton(tr("تشغيل الموقع", "Open location settings", "Ouvrir la localisation", "Открыть настройки")) { _, _ ->
                startActivity(Intent(Settings.ACTION_LOCATION_SOURCE_SETTINGS))
            }
            .setNegativeButton(tr("إلغاء", "Cancel", "Annuler", "Отмена")) { _, _ ->
                pendingWifiDirectActivation = false
                pendingWifiDirectSettings = null
                ConnectionRuntimeStore.updateTransportUiState(
                    TransportUiStatePolicy.WIFI_DIRECT,
                    TransportUiState.INACTIVE
                )
            }
            .setOnCancelListener {
                pendingWifiDirectActivation = false
                pendingWifiDirectSettings = null
                ConnectionRuntimeStore.updateTransportUiState(
                    TransportUiStatePolicy.WIFI_DIRECT,
                    TransportUiState.INACTIVE
                )
            }
            .show()
        addAppNotification(AppNotificationCategory.WIFI_DIRECT, AppNotificationSeverity.WARNING, "wfd", "location_required")
    }

    private fun startWifiDirectActivation() {
        if (!pendingWifiDirectActivation || !hasWifiDirectRuntimePermissions() || !isLocationServiceEnabled()) return
        if (ConnectionRuntimeStore.transportUiState(TransportUiStatePolicy.WIFI_DIRECT) in
            setOf(TransportUiState.ACTIVATING, TransportUiState.ACTIVE)
        ) {
            pendingWifiDirectActivation = false
            pendingWifiDirectSettings = null
            return
        }
        pendingWifiDirectActivation = false
        ConnectionRuntimeStore.updateSelectedMode(TransportUiStatePolicy.WIFI_DIRECT)
        ConnectionRuntimeStore.markTransportActivating(TransportUiStatePolicy.WIFI_DIRECT)
        val settings = pendingWifiDirectSettings.also { pendingWifiDirectSettings = null }
        val serviceIntent = Intent(this, ConnectionService::class.java).apply {
            putExtra(ConnectionService.EXTRA_TRANSPORT_MODE, TransportUiStatePolicy.WIFI_DIRECT)
            settings?.let { (networkName, passphrase) ->
                putExtra(ConnectionService.EXTRA_WIFI_NETWORK_NAME, networkName)
                putExtra(ConnectionService.EXTRA_WIFI_PASSPHRASE, passphrase)
            }
        }
        ContextCompat.startForegroundService(this, serviceIntent)
        logger.info(tag, "Wi-Fi Direct activation requested after permission/location gates")
    }

    private fun selectTransportMode(mode: String) {
        when (mode) {
            TransportUiStatePolicy.USB -> requestUsbActivation()
            TransportUiStatePolicy.WIFI_DIRECT -> requestWifiDirectActivation()
            else -> return
        }
        renderLogs()
    }

    private fun disconnectNow() {
        if (appPreferences.confirmDisconnect) {
            AlertDialog.Builder(this)
                .setTitle(tr("قطع الاتصال", "Disconnect", "Déconnecter", "Отключить"))
                .setMessage(tr("هل تريد قطع اتصال Net Relay؟", "Disconnect Net Relay?", "Déconnecter Net Relay ?", "Отключить Net Relay?"))
                .setPositiveButton(tr("قطع الاتصال", "Disconnect", "Déconnecter", "Отключить")) { _, _ -> performDisconnect() }
                .setNegativeButton(tr("إلغاء", "Cancel", "Annuler", "Отмена"), null)
                .show()
            return
        }
        performDisconnect()
    }

    private fun performDisconnect() {
        explicitDisconnectPending = true
        durationHandler.postDelayed({ explicitDisconnectPending = false }, 2500)
        val stopIntent = Intent(this, ConnectionService::class.java)
            .setAction(ConnectionService.ACTION_STOP_SERVICE)
        startService(stopIntent)
        connectedSinceMillis = null
        if (::connectionDurationText.isInitialized) connectionDurationText.text = "--:--"
        logger.info(tag, "Explicit connection service stop requested")
        addAppNotification(AppNotificationCategory.CONNECTION, AppNotificationSeverity.INFO, "connection", "disconnected")
        renderLogs()
    }

    private fun saveWifiDirectSettings() {
        val networkName = wifiNetworkEditText.text.toString().trim()
        val passphrase = wifiPassphraseEditText.text.toString().trim()

        if (!isValidWifiDirectNetworkName(networkName)) {
            Toast.makeText(
                this,
                tr("اسم الشبكة يجب أن يبدأ بـ DIRECT- وطوله لا يتجاوز 32 حرفا", "The network name must start with DIRECT- and be at most 32 characters", "Le nom doit commencer par DIRECT- et contenir au plus 32 caractères", "Имя сети должно начинаться с DIRECT- и содержать не более 32 символов"),
                Toast.LENGTH_LONG
            ).show()
            return
        }

        if (!isValidWifiDirectPassphrase(passphrase)) {
            Toast.makeText(
                this,
                tr("كلمة السر يجب أن تكون بين 8 و63 حرفا", "The password must contain 8 to 63 characters", "Le mot de passe doit contenir de 8 à 63 caractères", "Пароль должен содержать от 8 до 63 символов"),
                Toast.LENGTH_LONG
            ).show()
            return
        }

        pendingWifiDirectSettings = networkName to passphrase
        requestWifiDirectActivation()
        renderLogs()
    }

    private fun isValidWifiDirectNetworkName(value: String): Boolean {
        return value.startsWith("DIRECT-") &&
            value.length in 9..32 &&
            value.all { character ->
                character.code in 32..126 && (
                    character.isLetterOrDigit() ||
                        character == '-' ||
                        character == '_' ||
                        character == '.'
                )
            }
    }

    private fun isValidWifiDirectPassphrase(value: String): Boolean {
        return value.length in 8..63 &&
            value.all { character -> character.code in 32..126 }
    }

    private fun applyLaunchTransportOverride(sourceIntent: Intent?): Boolean {
        val requestedMode = sourceIntent?.getStringExtra(ConnectionService.EXTRA_TRANSPORT_MODE)
            ?: return false
        selectTransportMode(requestedMode)
        return true
    }

    private fun modeLabelFor(mode: String): String {
        return when (mode) {
            "wifiDirect" -> "Wi‑Fi Direct"
            "usb" -> "USB"
            else -> tr("غير معروف", "Unknown", "Inconnu", "Неизвестно")
        }
    }

    private fun UiState.toDisplayText(): String {
        return when (this) {
            UiState.Idle -> tr("جاهز", "Ready", "Prêt", "Готово")
            UiState.Connecting -> tr("جاري الاتصال", "Connecting", "Connexion", "Подключение")
            is UiState.Connected -> tr("تم الاتصال مع ${device.deviceName}", "Connected to ${device.deviceName}", "Connecté à ${device.deviceName}", "Подключено к ${device.deviceName}")
            UiState.Disconnected -> tr("تم قطع الاتصال", "Disconnected", "Déconnecté", "Отключено")
            is UiState.Error -> tr("خطأ: $message", "Error: $message", "Erreur : $message", "Ошибка: $message")
        }
    }

    private fun copyToClipboard(label: String, value: String) {
        val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        clipboard.setPrimaryClip(ClipData.newPlainText(label, value))
        Toast.makeText(this, tr("تم النسخ", "Copied", "Copié", "Скопировано"), Toast.LENGTH_SHORT).show()
    }

    private fun copyAllConnectionInfo() {
        val info = listOf(
            "SSID: $latestWifiNetwork",
            "Listener: $latestWifiEndpoint",
            "Proxy: $latestProxyEndpoint",
            "Computer: $latestWifiClient",
            "Mode: ${modeLabelFor(latestMode)}"
        ).joinToString("\n")
        copyToClipboard("NetRelay connection info", info)
    }

    private fun openWifiSettings() {
        startActivity(Intent(Settings.ACTION_WIFI_SETTINGS))
    }

    private fun endpointLooksAvailable(value: String): Boolean {
        return value.contains('.') || value.contains(':')
    }

    private fun clientLooksConnected(value: String): Boolean {
        return value.contains('.') || value.contains(':')
    }

    private fun extractHost(endpoint: String): String {
        val withoutScheme = endpoint.substringAfter("://", endpoint)
        return withoutScheme.substringBefore(':').takeIf { it.contains('.') }.orEmpty()
    }

    companion object {
        private const val ACTION_USB_STATE = "android.hardware.usb.action.USB_STATE"
        private const val APP_PREFS = "netrelay_link_app"
        private const val KEY_LANGUAGE = "language"
        private val PageBg = Color.parseColor("#06111F")
        private val CardBg = Color.parseColor("#0B1A2A")
        private val BottomBarBg = Color.parseColor("#0C1B2B")
        private val Surface2 = Color.parseColor("#102337")
        private val Surface3 = Color.parseColor("#152B43")
        private val InputBg = Color.parseColor("#081726")
        private val DangerSurface = Color.parseColor("#2A1720")
        private val StrokeSoft = Color.parseColor("#22384D")
        private val TextStrong = Color.parseColor("#F2F7FF")
        private val TextBase = Color.parseColor("#D9E6F5")
        private val TextMuted = Color.parseColor("#93A8BE")
        private val AccentGreen = Color.parseColor("#23E894")
        private val AccentBlue = Color.parseColor("#36A8FF")
        private val Danger = Color.parseColor("#FF606A")
        private val LogoA = Color.parseColor("#09D4B7")
        private val LogoB = Color.parseColor("#1678FF")
        private val SelectedModeA = Color.parseColor("#0A513C")
        private val SelectedModeB = Color.parseColor("#103D52")
    }
}
