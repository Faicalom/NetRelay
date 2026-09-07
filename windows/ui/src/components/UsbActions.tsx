import { useEffect, useState } from 'react'
import type { AndroidActionState, BridgeCommand, ConnectionSnapshot } from '../bridge/types'
import type { TranslationTable } from '../i18n'
import { Icon } from './Icon'
type Run = (command: BridgeCommand, payload?: Record<string, unknown>) => Promise<boolean>
export function UsbActions({ connection, t, busyCommand, androidAction, run, onDisconnect }: { connection: ConnectionSnapshot; t: TranslationTable; busyCommand: BridgeCommand | null; androidAction: AndroidActionState; run: Run; onDisconnect: () => void }) {
  const [endpoint, setEndpoint] = useState(connection.usb.controlEndpoint)
  useEffect(() => setEndpoint(connection.usb.controlEndpoint), [connection.usb.controlEndpoint])
  const active = connection.activeTransport === 'usb' && connection.isConnected
  const busy = busyCommand !== null
  const payload = { endpoint: endpoint.trim() }
  const installLabel = androidAction === 'checking' ? t.checkingPhone : androidAction === 'installing' ? t.installingAndroid : androidAction === 'updating' ? t.updatingAndroid : androidAction === 'opening' ? t.openingAndroid : t.installStart
  return <section className="operation-section"><div className="operation-heading"><div><span className="eyebrow">{t.usbWorkflow}</span><h3>{t.usbWorkflowTitle}</h3></div><span className="operation-note">{t.legacyCommands}</span></div><label className="field-label"><span>{t.controlAddress}</span><input className="operation-input technical-value" dir="ltr" value={endpoint} onChange={(event) => setEndpoint(event.target.value)} disabled={busy || active} /></label><div className="action-grid action-grid--usb"><button className="button button--ghost" disabled={busy || active} onClick={() => void run('refreshUsb')}><Icon name="refresh" />{busyCommand === 'refreshUsb' ? t.refreshing : t.refreshUsb}</button><button className="button button--ghost" disabled={busy || active} onClick={() => void run('prepareUsbPhone', payload)}>{busyCommand === 'prepareUsbPhone' ? installLabel : t.installStart}</button><button className="button button--primary" disabled={busy || active || (connection.isConnected && !active)} onClick={() => void run('connectUsb', payload)}>{busyCommand === 'connectUsb' ? t.connecting : t.connectNow}</button><button className="button button--danger" disabled={busy || !active} onClick={onDisconnect}>{busyCommand === 'disconnect' ? t.disconnecting : t.disconnect}</button></div></section>
}
