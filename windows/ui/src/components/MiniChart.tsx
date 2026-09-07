export function MiniChart({ values, tone = 'cyan' }: { values: number[]; tone?: 'cyan' | 'mint' }) {
  const safe = values.length > 1 ? values.slice(-60) : [0, 0]
  const max = Math.max(1, ...safe)
  const points = safe.map((value, index) => `${(index / (safe.length - 1)) * 100},${38 - (value / max) * 32}`).join(' ')
  return <svg className={`mini-chart mini-chart--${tone}`} viewBox="0 0 100 42" preserveAspectRatio="none" role="img" aria-label="Network metric trend"><polyline points={points} /></svg>
}
