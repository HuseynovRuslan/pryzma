import { useState } from 'react'
import { ApiError } from '../../api/client'
import { formatDateTime } from '../../lib/format'
import { useApi } from '../../lib/useApi'
import { adminApi } from '../api'
import { ConfirmDialog } from '../ConfirmDialog'
import { Badge, Notice, Stat } from '../ui'

/**
 * D14, "Müvəqqəti e-poçt qaydası" (owner, 7 October 2026): what the frozen rule meets in this competition — per work,
 * per domain, and vote by vote with a masked address (never the address, a phone or a connection) — and the one action
 * it allows: setting every eligible vote aside at once, the same for every work, with the count the admin sees
 * confirmed by the server. Votes are never deleted; each has its own audit entry, and the decision one more.
 */
export function DisposableEmailPanel({ competitionId, onChanged }: { competitionId: number; onChanged: () => void }) {
  const [version, setVersion] = useState(0)
  const state = useApi((signal) => adminApi.disposableEmailRule(competitionId, signal), [competitionId, version])
  const [dialog, setDialog] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const [shown, setShown] = useState(false)
  const r = state.data
  if (!r) return null

  const apply = async () => {
    setBusy(true)
    setError(undefined)
    try {
      await adminApi.applyDisposableEmailRule(competitionId, r.eligibleVotes, r.reason)
      setDialog(false)
      setVersion((n) => n + 1)
      onChanged()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Qayda tətbiq olunmadı.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="a-panel" aria-labelledby="d14-title">
      <header className="a-panel__head">
        <h3 id="d14-title" className="a-panel__title">
          Müvəqqəti e-poçt qaydası <span className="a-muted">· D14</span>
        </h3>
        <Badge tone={r.enabled ? 'good' : 'archived'}>{r.enabled ? 'Aktiv' : 'Aktiv deyil'}</Badge>
      </header>
      <p className="a-muted">
        {r.reason} Qayda: <code>{r.policyId}</code> v{r.version}, sübut növü <code>{r.evidenceType}</code>,{' '}
        <span className="tabular">{r.domainCount}</span> təsdiqlənmiş domen (sabit siyahı). Səsvermə müddəti:{' '}
        <span className="tabular">
          {formatDateTime(r.votingStartsAt)} — {formatDateTime(r.votingEndsAt)}
        </span>
        .
      </p>

      <div className="a-stats">
        <Stat value={r.eligibleVotes} label="qaydaya uyğun etibarlı səs" />
        <Stat value={r.setAsideUnderRule} label="bu qayda ilə etibarsız sayılıb" />
        <Stat value={r.setAsideOtherwise} label="başqa qayda ilə əvvəlcədən" />
        <Stat value={r.openedBeforeVoting} label="səsvermədən əvvəl açılmış (uyğun deyil)" />
      </div>

      {r.works.length > 0 && (
        <table className="a-table">
          <caption className="a-muted">İşlər üzrə</caption>
          <thead>
            <tr>
              <th scope="col">İş</th>
              <th scope="col">Etibarlı indi</th>
              <th scope="col">Qaydaya uyğun</th>
              <th scope="col">Sonra</th>
              <th scope="col">Bu qayda ilə etibarsız</th>
            </tr>
          </thead>
          <tbody>
            {r.works.map((w) => (
              <tr key={w.submissionId}>
                <td className="tabular">{w.reference}</td>
                <td className="tabular">{w.validNow}</td>
                <td className="tabular">{w.eligible}</td>
                <td className="tabular">{w.validAfter}</td>
                <td className="tabular">{w.setAsideUnderRule}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <details>
        <summary>
          Domenlər (<span className="tabular">{r.domainCount}</span>) və sübut
        </summary>
        <table className="a-table">
          <thead>
            <tr>
              <th scope="col">Domen</th>
              <th scope="col">Xidmət</th>
              <th scope="col">Səs</th>
              <th scope="col">Etibarlı</th>
              <th scope="col">D14 ilə etibarsız</th>
            </tr>
          </thead>
          <tbody>
            {r.domains.map((d) => (
              <tr key={d.domain}>
                <td>{d.domain}</td>
                <td className="a-muted">{d.provider}</td>
                <td className="tabular">{d.votes}</td>
                <td className="tabular">{d.eligible}</td>
                <td className="tabular">{d.setAsideUnderRule}</td>
              </tr>
            ))}
          </tbody>
        </table>
        <p className="a-muted">{r.source}</p>
        <button type="button" className="btn btn--sm btn--ghost" onClick={() => setShown((x) => !x)} aria-expanded={shown}>
          {shown ? 'Səsləri gizlət' : `Səsləri göstər (${r.evidence.length})`}
        </button>
        {shown && (
          <table className="a-table">
            <thead>
              <tr>
                <th scope="col">Səs</th>
                <th scope="col">İş</th>
                <th scope="col">E-poçt (maskalı)</th>
                <th scope="col">Hesab açılıb</th>
                <th scope="col">Səs verilib</th>
                <th scope="col">Vəziyyət</th>
              </tr>
            </thead>
            <tbody>
              {r.evidence.map((e) => (
                <tr key={e.voteId}>
                  <td className="tabular">#{e.voteId}</td>
                  <td className="tabular">{e.reference}</td>
                  <td>{e.maskedEmail}</td>
                  <td className="tabular">{formatDateTime(e.accountOpenedAt)}</td>
                  <td className="tabular">{formatDateTime(e.votedAt)}</td>
                  <td>{e.valid ? 'etibarlı' : e.setAsideUnderRule ? 'D14 ilə etibarsız' : 'başqa qayda ilə etibarsız'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </details>

      {r.enabled && r.eligibleVotes > 0 && (
        <div className="a-actions">
          <button type="button" className="btn btn--primary" disabled={!r.votingClosed} onClick={() => setDialog(true)}>
            Qaydanı tətbiq et — {r.eligibleVotes} səs
          </button>
          {!r.votingClosed && <span className="a-muted">Səsvermə bağlandıqdan sonra.</span>}
        </div>
      )}
      {error && !dialog && <Notice tone="error">{error}</Notice>}
      {dialog && (
        <ConfirmDialog
          title={`${r.eligibleVotes} səs etibarsız sayılsın?`}
          confirmLabel={`Bəli, ${r.eligibleVotes} səsi etibarsız say`}
          busy={busy}
          error={error}
          onCancel={() => setDialog(false)}
          onConfirm={apply}
        >
          <p>
            Qaydaya uyğun bütün etibarlı səslər bir qərarla etibarsız sayılacaq —{' '}
            {r.works.filter((w) => w.eligible > 0).map((w, i) => (
              <span key={w.submissionId} className="tabular">
                {i > 0 && ', '}
                {w.reference}: {w.eligible}
              </span>
            ))}
            . Səslər silinmir; hər səs üçün audit qeydi yazılır və bərpa etmək mümkündür.
          </p>
          <p className="a-muted">Server sayı yenidən hesablayır: say fərqlidirsə, heç nə dəyişmir.</p>
        </ConfirmDialog>
      )}
    </section>
  )
}
