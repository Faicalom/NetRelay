import { en } from './en'
import { ar } from './ar'
import { fr } from './fr'
import { ru } from './ru'
import type { Language } from '../bridge/types'

export type { Language }
export type TranslationKey = keyof typeof en
export type TranslationTable = Record<TranslationKey, string>

const tables: Record<Language, TranslationTable> = { en, ar, fr, ru }

export function translations(language: Language): TranslationTable {
  return tables[language] ?? en
}

export function formatTranslation(table: TranslationTable, key: string, args: Record<string, string> = {}): string {
  const template = table[key as TranslationKey] ?? key
  return Object.entries(args).reduce((value, [name, replacement]) => value.replaceAll(`{${name}}`, replacement), template)
}
