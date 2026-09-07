import type { TransportViewState } from '../bridge/types'
import { Icon } from './Icon'
import type { TranslationTable } from '../i18n'

export function TransportCard({ transport, selected = false, disabled, t, onClick }: {
  transport: TransportViewState; selected?: boolean; disabled: boolean; t: TranslationTable; onClick: () => void
}) {
  const icon = transport.key === 'usb' ? 'usb' : 'wifi'
  const stateLabel = transport.state === 'active' ? t.active : transport.state === 'connecting' ? t.connecting : transport.state === 'error' ? t.error : t.inactive
  return <button className={`transport-card transport-card--${transport.state} ${selected ? 'transport-card--selected' : ''}`} disabled={disabled || !transport.available} onClick={onClick} aria-pressed={selected}>
    <span className="transport-card__icon"><Icon name={icon} /></span>
    <span className="transport-card__copy"><strong>{transport.label}</strong><small>{transport.endpoint || t.ready}</small></span>
    <span className="transport-card__state"><i />{stateLabel}</span>
  </button>
}
