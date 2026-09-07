import type { BridgeCommand, ConnectionSnapshot } from '../bridge/types'
import type { TranslationTable } from '../i18n'
import { Icon } from './Icon'
type Run = (command: BridgeCommand, payload?: Record<string, unknown>) => Promise<boolean>
export function WifiActions({ connection, t, ssid, passphrase, endpoint, busyCommand, run, onDisconnect }: { connection: ConnectionSnapshot; t: TranslationTable; ssid: string; passphrase: string; endpoint: string; busyCommand: BridgeCommand | null; run: Run; onDisconnect: () => void }) {
  const active = connection.activeTransport === 'wifiDirect' && connection.isConnected
  const busy = busyCommand !== null || connection.wifiDirect.isSearching
  const payload = { ssid, passphrase, endpoint: endpoint.trim() }
  return <div className="action-grid action-grid--wifi"><button className="button button--ghost" disabled={busy || active} onClick={() => void run('refreshWifiDirect')}><Icon name="search" />{busyCommand === 'refreshWifiDirect' ? t.searching : t.search}</button><button className="button button--ghost" disabled={busy || active || !ssid} onClick={() => void run('connectWifiNetworkOnly', payload)}>{busyCommand === 'connectWifiNetworkOnly' ? t.networkConnecting : t.connectNetworkOnly}</button><button className="button button--primary" disabled={busy || active || (connection.isConnected && !active) || !ssid} onClick={() => void run('connectWifiDirect', payload)}>{busyCommand === 'connectWifiDirect' ? t.fullConnecting : t.connectThroughPhone}</button><button className="button button--danger" disabled={busy || !active} onClick={onDisconnect}>{busyCommand === 'disconnect' ? t.disconnecting : t.disconnect}</button></div>
}
