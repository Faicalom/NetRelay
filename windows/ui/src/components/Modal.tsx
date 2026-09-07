export function Modal({ open, title, body, cancelLabel, confirmLabel, danger, onCancel, onConfirm }: {
  open: boolean; title: string; body: string; cancelLabel: string; confirmLabel: string; danger?: boolean; onCancel: () => void; onConfirm: () => void
}) {
  if (!open) return null
  return <div className="modal-backdrop" role="presentation" onMouseDown={onCancel}><section className="modal" role="dialog" aria-modal="true" aria-labelledby="modal-title" onMouseDown={(event) => event.stopPropagation()}><h2 id="modal-title">{title}</h2><p>{body}</p><div><button className="button button--ghost" onClick={onCancel}>{cancelLabel}</button><button className={`button ${danger ? 'button--danger' : 'button--primary'}`} onClick={onConfirm}>{confirmLabel}</button></div></section></div>
}
