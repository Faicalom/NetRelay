import { useEffect, useMemo, useRef, useState } from 'react'
import type { LogEntry, LogLevel } from '../bridge/types'
import type { TranslationKey } from '../i18n'
import { Icon } from '../components/Icon'
type T = Record<TranslationKey, string>
export function Logs({ logs, t, onClear }: { logs: LogEntry[]; t: T; onClear: () => void }) {
  const [query, setQuery] = useState(''); const [level, setLevel] = useState<'ALL' | LogLevel>('ALL'); const [auto, setAuto] = useState(true); const end = useRef<HTMLDivElement>(null)
  const filtered = useMemo(() => logs.filter((item) => (level === 'ALL' || item.level === level) && `${item.tag} ${item.message}`.toLowerCase().includes(query.toLowerCase())).slice(-300), [logs, query, level])
  useEffect(() => { if (auto) end.current?.scrollIntoView({ block: 'end' }) }, [auto, filtered])
  const copy = () => void navigator.clipboard.writeText(filtered.map((item) => `[${item.timestamp}] ${item.level} [${item.tag}] ${item.message}`).join('\n'))
  return <section className="panel logs-page"><div className="logs-toolbar"><label className="search-field"><Icon name="search" /><input value={query} onChange={(e) => setQuery(e.target.value)} placeholder={t.searchLogs} /></label><select value={level} onChange={(e) => setLevel(e.target.value as 'ALL' | LogLevel)}><option value="ALL">{t.allLevels}</option><option>DEBUG</option><option>INFO</option><option>WARN</option><option>ERROR</option></select><button className="button button--ghost" onClick={copy}><Icon name="copy" />{t.copy}</button><button className="button button--ghost" onClick={onClear}><Icon name="trash" />{t.clear}</button><label className="toggle"><input type="checkbox" checked={auto} onChange={(e) => setAuto(e.target.checked)} /><span />{t.autoScroll}</label></div><div className="log-table">{filtered.length ? filtered.map((item, index) => <div className="log-row" key={`${item.timestamp}-${index}`}><time>{item.timestamp}</time><b className={`level level--${item.level.toLowerCase()}`}>{item.level}</b><code>{item.tag}</code><p>{item.message}</p></div>) : <div className="empty-state">{t.noLogs}</div>}<div ref={end} /></div></section>
}
