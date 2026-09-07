import type { ApplicationSettings } from '../bridge/types'
import type { TranslationTable } from '../i18n'
import { Icon } from '../components/Icon'

type Update = (changes: Partial<ApplicationSettings>) => void

function Toggle({ checked, title, hint, disabled, onChange }: { checked: boolean; title: string; hint: string; disabled?: boolean; onChange: (value: boolean) => void }) {
  return <label className={`setting-row ${disabled ? 'setting-row--disabled' : ''}`}><span><strong>{title}</strong><small>{hint}</small></span><input type="checkbox" checked={checked} disabled={disabled} onChange={(event) => onChange(event.target.checked)} /><i /></label>
}

export function Settings({ settings, t, update, onClear }: { settings: ApplicationSettings; t: TranslationTable; update: Update; onClear: () => void }) {
  const themes = [{ key: 'midnight' as const, title: t.midnight, hint: t.midnightHint }, { key: 'oled' as const, title: t.oled, hint: t.oledHint }, { key: 'light' as const, title: t.light, hint: t.lightHint }]
  const languages = [{ key: 'en' as const, title: t.english }, { key: 'ar' as const, title: t.arabic }, { key: 'fr' as const, title: t.french }, { key: 'ru' as const, title: t.russian }]
  return <div className="settings-page">
    <section className="panel settings-section"><div className="settings-heading"><span className="icon-chip"><Icon name="settings" /></span><div><h2>{t.general}</h2><p>{t.connectionDetails}</p></div></div>
      <Toggle checked={settings.startWithWindows} title={t.startWithWindows} hint={t.startWithWindowsHint} onChange={(value) => update({ startWithWindows: value })} />
      <Toggle checked={settings.startMinimized} disabled={!settings.startWithWindows} title={t.startMinimized} hint={t.startMinimizedHint} onChange={(value) => update({ startMinimized: value })} />
      <Toggle checked={settings.closeToTray} title={t.closeToTray} hint={t.closeToTrayHint} onChange={(value) => update({ closeToTray: value })} />
      <Toggle checked={settings.restoreLastPage} title={t.restoreLastPage} hint={t.restoreLastPageHint} onChange={(value) => update({ restoreLastPage: value })} />
    </section>
    <section className="panel settings-section"><div className="settings-heading"><span className="icon-chip"><Icon name="language" /></span><div><h2>{t.appearance}</h2><p>{t.midnight} / {t.oled} / {t.light}</p></div></div>
      <div className="choice-grid choice-grid--themes">{themes.map((item) => <button key={item.key} className={`choice-card theme-preview theme-preview--${item.key} ${settings.theme === item.key ? 'active' : ''}`} onClick={() => update({ theme: item.key })}><i /><strong>{item.title}</strong><small>{item.hint}</small></button>)}</div>
      <h3 className="settings-subtitle">{t.language}</h3><div className="choice-grid">{languages.map((item) => <button key={item.key} lang={item.key} dir={item.key === 'ar' ? 'rtl' : 'ltr'} className={`choice-card ${settings.language === item.key ? 'active' : ''}`} onClick={() => update({ language: item.key })}><strong>{item.title}</strong>{settings.language === item.key && <Icon name="check" />}</button>)}</div>
    </section>
    <section className="panel settings-section settings-section--wide"><div className="settings-heading"><span className="icon-chip"><Icon name="tools" /></span><div><h2>{t.advanced}</h2><p>{t.loggingHint}</p></div></div>
      <Toggle checked={settings.autoReconnect} title={t.autoReconnect} hint={t.autoReconnectHint} onChange={(value) => update({ autoReconnect: value })} />
      <div className="settings-fields"><label><span>{t.preferredTransport}</span><small>{t.preferredTransportHint}</small><select value={settings.preferredTransport} onChange={(event) => update({ preferredTransport: event.target.value as ApplicationSettings['preferredTransport'] })}><option value="none">{t.preferredNone}</option><option value="usb">{t.preferredUsb}</option><option value="wifiDirect">{t.preferredWifi}</option></select></label>
        <label><span>{t.loggingLevel}</span><small>{t.loggingHint}</small><select value={settings.loggingLevel} onChange={(event) => update({ loggingLevel: event.target.value as ApplicationSettings['loggingLevel'] })}><option value="normal">{t.normal}</option><option value="detailed">{t.detailed}</option></select></label>
        <label><span>{t.maxUiLogs}</span><small>{settings.maxUiLogs}</small><select value={settings.maxUiLogs} onChange={(event) => update({ maxUiLogs: Number(event.target.value) as ApplicationSettings['maxUiLogs'] })}><option value="200">200</option><option value="500">500</option><option value="1000">1000</option></select></label></div>
      <div className="danger-setting"><span><strong>{t.clearLocalState}</strong><small>{t.clearLocalStateHint}</small></span><button className="button button--danger" onClick={onClear}><Icon name="trash" />{t.clear}</button></div>
    </section>
  </div>
}
