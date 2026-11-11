type PendingRequest = {
  resolve: (value: unknown) => void
  reject: (reason: Error) => void
}

type BridgeResponse<T = unknown> = {
  id: string
  ok: boolean
  result?: T
  error?: string
}

type NativeEvent = {
  type: 'event'
  action: string
  at: string
}

type WebViewClient = {
  postMessage: (message: unknown) => void
  addEventListener: (
    type: 'message',
    listener: (event: { data: unknown }) => void,
  ) => void
}

declare global {
  interface Window {
    chrome?: {
      webview?: WebViewClient
    }
  }
}

const pending = new Map<string, PendingRequest>()
const listeners = new Set<(event: NativeEvent) => void>()
let sequence = 0
let installed = false

function installListener() {
  if (installed || !window.chrome?.webview) {
    return
  }

  installed = true
  window.chrome.webview.addEventListener('message', (event) => {
    const data = event.data as Partial<BridgeResponse> | NativeEvent
    if ('type' in data && data.type === 'event') {
      listeners.forEach((listener) => listener(data as NativeEvent))
      return
    }

    if (!('id' in data) || typeof data.id !== 'string') {
      return
    }

    const request = pending.get(data.id)
    if (!request) {
      return
    }

    pending.delete(data.id)
    if (data.ok) {
      request.resolve(data.result)
    } else {
      request.reject(new Error(data.error || 'LocalDeck bridge error'))
    }
  })
}

export async function invoke<T>(
  method: string,
  params: Record<string, unknown> = {},
): Promise<T> {
  installListener()
  if (!window.chrome?.webview) {
    throw new Error('当前页面不在 LocalDeck 桌面壳内运行')
  }

  const id = `${Date.now()}-${sequence++}`
  return await new Promise<T>((resolve, reject) => {
    pending.set(id, {
      resolve: (value) => resolve(value as T),
      reject,
    })
    window.chrome!.webview!.postMessage({ id, method, params })
  })
}

export function onNativeEvent(listener: (event: NativeEvent) => void): () => void {
  installListener()
  listeners.add(listener)
  return () => listeners.delete(listener)
}
