import { useEffect, useMemo, useRef, useState } from 'react'
import type { ApplicationSettings } from '../bridge/types'
import { Sidebar, type PageKey } from '../components/Sidebar'
import { TopBar } from '../components/TopBar'
import { ToastHost } from '../components/ToastHost'
import { Modal } from '../components/Modal'
import { NotificationCenter } from '../components/NotificationCenter'
import { Dashboard } from '../pages/Dashboard'
import { Connection } from '../pages/Connection'
import { Statistics } from '../pages/Statistics'
import { Logs } from '../pages/Logs'
import { Settings } from '../pages/Settings'
import { About } from '../pages/About'
import { translations } from '../i18n'
import { useNetRelayState } from '../state/useNetRelayState'

export function App() {
  const state = useNetRelayState()
  const [page, setPage] = useState<PageKey>('dashboard')
  const [confirmDisconnect, setConfirmDisconnect] = useState(false)
  const [confirmClear, setConfirmClear] = useState(false)
  const [notificationsOpen, setNotificationsOpen] = useState(false)
  const [downloadHistory, setDownloadHistory] = useState<number[]>([0])
  const [uploadHistory, setUploadHistory] = useState<number[]>([0])
  const restored = useRef(false)
  const t = useMemo(() => translations(state.settings.language), [state.settings.language])
  const labels: Record<PageKey, string> = { dashboard: t.dashboard, connection: t.connection, statistics: t.statistics, logs: t.logs, settings: t.settings, about: t.about }

  useEffect(() => {
    document.documentElement.lang = state.settings.language
    document.documentElement.dir = state.settings.language === 'ar' ? 'rtl' : 'ltr'
    document.documentElement.dataset.theme = state.settings.theme
  }, [state.settings.language, state.settings.theme])

  useEffect(() => {
    if (restored.current || !state.initialStateReady) return
    restored.current = true
    if (state.settings.restoreLastPage) setPage(state.settings.lastPage)
  }, [state.initialStateReady, state.settings.lastPage, state.settings.restoreLastPage])

  useEffect(() => {
    setDownloadHistory((values) => [...values.slice(-59), state.metrics.downloadBps])
    setUploadHistory((values) => [...values.slice(-59), state.metrics.uploadBps])
  }, [state.metrics.downloadBps, state.metrics.uploadBps])

  const updateSettings = (changes: Partial<ApplicationSettings>) => { void state.command('updateSettings', changes) }
  const navigate = (next: PageKey) => { setPage(next); if (state.settings.restoreLastPage) updateSettings({ lastPage: next }) }
  const connect = (transport: 'usb' | 'wifiDirect') => state.command(transport === 'usb' ? 'connectUsb' : 'connectWifiDirect')
  const content = page === 'dashboard'
    ? <Dashboard connection={state.connection} metrics={state.metrics} downloadHistory={downloadHistory} uploadHistory={uploadHistory} t={t} busy={Boolean(state.busyCommand)} onConnect={connect} onDisconnect={() => setConfirmDisconnect(true)} />
    : page === 'connection'
      ? <Connection connection={state.connection} preferredTransport={state.settings.preferredTransport} t={t} busyCommand={state.busyCommand} androidAction={state.androidAction} run={state.command} onDisconnect={() => setConfirmDisconnect(true)} />
      : page === 'statistics'
        ? <Statistics metrics={state.metrics} downloadHistory={downloadHistory} uploadHistory={uploadHistory} t={t} />
        : page === 'logs'
          ? <Logs logs={state.logs} t={t} onClear={() => state.setLogs([])} />
          : page === 'settings'
            ? <Settings settings={state.settings} t={t} update={updateSettings} onClear={() => setConfirmClear(true)} />
            : <About info={state.connection.appInfo} t={t} />

  const lifecycleLabel = state.connection.uiState === 'connected' ? t.connected : state.connection.uiState === 'connecting' || state.connection.uiState === 'reconnecting' ? t.connecting : state.connection.uiState === 'error' ? t.error : t.disconnected
  return <div className="app-shell"><Sidebar page={page} labels={labels} version={state.connection.appVersion} connected={state.connection.isConnected} readyLabel={t.ready} connectedLabel={t.connected} onNavigate={navigate} /><main className="main-content"><TopBar title={labels[page]} state={state.connection.uiState} stateLabel={lifecycleLabel} count={state.notifications.unreadCount} notificationsLabel={t.notifications} onBell={() => setNotificationsOpen((value) => !value)} /><div className="page" key={page}>{content}</div></main><NotificationCenter open={notificationsOpen} snapshot={state.notifications} t={t} language={state.settings.language} onClose={() => setNotificationsOpen(false)} onRead={(id) => void state.command('markNotificationRead', { id })} onMarkAll={() => void state.command('markAllNotificationsRead')} onClear={() => void state.command('clearNotifications')} /><ToastHost notices={state.notices} /><Modal open={confirmDisconnect} title={t.disconnect} body={t.confirmDisconnect} cancelLabel={t.cancel} confirmLabel={t.confirm} danger onCancel={() => setConfirmDisconnect(false)} onConfirm={() => { setConfirmDisconnect(false); void state.command('disconnect') }} /><Modal open={confirmClear} title={t.clearLocalState} body={t.clearLocalStateConfirm} cancelLabel={t.cancel} confirmLabel={t.clear} danger onCancel={() => setConfirmClear(false)} onConfirm={() => { setConfirmClear(false); void state.command('clearUiState') }} /><Modal open={state.androidSignatureRepair !== null} title={t.androidSignatureMismatchTitle} body={t.androidSignatureMismatchBody} cancelLabel={t.cancel} confirmLabel={t.reinstallAndroid} danger onCancel={state.dismissAndroidSignatureRepair} onConfirm={() => { const serial = state.androidSignatureRepair?.serial; state.dismissAndroidSignatureRepair(); void state.command('reinstallAndroidApp', serial ? { serial } : {}) }} /></div>
}
