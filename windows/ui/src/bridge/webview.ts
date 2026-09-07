import type { BridgeCommand, BridgeMessage, CommandRequest, CommandResult } from './types'

interface WebViewHost {
  postMessage(message: unknown): void
  addEventListener(type: 'message', handler: (event: MessageEvent) => void): void
  removeEventListener(type: 'message', handler: (event: MessageEvent) => void): void
}

declare global {
  interface Window {
    chrome?: { webview?: WebViewHost }
  }
}

const pending = new Map<string, {
  resolve: (result: CommandResult) => void
  reject: (error: Error) => void
  timeout: number
}>()

const listeners = new Set<(message: BridgeMessage) => void>()

function createId(): string {
  return globalThis.crypto?.randomUUID?.() ?? `ui-${Date.now()}-${Math.random().toString(16).slice(2)}`
}

function handleMessage(event: MessageEvent): void {
  const message = event.data as BridgeMessage
  if (!message || typeof message !== 'object') return

  if (message.type === 'commandResult') {
    const waiter = pending.get(message.id)
    if (waiter) {
      window.clearTimeout(waiter.timeout)
      pending.delete(message.id)
      waiter.resolve(message)
    }
  }

  listeners.forEach((listener) => listener(message))
}

if (window.chrome?.webview) {
  window.chrome.webview.addEventListener('message', handleMessage)
}

export const hasNativeBridge = (): boolean => Boolean(window.chrome?.webview)

export function subscribeBridge(listener: (message: BridgeMessage) => void): () => void {
  listeners.add(listener)
  return () => listeners.delete(listener)
}

export function sendCommand<T = unknown>(
  command: BridgeCommand,
  payload: Record<string, unknown> = {},
  timeoutMs = 45_000,
): Promise<CommandResult<T>> {
  const host = window.chrome?.webview
  if (!host) {
    return Promise.reject(new Error('Net Relay native bridge is unavailable.'))
  }

  const id = createId()
  const request: CommandRequest = { type: 'command', id, command, payload }
  return new Promise((resolve, reject) => {
    const timeout = window.setTimeout(() => {
      pending.delete(id)
      reject(new Error(`Bridge command timed out: ${command}`))
    }, timeoutMs)
    pending.set(id, { resolve: resolve as (value: CommandResult) => void, reject, timeout })
    host.postMessage(request)
  })
}
