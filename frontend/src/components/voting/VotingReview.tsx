import type { ReactNode } from 'react'
import type { VotingAudit } from '../../api/types'
import { formatDateTime } from '../../lib/format'
import { auditLine, auditRules } from '../../pages/voting/lib'
import './review.css'

/**
 * After the vote and before the official result (the owner's decision of 7 October 2026): the provisional stage.
 * The ranking stays on the page — this sits above it and says what it is: provisional, on valid votes, while the votes
 * are checked. It promises nothing of the jury: its evaluation is internal and decides no public place (D16, 8 October
 * 2026). The three sentences are the owner's own wording (9 October 2026). The dot pulses softly, and not at all for
 * a reader who asked for less motion.
 *
 * The totals are the server's own (VotingAudit), counted from the votes themselves; while they are on their way the
 * counters are simply not drawn. The page around it decides when it is shown (`afterVote` in pages/voting/lib).
 */
export function ReviewStatus({
  audit,
  headingLevel = 'h2',
  children,
}: {
  audit: VotingAudit | null
  headingLevel?: 'h1' | 'h2'
  /** Ways onward — to the ranking, the works. */
  children?: ReactNode
}) {
  const Heading = headingLevel
  return (
    <section className="vreview" aria-labelledby="vreview-title">
      <div className="vreview__grid">
        <div className="vreview__main">
          <p className="vreview__eyebrow">İlkin nəticələr</p>
          <Heading id="vreview-title" className="vreview__title">
            <span className="vreview__pulse" aria-hidden="true" />
            Səslər yoxlanılır
          </Heading>
          <p className="vreview__lede">Səsvermə başa çatıb.</p>
          <p className="vreview__second">
            <span className="vreview__mark" aria-hidden="true" />
            Göstərilən sıralama etibarlı səslərə əsaslanan ilkin nəticədir.
          </p>
          <p className="vreview__note">Yekun nəticələr ayrıca elan ediləcək.</p>
          {children && <div className="vreview__actions">{children}</div>}
        </div>

        {audit && (
          <dl className="vreview__totals" aria-label="Səs sayları">
            <div className="vreview__total vreview__total--valid">
              <dt>Etibarlı səslər</dt>
              <dd className="tabular">{audit.validVotes}</dd>
            </div>
            <div className="vreview__total">
              <dt>Etibarsız sayılmış səslər</dt>
              <dd className="tabular">{audit.invalidatedVotes}</dd>
            </div>
            <div className="vreview__total">
              <dt>Ümumi səslər</dt>
              <dd className="tabular">{audit.totalVotes}</dd>
            </div>
          </dl>
        )}
      </div>
    </section>
  )
}

/** The votes set aside under the disposable-e-mail rule (D14), from the server's own per-rule totals. */
const disposableVotes = (audit: VotingAudit | null) => audit?.rules?.find((r) => r.reason === 'DisposableEmail')?.votes ?? 0

/**
 * "Səsvermə auditi yeniləndi" (owner, 7 October 2026): the ranking changed after the vote closed, and the page says why
 * — what was technically detected, never who did it. Shown once votes have been set aside under the disposable-e-mail
 * rule; every number is the server's (VotingAudit), counted from the votes themselves.
 */
export function AuditNotice({ audit }: { audit: VotingAudit | null }) {
  const n = disposableVotes(audit)
  if (!audit || n === 0) return null
  return (
    <section className="vnotice" aria-labelledby="vnotice-title">
      <h2 id="vnotice-title" className="vnotice__title">
        Səsvermə auditi yeniləndi
      </h2>
      <p className="vnotice__body">
        Səsvermə bağlandıqdan sonra aparılan yekun yoxlama zamanı müvəqqəti (disposable) e-poçt xidmətləri ilə səsvermə
        müddətində yaradılmış hesablardan verilən <strong className="tabular">{n}</strong> səs müəyyən edilib. Bu səslər
        silinməyib — audit izi saxlanılmaqla etibarsız sayılıb və sıralama yenidən hesablanıb.
      </p>
      <dl className="vnotice__totals">
        <div>
          <dt>Ümumi səs</dt>
          <dd className="tabular">{audit.totalVotes}</dd>
        </div>
        <div>
          <dt>Etibarlı səs</dt>
          <dd className="tabular">{audit.validVotes}</dd>
        </div>
        <div>
          <dt>Etibarsız sayılmış səs</dt>
          <dd className="tabular">{audit.invalidatedVotes}</dd>
        </div>
      </dl>
      <p className="vnotice__rule">
        Müvəqqəti e-poçt qaydası: <strong className="tabular">{n} səs</strong>
      </p>
      <p className="vnotice__fair">Qayda bütün iştirakçılara eyni şəkildə tətbiq olunub.</p>
    </section>
  )
}

/**
 * "Audit yeniləmələri": one line per admin decision, newest first — its Baku date and time, how many votes, and the
 * rule, in public words. Aggregates only: never a voter, an account, a contact, an address, an admin or a work.
 */
export function AuditUpdates({ audit }: { audit: VotingAudit | null }) {
  if (!audit) return null
  return (
    <section className="vaudit" aria-labelledby="vaudit-title">
      <h2 id="vaudit-title" className="vaudit__title">
        Audit yeniləmələri
      </h2>
      {audit.entries.length === 0 ? (
        <p className="vaudit__empty">Hələlik etibarsız sayılmış səs yoxdur.</p>
      ) : (
        <ol role="list" className="vaudit__list">
          {audit.entries.map((e) => (
            <li key={`${e.at}-${e.reason}`} className="vaudit__item">
              <time className="vaudit__time tabular" dateTime={e.at}>
                {formatDateTime(e.at)}
              </time>
              <span className="vaudit__text">{auditLine(e)}</span>
            </li>
          ))}
        </ol>
      )}
      {audit.rules && audit.rules.length > 0 && (
        <div className="vaudit__rules">
          <h3 className="vaudit__subtitle">
            Etibarsız sayılmış səslər — <span className="tabular">{audit.invalidatedVotes}</span>
          </h3>
          <dl className="vaudit__rulelist">
            {audit.rules.map((r) => (
              <div key={r.reason} className="vaudit__rule">
                <dt>
                  {auditRules[r.reason].name} — <span className="tabular">{r.votes}</span>
                </dt>
                <dd>{auditRules[r.reason].meaning}</dd>
              </div>
            ))}
          </dl>
          {audit.rules.length > 1 && (
            <p className="vaudit__sum tabular">
              {audit.rules.map((r) => r.votes).join(' + ')} = {audit.rules.reduce((a, r) => a + r.votes, 0)}
            </p>
          )}
        </div>
      )}
      <p className="vaudit__fine">Tarix və saat Bakı vaxtı ilədir. Səs sayları etibarlı səslərə əsasən yenilənir.</p>
    </section>
  )
}
