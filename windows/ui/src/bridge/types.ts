export type Language = 'en' | 'ar' | 'fr' | 'ru'
export type Theme = 'midnight' | 'oled' | 'light'
export type UiLifecycleState = 'idle' | 'connecting' | 'reconnecting' | 'connected' | 'disconnected' | 'error'
export type TransportActivity = 'inactive' | 'connecting' | 'active' | 'error'
export type LogLevel = 'DEBUG' | 'INFO' | 'WARN' | 'ERROR'

export interface TransportViewState {
  key: 'usb' | 'wifiDirect'
  label: string
  state: TransportActivity
  endpoint: string
  available: boolean
}

export interface ConnectionSnapshot {
  appVersion: string
  language: Language
  theme: Theme
  uiState: UiLifecycleState
  statusMessage: string
  isConnected: boolean
  isConnecting: boolean
  selectedTransport: string
  activeTransport: string
  connectionDuration: string
  phone: {
    name: string
    id: string
    status: string
  }
  internet: {
    available: boolean
    status: string
    detail: string
  }
  adapter: {
    running: boolean
    status: string
  }
  endpoints: {
    target: string
    proxy: string
    gateway: string
    socks: string
    control: string
  }
  usb: {
    cableStatus: 'connected' | 'disconnected' | 'unknown'
    debuggingStatus: 'enabled' | 'disabled' | 'unknown'
    adbStatus: 'ready' | 'unauthorized' | 'offline' | 'notFound' | 'unknown'
    authorization: 'authorized' | 'unauthorized' | 'unknown'
    deviceName: string
    deviceId: string
    controlEndpoint: string
    gatewayEndpoint: string
  }
  wifiDirect: {
    status: 'inactive' | 'connecting' | 'connected' | 'error'
    groupFormed: boolean | null
    isGroupOwner: boolean | null
    groupOwner: string
    networkName: string
    passphrase: string
    phoneIp: string
    windowsIp: string
    profileStatus: string
    hasSavedProfile: boolean | null
    isSearching: boolean
    controlEndpoint: string
    gatewayEndpoint: string
    availableNetworks: string[]
  }
  transports: TransportViewState[]
  notificationsCount: number
  appInfo: {
    product: string
    version: string
    ui: string
    runtime: string
    architecture: string
    webView2Version: string
    appDataPath: string
  }
}

export interface NetworkMetrics {
  download: string
  upload: string
  ping: string
  totalReceived: string
  totalSent: string
  duration: string
  downloadBps: number
  uploadBps: number
  totalReceivedBytes: number
  totalSentBytes: number
  activeTcpFlows: number
  activeUdpFlows: number
  totalTcpFlows: number
  totalUdpFlows: number
  hasActiveSession: boolean
}

export interface ApplicationSettings {
  language: Language
  theme: Theme
  startWithWindows: boolean
  startMinimized: boolean
  closeToTray: boolean
  autoReconnect: boolean
  preferredTransport: 'none' | 'usb' | 'wifiDirect'
  restoreLastPage: boolean
  lastPage: 'dashboard' | 'connection' | 'statistics' | 'logs' | 'settings' | 'about'
  loggingLevel: 'normal' | 'detailed'
  maxUiLogs: 200 | 500 | 1000
}

export interface AppNotification {
  id: string
  timestamp: string
  severity: 'info' | 'success' | 'warning' | 'error'
  category: string
  titleKey: string
  messageKey: string
  messageArgs: Record<string, string>
  isRead: boolean
}

export interface NotificationSnapshot {
  unreadCount: number
  items: AppNotification[]
}

export interface LogEntry {
  timestamp: string
  level: LogLevel
  tag: string
  message: string
}

export type BridgeCommand =
  | 'uiReady'
  | 'getInitialState'
  | 'connectUsb'
  | 'prepareUsbPhone'
  | 'reinstallAndroidApp'
  | 'connectWifiDirect'
  | 'selectWifiDirectNetwork'
  | 'connectWifiNetworkOnly'
  | 'disconnect'
  | 'refreshUsb'
  | 'refreshWifiDirect'
  | 'getConnectionState'
  | 'getNetworkMetrics'
  | 'getRecentLogs'
  | 'getSettings'
  | 'updateSettings'
  | 'getNotifications'
  | 'markNotificationRead'
  | 'markAllNotificationsRead'
  | 'clearNotifications'
  | 'clearUiState'
  | 'openSettingsSection'

export interface CommandRequest {
  type: 'command'
  id: string
  command: BridgeCommand
  payload: Record<string, unknown>
}

export interface CommandResult<T = unknown> {
  type: 'commandResult'
  id: string
  success: boolean
  errorCode?: string
  message?: string
  payload?: T
}

export interface StateMessage<T = unknown> {
  type: 'state'
  topic: 'initial' | 'connection' | 'metrics' | 'logs' | 'settings' | 'notifications' | 'toast' | 'androidAction'
  payload: T
}

export type AndroidActionState = 'idle' | 'checking' | 'installing' | 'updating' | 'opening'

export type BridgeMessage = CommandResult | StateMessage
