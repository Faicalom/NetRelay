import { useState } from 'react'
import { Icon } from './Icon'

export function CopyValue({ value, secret = false, unavailable = 'Unavailable', showLabel = 'Show', hideLabel = 'Hide', copyLabel = 'Copy' }: { value: string; secret?: boolean; unavailable?: string; showLabel?: string; hideLabel?: string; copyLabel?: string }) {
  const [revealed, setRevealed] = useState(false)
  if (!value) return <span className="detail-value detail-value--unavailable">{unavailable}</span>
  const displayed = secret && !revealed ? '••••••••••' : value
  return <span className="copy-value">
    <b className="selectable technical-value" dir="ltr">{displayed}</b>
    {secret && <button type="button" className="micro-button" onClick={() => setRevealed((current) => !current)} title={revealed ? hideLabel : showLabel} aria-label={revealed ? hideLabel : showLabel}><Icon name={revealed ? 'close' : 'search'} /></button>}
    <button type="button" className="micro-button" onClick={() => void navigator.clipboard.writeText(value)} title={copyLabel} aria-label={copyLabel}><Icon name="copy" /></button>
  </span>
}
