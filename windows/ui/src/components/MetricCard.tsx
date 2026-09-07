import { Icon, type IconName } from './Icon'
import { MiniChart } from './MiniChart'

export function MetricCard({ icon, label, value, footnote, values, tone = 'cyan' }: {
  icon: IconName; label: string; value: string; footnote?: string; values?: number[]; tone?: 'cyan' | 'mint'
}) {
  return <article className="metric-card">
    <div className="metric-card__heading"><span className="icon-chip"><Icon name={icon} /></span><span>{label}</span></div>
    <strong>{value}</strong>
    {values && <MiniChart values={values} tone={tone} />}
    {footnote && <small>{footnote}</small>}
  </article>
}
