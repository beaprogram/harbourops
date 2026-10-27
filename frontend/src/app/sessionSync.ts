import type { QueryClient } from '@tanstack/react-query'
import { refreshAntiforgeryToken, SESSION_CHANGED_EVENT } from '../api/client'

const CHANNEL = 'harbourops-session'

/**
 * Tells this browser's other tabs that the signed-in person changed (sign-in, sign-out, or a demo persona
 * switch, reset, or end). The session cookie is shared by every tab, so they are now acting as that person too.
 */
export function announceSessionChange(): void {
  if (typeof BroadcastChannel === 'undefined') return
  const channel = new BroadcastChannel(CHANNEL)
  channel.postMessage('changed')
  channel.close()
}

/**
 * Keeps a tab in step when the person changes elsewhere: drop the old antiforgery token and ask the server
 * again who is signed in and what they can see. Started once by main.tsx.
 */
export function startSessionSync(queryClient: QueryClient): () => void {
  const refresh = () => {
    void refreshAntiforgeryToken().catch(() => undefined)
    void queryClient.invalidateQueries()
  }
  window.addEventListener(SESSION_CHANGED_EVENT, refresh)
  const channel = typeof BroadcastChannel === 'undefined' ? null : new BroadcastChannel(CHANNEL)
  channel?.addEventListener('message', refresh)
  return () => {
    window.removeEventListener(SESSION_CHANGED_EVENT, refresh)
    channel?.close()
  }
}
