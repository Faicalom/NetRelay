import { Icon } from './Icon'
import { StatusPill } from './StatusPill'
import type { UiLifecycleState } from '../bridge/types'

export function TopBar({ title, state, stateLabel, count, notificationsLabel, onBell }: { title: string; state: UiLifecycleState; stateLabel: string; count: number; notificationsLabel: string; onBell: () => void }) {
  return <header className="topbar"><div><p>Net Relay</p><h1>{title}</h1></div><div className="topbar__actions"><StatusPill state={state} label={stateLabel} /><button className="icon-button bell" aria-label={notificationsLabel} title={notificationsLabel} onClick={onBell}><Icon name="bell" />{count > 0 && <b>{count}</b>}</button></div></header>
}
