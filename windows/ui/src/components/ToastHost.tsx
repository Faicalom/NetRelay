import type { ToastNotice } from '../state/useNetRelayState'
import { Icon } from './Icon'

export function ToastHost({ notices }: { notices: ToastNotice[] }) {
  return <div className="toast-host" aria-live="polite">{notices.map((notice) => <div key={notice.id} className={`toast toast--${notice.tone}`}><Icon name={notice.tone === 'success' ? 'check' : notice.tone === 'error' ? 'close' : 'bell'} /><span>{notice.message}</span></div>)}</div>
}
