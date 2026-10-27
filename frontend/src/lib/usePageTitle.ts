import { useEffect } from 'react'

/** Gives each page its own document title, which screen readers announce and browser tabs show. */
export function usePageTitle(title: string) {
  useEffect(() => {
    document.title = `${title} · HarbourOps`
  }, [title])
}
