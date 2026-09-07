import { Icon, type IconName } from './Icon'

export type PageKey = 'dashboard' | 'connection' | 'statistics' | 'logs' | 'settings' | 'about'

const items: { key: PageKey; icon: IconName }[] = [
  { key: 'dashboard', icon: 'dashboard' }, { key: 'connection', icon: 'connection' },
  { key: 'statistics', icon: 'statistics' }, { key: 'logs', icon: 'logs' },
  { key: 'settings', icon: 'settings' }, { key: 'about', icon: 'help' },
]

export function Sidebar({ page, labels, version, connected, readyLabel, connectedLabel, onNavigate }: {
  page: PageKey; labels: Record<PageKey, string>; version: string; connected: boolean; readyLabel: string; connectedLabel: string; onNavigate: (page: PageKey) => void
}) {
  return <aside className="sidebar">
    <div className="brand"><img src="/netrelay-icon.png" alt="" draggable={false} /><span><strong>Net Relay</strong><small>for Windows</small></span></div>
    <nav aria-label="Main navigation">{items.map((item) => <button key={item.key} aria-label={labels[item.key]} title={labels[item.key]} className={page === item.key ? 'active' : ''} onClick={() => onNavigate(item.key)}><Icon name={item.icon} /><span>{labels[item.key]}</span></button>)}</nav>
    <div className="sidebar__footer"><div className={`compact-state ${connected ? 'connected' : ''}`}><i /><span>{connected ? connectedLabel : readyLabel}</span></div><small>Net Relay v{version}</small></div>
  </aside>
}
