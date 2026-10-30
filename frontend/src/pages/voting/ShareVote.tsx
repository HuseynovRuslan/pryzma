import { useState } from 'react'
import { usePlatform } from '../../platform/context'
import { shareMessage } from './lib'

type Copy = 'idle' | 'done' | 'failed'

/**
 * The invitation a voter can pass on: the message itself, shown before it is sent, and the two ways to send it.
 *
 * The message says the person voted and where anyone can vote — never for which work: the ballot stays secret even
 * in the voter's own words. A phone gets its own share sheet (WhatsApp, Instagram, Telegram, whatever is installed);
 * a computer gets WhatsApp, which is where people here pass links on. Copying the link is always there.
 */
export function ShareVote({ slug, competitionTitle, lead }: { slug: string; competitionTitle: string; lead: string }) {
  const { platformName } = usePlatform()
  const [copy, setCopy] = useState<Copy>('idle')
  const url = `${window.location.origin}/competitions/${slug}/voting`
  const shown = url.replace(/^https?:\/\//, '')
  const text = shareMessage(platformName, competitionTitle)
  const canShare = typeof navigator.share === 'function'

  const share = async () => {
    try {
      await navigator.share({ title: platformName, text, url })
    } catch {
      // Closing the share sheet is a choice, not an error.
    }
  }

  const copyLink = async () => {
    try {
      await navigator.clipboard.writeText(url)
      setCopy('done')
    } catch {
      setCopy('failed')
    }
  }

  return (
    <div className="vshare">
      <p className="vshare__lead">{lead}</p>
      <blockquote className="vshare__message">
        <p>{text}</p>
        <p className="vshare__url">{shown}</p>
      </blockquote>
      <div className="vshare__actions">
        {canShare ? (
          <button type="button" className="btn btn--primary" onClick={share}>
            Paylaş
          </button>
        ) : (
          <a
            className="btn btn--primary"
            href={`https://wa.me/?text=${encodeURIComponent(`${text} ${url}`)}`}
            target="_blank"
            rel="noreferrer"
          >
            WhatsApp-da göndər
          </a>
        )}
        <button type="button" className="btn" onClick={copyLink}>
          {copy === 'done' ? 'Link kopyalandı' : 'Linki kopyala'}
        </button>
      </div>
      <p className="visually-hidden" aria-live="polite">
        {copy === 'done' ? 'Link kopyalandı' : ''}
      </p>
      {copy === 'failed' && (
        <p className="vshare__error" role="alert">
          Link kopyalanmadı. Mesajdakı linki seçib özünüz kopyalayın.
        </p>
      )}
    </div>
  )
}
