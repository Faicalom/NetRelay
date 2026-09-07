import { useEffect, useState } from 'react'
import type { AndroidActionState, ApplicationSettings, BridgeCommand, ConnectionSnapshot } from '../bridge/types'
import type { TranslationKey } from '../i18n'
import { TransportSelector } from '../components/TransportSelector'
import { UsbDetailsPanel } from '../components/UsbDetailsPanel'
import { WifiDirectDetailsPanel } from '../components/WifiDirectDetailsPanel'

type T = Record<TranslationKey, string>
type TransportKey = 'usb' | 'wifiDirect'

function initialTransport(connection: ConnectionSnapshot, preferred: ApplicationSettings['preferredTransport']): TransportKey {
  if (connection.activeTransport === 'usb' || connection.activeTransport === 'wifiDirect') return connection.activeTransport
  if (connection.selectedTransport === 'usb' || connection.selectedTransport === 'wifiDirect') return connection.selectedTransport
  return preferred === 'wifiDirect' ? 'wifiDirect' : 'usb'
}

export function Connection({ connection, preferredTransport, t, busyCommand, androidAction, run, onDisconnect }: {
  connection: ConnectionSnapshot; t: T; busyCommand: BridgeCommand | null;
  androidAction: AndroidActionState;
  preferredTransport: ApplicationSettings['preferredTransport'];
  run: (command: BridgeCommand, payload?: Record<string, unknown>) => Promise<boolean>; onDisconnect: () => void
}) {
  const [selectedForView, setSelectedForView] = useState<TransportKey>(() => initialTransport(connection, preferredTransport))
  useEffect(() => {
    if (connection.activeTransport === 'usb' || connection.activeTransport === 'wifiDirect') setSelectedForView(connection.activeTransport)
  }, [connection.activeTransport])

  return <div className="connection-page">
    <section className="panel transport-selector-panel"><div className="section-heading"><div><span className="eyebrow">{t.selectTransport}</span><h2>{t.transport}</h2></div></div><p className="panel-hint">{t.selectTransportHint}</p><TransportSelector transports={connection.transports} selected={selectedForView} t={t} onSelect={setSelectedForView} /></section>
    {selectedForView === 'usb'
      ? <UsbDetailsPanel connection={connection} t={t} busyCommand={busyCommand} androidAction={androidAction} run={run} onDisconnect={onDisconnect} />
      : <WifiDirectDetailsPanel connection={connection} t={t} busyCommand={busyCommand} run={run} onDisconnect={onDisconnect} />}
  </div>
}
