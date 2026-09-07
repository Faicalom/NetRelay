import { useState } from 'react'
import type { TranslationTable } from '../i18n'
import { Icon } from './Icon'
export function WifiPasswordField({ value, disabled, t, onChange }: { value: string; disabled: boolean; t: TranslationTable; onChange: (value: string) => void }) {
  const [visible, setVisible] = useState(false)
  return <label className="field-label"><span>{t.password}</span><div className="password-field"><input className="operation-input technical-value" dir="ltr" type={visible ? 'text' : 'password'} autoComplete="off" value={value} disabled={disabled} onChange={(event) => onChange(event.target.value)} /><button type="button" className="micro-button" disabled={!value || disabled} title={visible ? t.hide : t.show} onClick={() => setVisible((current) => !current)}><Icon name={visible ? 'eyeOff' : 'eye'} /></button><button type="button" className="micro-button" disabled={!value || disabled} title={t.copy} onClick={() => value && void navigator.clipboard.writeText(value)}><Icon name="copy" /></button></div></label>
}
