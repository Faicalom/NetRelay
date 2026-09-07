import type { TranslationTable } from '../i18n'
import { Icon } from './Icon'
export function WifiNetworkSelector({ networks, value, loading, disabled, t, onChange, onRefresh }: { networks: string[]; value: string; loading: boolean; disabled: boolean; t: TranslationTable; onChange: (value: string) => void; onRefresh: () => void }) {
  return <div className="network-selector-row"><label className="field-label"><span>{t.networkName}</span><select className="operation-input technical-value" value={value} disabled={disabled || loading} onChange={(event) => onChange(event.target.value)}><option value="">{loading ? t.searchingWifiNetworks : t.noWifiNetworks}</option>{networks.map((ssid) => <option key={ssid} value={ssid}>{ssid}</option>)}</select></label><button className="button button--ghost refresh-networks" disabled={disabled || loading} onClick={onRefresh}><Icon name="refresh" />{loading ? t.refreshing : t.refresh}</button></div>
}
