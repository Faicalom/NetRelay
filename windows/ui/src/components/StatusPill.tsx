import type { UiLifecycleState } from '../bridge/types'

export function StatusPill({ state, label }: { state: UiLifecycleState; label: string }) {
  return <span className={`status-pill status-pill--${state}`}><i />{label}</span>
}
