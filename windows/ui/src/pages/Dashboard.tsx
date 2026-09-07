import type { ConnectionSnapshot, NetworkMetrics } from '../bridge/types'
import type { TranslationKey } from '../i18n'
import { Icon } from '../components/Icon'
import { MetricCard } from '../components/MetricCard'
import { TransportCard } from '../components/TransportCard'

type T = Record<TranslationKey, string>

export function Dashboard({ connection, metrics, downloadHistory, uploadHistory, t, busy, onConnect, onDisconnect }: {
  connection: ConnectionSnapshot; metrics: NetworkMetrics; downloadHistory: number[]; uploadHistory: number[]; t: T; busy: boolean;
  onConnect: (transport: 'usb' | 'wifiDirect') => void; onDisconnect: () => void
}) {
  const transport = connection.transports.find((item) => item.key === connection.activeTransport)
  return <div className="dashboard-grid">
    <section className={`hero-card hero-card--${connection.uiState}`}>
      <div className="hero-orbit"><span><Icon name={connection.isConnected ? 'check' : connection.isConnecting ? 'spinner' : 'power'} /></span></div>
      <div className="hero-copy"><span className="eyebrow">{t.connectionStatus}</span><h2>{connection.isConnected ? t.connected : connection.isConnecting ? t.connecting : t.disconnected}</h2><p>{connection.statusMessage || t.noTransport}</p>
        <div className="hero-facts"><span><b>{t.transport}</b>{transport?.label || t.inactive}</span><span><b>{t.phone}</b>{connection.phone.name}</span><span><b>{t.internet}</b>{connection.internet.status}</span></div>
        {connection.isConnected && <button className="button button--danger" disabled={busy} onClick={onDisconnect}>{t.disconnect}</button>}
      </div>
    </section>

    <section className="panel transport-panel"><div className="section-heading"><div><span className="eyebrow">{t.transportSection}</span><h3>{t.transport}</h3></div></div><div className="transport-list">{connection.transports.map((item) => <TransportCard key={item.key} transport={item} t={t} disabled={busy || connection.isConnected} onClick={() => onConnect(item.key)} />)}</div></section>

    <section className={`panel flow-panel ${connection.isConnected ? 'flow-panel--active' : ''}`}><div className="section-heading"><div><span className="eyebrow">{t.dataPlane}</span><h3>{t.networkPath}</h3></div></div><div className="connection-flow"><div><Icon name="phone" /><span>{t.phone}</span></div><i /><div className="relay-node"><img src="/netrelay-icon.png" alt="" draggable={false} /><span>{t.relay}</span></div><i /><div><Icon name="computer" /><span>{t.computer}</span></div></div></section>

    <section className="metrics-grid">
      <MetricCard icon="download" label={t.download} value={metrics.download} values={downloadHistory} />
      <MetricCard icon="upload" label={t.upload} value={metrics.upload} values={uploadHistory} tone="mint" />
      <MetricCard icon="clock" label={t.duration} value={metrics.duration} footnote={connection.activeTransport || t.noTransport} />
      <MetricCard icon="latency" label={t.gatewayLatency} value={metrics.ping} footnote={t.metricsUnavailable} />
    </section>

    <section className="panel details-strip"><div><Icon name="phone" /><span><small>{t.target}</small><strong>{connection.endpoints.target || '—'}</strong></span></div><div><Icon name="gateway" /><span><small>{t.gateway}</small><strong>{connection.endpoints.gateway || '—'}</strong></span></div><div><Icon name="internet" /><span><small>{t.internet}</small><strong>{connection.internet.available ? t.connected : t.disconnected}</strong></span></div></section>
  </div>
}
