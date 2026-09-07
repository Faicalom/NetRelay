import type { TransportViewState } from '../bridge/types'
import { TransportCard } from './TransportCard'
import type { TranslationTable } from '../i18n'

export function TransportSelector({ transports, selected, t, onSelect }: { transports: TransportViewState[]; selected: 'usb' | 'wifiDirect'; t: TranslationTable; onSelect: (transport: 'usb' | 'wifiDirect') => void }) {
  return <div className="transport-list">{transports.map((transport) => <TransportCard key={transport.key} transport={transport} selected={selected === transport.key} disabled={false} t={t} onClick={() => onSelect(transport.key)} />)}</div>
}
