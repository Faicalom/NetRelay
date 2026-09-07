import type { ReactNode } from 'react'
import { CopyValue } from './CopyValue'

export function DetailRow({ label, value, unavailableLabel = '—', showLabel, hideLabel, copyLabel, technical = false, secret = false }: { label: string; value?: string; unavailableLabel?: string; showLabel?: string; hideLabel?: string; copyLabel?: string; technical?: boolean; secret?: boolean }) {
  return <div className="detail-row"><dt>{label}</dt><dd>{technical || secret ? <CopyValue value={value ?? ''} secret={secret} unavailable={unavailableLabel} showLabel={showLabel} hideLabel={hideLabel} copyLabel={copyLabel} /> : <span className={value ? '' : 'detail-value--unavailable'}>{value || unavailableLabel}</span>}</dd></div>
}

export function DetailGroup({ title, children }: { title: string; children: ReactNode }) {
  return <section className="detail-group"><h3>{title}</h3><dl>{children}</dl></section>
}
