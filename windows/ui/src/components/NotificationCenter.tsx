import { formatTranslation, type TranslationTable } from '../i18n'
import type { AppNotification, NotificationSnapshot } from '../bridge/types'
import { Icon } from './Icon'

function timeLabel(value: string, language: string): string {
  const date = new Date(value)
  return Number.isNaN(date.valueOf()) ? value : new Intl.DateTimeFormat(language, { dateStyle: 'short', timeStyle: 'short' }).format(date)
}

function NotificationItem({ item, t, language, onRead }: { item: AppNotification; t: TranslationTable; language: string; onRead: () => void }) {
  const icon = item.severity === 'success' ? 'check' : item.severity === 'error' ? 'close' : item.severity === 'warning' ? 'shield' : 'bell'
  return <button className={`notification-item notification-item--${item.severity} ${item.isRead ? '' : 'notification-item--unread'}`} onClick={onRead}>
    <span className="notification-item__icon"><Icon name={icon} /></span>
    <span className="notification-item__body"><strong>{formatTranslation(t, item.titleKey)}</strong><span>{formatTranslation(t, item.messageKey, item.messageArgs)}</span><small>{timeLabel(item.timestamp, language)}</small></span>
    {!item.isRead && <i title={t.unread} />}
  </button>
}

export function NotificationCenter({ open, snapshot, t, language, onClose, onRead, onMarkAll, onClear }: {
  open: boolean
  snapshot: NotificationSnapshot
  t: TranslationTable
  language: string
  onClose: () => void
  onRead: (id: string) => void
  onMarkAll: () => void
  onClear: () => void
}) {
  if (!open) return null
  return <><button className="notification-scrim" aria-label={t.closeToTray} onClick={onClose} /><aside className="notification-center">
    <header><div><span className="eyebrow">NET RELAY</span><h2>{t.notifications}</h2></div><button className="micro-button" onClick={onClose}><Icon name="close" /></button></header>
    <div className="notification-actions"><button disabled={!snapshot.items.length} onClick={onMarkAll}>{t.markAllRead}</button><button disabled={!snapshot.items.length} onClick={onClear}>{t.clearAll}</button></div>
    <div className="notification-list">{snapshot.items.length ? snapshot.items.map((item) => <NotificationItem key={item.id} item={item} t={t} language={language} onRead={() => onRead(item.id)} />) : <div className="notification-empty"><Icon name="bell" /><span>{t.noNotifications}</span></div>}</div>
  </aside></>
}
