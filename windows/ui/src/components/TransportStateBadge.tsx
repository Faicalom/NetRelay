import type { TransportActivity } from '../bridge/types'
import type { TranslationTable } from '../i18n'

export function TransportStateBadge({ state, t }: { state: TransportActivity; t: TranslationTable }) {
  const label = state === 'active' ? t.active : state === 'connecting' ? t.connecting : state === 'error' ? t.error : t.inactive
  return <span className={`transport-badge transport-badge--${state}`}><i />{label}</span>
}
