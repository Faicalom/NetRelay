import { useCallback, useEffect, useMemo, useState } from 'react'
import { hasNativeBridge, sendCommand, subscribeBridge } from '../bridge/webview'
import type { AndroidActionState, ApplicationSettings, BridgeCommand, ConnectionSnapshot, LogEntry, NetworkMetrics, NotificationSnapshot } from '../bridge/types'
import { translations } from '../i18n'

export interface ToastNotice { id: string; tone: 'success' | 'info' | 'warning' | 'error'; message: string }

const emptyConnection: ConnectionSnapshot = {
  appVersion: '1.0.0', language: 'en', theme: 'midnight', uiState: 'idle', statusMessage: '',
  isConnected: false, isConnecting: false, selectedTransport: '', activeTransport: '', connectionDuration: '--:--',
  phone: { name: 'No phone', id: '', status: 'Not connected' },
  internet: { available: false, status: 'Not connected', detail: 'No phone connection' },
  adapter: { running: false, status: 'Inactive' },
  endpoints: { target: '', proxy: '', gateway: '', socks: '127.0.0.1:8990', control: '' },
  usb: { cableStatus: 'unknown', debuggingStatus: 'unknown', adbStatus: 'unknown', authorization: 'unknown', deviceName: '', deviceId: '', controlEndpoint: '127.0.0.1:8988', gatewayEndpoint: '' },
  wifiDirect: { status: 'inactive', groupFormed: null, isGroupOwner: null, groupOwner: '', networkName: '', passphrase: '', phoneIp: '', windowsIp: '', profileStatus: '', hasSavedProfile: null, isSearching: false, controlEndpoint: '192.168.49.1:8988', gatewayEndpoint: '', availableNetworks: [] },
  transports: [
    { key: 'usb', label: 'USB', state: 'inactive', endpoint: '127.0.0.1:8988', available: true },
    { key: 'wifiDirect', label: 'Wi‑Fi Direct', state: 'inactive', endpoint: '', available: true },
  ],
  notificationsCount: 0,
  appInfo: { product: 'Net Relay', version: '1.0.0', ui: 'Chromium / WebView2', runtime: '', architecture: '', webView2Version: '', appDataPath: '' },
}

const emptyMetrics: NetworkMetrics = {
  download: '0 bps', upload: '0 bps', ping: '-- ms', totalReceived: '0 B', totalSent: '0 B',
  duration: '--:--', downloadBps: 0, uploadBps: 0,
  totalReceivedBytes: 0, totalSentBytes: 0, activeTcpFlows: 0, activeUdpFlows: 0,
  totalTcpFlows: 0, totalUdpFlows: 0, hasActiveSession: false,
}

const emptySettings: ApplicationSettings = {
  language: 'en', theme: 'midnight', startWithWindows: false, startMinimized: false, closeToTray: false,
  autoReconnect: true, preferredTransport: 'none', restoreLastPage: true, lastPage: 'dashboard',
  loggingLevel: 'normal', maxUiLogs: 500,
}

const emptyNotifications: NotificationSnapshot = { unreadCount: 0, items: [] }

export function useNetRelayState() {
  const [connection, setConnection] = useState(emptyConnection)
  const [metrics, setMetrics] = useState(emptyMetrics)
  const [logs, setLogs] = useState<LogEntry[]>([])
  const [settings, setSettings] = useState<ApplicationSettings>(emptySettings)
  const [notifications, setNotifications] = useState<NotificationSnapshot>(emptyNotifications)
  const [notices, setNotices] = useState<ToastNotice[]>([])
  const [busyCommand, setBusyCommand] = useState<BridgeCommand | null>(null)
  const [androidAction, setAndroidAction] = useState<AndroidActionState>('idle')
  const [androidSignatureRepair, setAndroidSignatureRepair] = useState<{ serial?: string } | null>(null)
  const [initialStateReady, setInitialStateReady] = useState(false)
  const native = useMemo(hasNativeBridge, [])

  const pushNotice = useCallback((tone: ToastNotice['tone'], message: string) => {
    const id = `${Date.now()}-${Math.random()}`
    setNotices((current) => [...current.slice(-3), { id, tone, message }])
    window.setTimeout(() => setNotices((current) => current.filter((item) => item.id !== id)), 4200)
  }, [])

  useEffect(() => subscribeBridge((message) => {
    if (message.type !== 'state') return
    if (message.topic === 'initial') {
      const payload = message.payload as { connection: ConnectionSnapshot; metrics: NetworkMetrics; logs: LogEntry[]; settings: ApplicationSettings; notifications: NotificationSnapshot }
      setConnection(payload.connection)
      setMetrics(payload.metrics)
      setLogs(payload.logs.slice(-1000))
      setSettings(payload.settings)
      setNotifications(payload.notifications)
      setInitialStateReady(true)
    } else if (message.topic === 'connection') {
      setConnection(message.payload as ConnectionSnapshot)
    } else if (message.topic === 'metrics') {
      setMetrics(message.payload as NetworkMetrics)
    } else if (message.topic === 'logs') {
      setLogs((message.payload as LogEntry[]).slice(-1000))
    } else if (message.topic === 'settings') {
      setSettings(message.payload as ApplicationSettings)
    } else if (message.topic === 'notifications') {
      setNotifications(message.payload as NotificationSnapshot)
    } else if (message.topic === 'toast') {
      const toast = message.payload as { tone: ToastNotice['tone']; message: string }
      pushNotice(toast.tone, toast.message)
    } else if (message.topic === 'androidAction') {
      const payload = message.payload as { state: AndroidActionState }
      setAndroidAction(payload.state)
    }
  }), [pushNotice])

  useEffect(() => {
    if (!native) return
    sendCommand('uiReady').catch((error: Error) => pushNotice('error', error.message))
  }, [native, pushNotice])

  const command = useCallback(async (name: BridgeCommand, payload: Record<string, unknown> = {}) => {
    if (busyCommand) return false
    setBusyCommand(name)
    try {
      const result = await sendCommand(name, payload)
      if (!result.success) {
        if (result.errorCode === 'ANDROID_SIGNATURE_MISMATCH') {
          const details = result.payload as { serial?: string } | undefined
          setAndroidSignatureRepair({ serial: details?.serial })
        }
        pushNotice('error', result.message ?? translations(settings.language).operationFailed)
        return false
      }
      if (name === 'reinstallAndroidApp') setAndroidSignatureRepair(null)
      return true
    } catch (error) {
      pushNotice('error', error instanceof Error ? error.message : translations(settings.language).operationFailed)
      return false
    } finally {
      setBusyCommand(null)
    }
  }, [busyCommand, pushNotice, settings.language])

  return { connection, metrics, logs, setLogs, settings, notifications, notices, busyCommand, androidAction, androidSignatureRepair, dismissAndroidSignatureRepair: () => setAndroidSignatureRepair(null), native, initialStateReady, command, pushNotice }
}
