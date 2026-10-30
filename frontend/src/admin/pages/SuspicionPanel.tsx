import { useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { formatDateTime } from '../../lib/format'
import { useApi } from '../../lib/useApi'
import { adminApi, type AdminSuspicionWork, type AdminSuspiciousVote, type AdminVoteSuspicion } from '../api'
import { Badge, Notice } from '../ui'
import { VoteReviewPanel } from './VoteReviewPanel'

/** What each sign adds to a vote's score — the same weights the server uses, said in words. */
const WEIGHTS: [string, string][] = [
  ['Telefonu işin müəllifi ilə eynidir (öz işinə ikinci hesabla səs)', '+90'],
  ['Adı işin müəllifinin adı ilə eynidir', '+80'],
  ['Telefonu səs verən başqa hesabla eynidir', '+60 sonra açılan hesaba, +35 ilkinə'],
  ['Eyni ad-soyadla səs verən başqa hesab var', '+50 sonra açılana, +25 ilkinə'],
  ['Bu işə eyni IP-dən başqa hesablar səs verib', '8 və çox: +40, 3–7: +25, 1–2: +10'],
  ['E-maili bu işin başqa səsverəninkinə oxşayır', '+20'],
  ['Hesab səsvermə başlayandan sonra açılıb', '+10'],
  ['Hesab açılan kimi səs verib', '2 dəqiqədə: +10, 10 dəqiqədə: +5'],
  ['Bu işə 15 dəqiqədə 5+ yeni hesabdan səs gəlib', '+10'],
  ['Soyadı müəllifinkinə uyğundur (ailə — qadağan deyil)', '+5'],
]

/** "~13,6": an estimate, written the Azerbaijani way whatever the browser's locale. */
const approx = (n: number) => `~${Number.isInteger(n) ? n : n.toFixed(1).replace('.', ',')}`

/**
 * "Şübhəli səslər": every vote scored for signs of manipulation, summed per work — for the admin who awards the
 * places. A score is a weighted list of signs, not a measured probability, and it changes no vote; the page says
 * both. Only the accounts behind suspicious votes are named.
 *
 * The panel lays itself out by its own width, not the screen's (the admin has a side rail): a table where there is
 * room, and on a phone one block per work — its name, the number an admin acts on ("təmiz qalan"), and a strip
 * showing how its votes divide between high, medium and low suspicion, keyed by the three counts above.
 */
export function SuspicionPanel({ competitionId, onChanged }: { competitionId: number; onChanged?: () => void }) {
  const [version, setVersion] = useState(0)
  const [reviewing, setReviewing] = useState<number | null>(null)
  const state = useApi((signal) => adminApi.voteSuspicion(competitionId, signal), [competitionId, version])
  // Kept across a refresh after a decision, so the panel and the review under it do not blink away.
  const kept = useRef<AdminVoteSuspicion | null>(null)
  if (state.status === 'success') kept.current = state.data
  const s = state.status === 'success' ? state.data : kept.current
  if (!s || s.works.length === 0) return null
  const high = s.votes.filter((v) => v.score >= 60)
  const medium = s.votes.filter((v) => v.score < 60)

  return (
    <section className="a-panel a-susp" aria-labelledby="suspicion-title">
      <header className="a-panel__head">
        <h3 id="suspicion-title" className="a-panel__title">
          Şübhəli səslər
        </h3>
      </header>
      <p className="a-muted a-susp__intro">
        Hər səsə 0–99% arası şübhə balı verilir: əlamətlərin çəkili cəmidir, ölçülmüş ehtimal və ya sübut deyil. Heç bir səsə
        toxunmur — yerləri təyin edərkən nəzərə almaq üçündür. “Təxmini saxta” işin səslərinin ballarının cəmidir (məsələn, 70%-lik on səs
        ≈ 7).
      </p>

      {s.invalidatedCount > 0 && (
        <Notice tone="info">
          <span className="tabular">{s.invalidatedCount}</span> səs etibarsız sayılıb: bazada qalır, heç bir sayda və
          saytdakı reytinqdə nəzərə alınmır. Aşağıdakı rəqəmlər sayılan səslər üzrədir.
        </Notice>
      )}

      {/* The three counts double as the key to the strips below. */}
      <dl className="a-susp__totals">
        <Total tone="high" value={s.high} label="yüksək şübhəli" range="60% və çox" />
        <Total tone="mid" value={s.medium} label="orta" range="30–59%" />
        <Total tone="low" value={s.low} label="aşağı" range="30%-dən az" />
      </dl>

      <table className="a-table a-susp__works">
        <thead>
          <tr>
            <th scope="col">İş</th>
            <th scope="col">Bölgü</th>
            <th scope="col" className="num">
              Səs
            </th>
            <th scope="col" className="num">
              Yüksək
            </th>
            <th scope="col" className="num">
              Orta
            </th>
            <th scope="col" className="num">
              Təxmini saxta
            </th>
            <th scope="col" className="num">
              Təmiz qalan
            </th>
          </tr>
        </thead>
        <tbody>
          {s.works.map((w) => (
            <tr key={w.submissionId}>
              <td className="a-susp__work">
                <Link to={`/admin/submissions/${w.submissionId}`} className="a-table__link">
                  {w.title || 'Adsız iş'}
                </Link>
                <span className="a-table__sub tabular">
                  {w.reference}
                  {w.invalidatedVotes > 0 && ` · ${w.invalidatedVotes} etibarsız sayılıb`}
                </span>
                <button
                  type="button"
                  className="btn btn--sm btn--ghost a-susp__review"
                  aria-expanded={reviewing === w.submissionId}
                  onClick={() => setReviewing(reviewing === w.submissionId ? null : w.submissionId)}
                >
                  {reviewing === w.submissionId ? 'Yoxlamanı bağla' : 'Səsləri yoxla'}
                  {w.phoneClusterVotes > 0 && <span className="tabular"> · {w.phoneClusterVotes} telefon klasterində</span>}
                </button>
              </td>
              <td className="a-susp__share">
                <Share work={w} />
              </td>
              <td className="num a-susp__votes" data-label="səs">
                {w.votes}
              </td>
              <td className="num a-susp__high" data-label="yüksək">
                {w.high}
              </td>
              <td className="num a-susp__mid" data-label="orta">
                {w.medium}
              </td>
              <td className="num a-susp__fake" data-label="təxmini saxta">
                {approx(w.expectedFake)}
              </td>
              <td className="num a-susp__clean" data-label="təmiz qalan">
                <strong>{approx(w.cleanEstimate)}</strong>
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      {reviewing !== null && (
        <VoteReviewPanel
          competitionId={competitionId}
          submissionId={reviewing}
          onClose={() => setReviewing(null)}
          onChanged={() => {
            setVersion((n) => n + 1)
            onChanged?.()
          }}
        />
      )}

      {high.length > 0 && <SuspiciousVotes title={`Yüksək şübhəli səslər (${high.length})`} votes={high} />}
      {medium.length > 0 && (
        <details className="a-details">
          <summary>Orta şübhəli səslər ({medium.length})</summary>
          <SuspiciousVotes votes={medium} />
        </details>
      )}

      <details className="a-details">
        <summary>Bal necə hesablanır</summary>
        <table className="a-table a-susp__weights">
          <tbody>
            {WEIGHTS.map(([sign, points]) => (
              <tr key={sign}>
                <td>{sign}</td>
                <td className="tabular">{points}</td>
              </tr>
            ))}
          </tbody>
        </table>
        <p className="a-muted">
          Telefon və ad təkrarı güclü əlamətdir. IP zəifdir: mobil operatorlar çox adamı eyni ünvandan çıxarır, ona görə tək başına az bal
          verir. Ən çox 99%.
        </p>
      </details>
    </section>
  )
}

function Total({ tone, value, label, range }: { tone: 'high' | 'mid' | 'low'; value: number; label: string; range: string }) {
  return (
    <div className="a-susp__total">
      <dt className="a-susp__total-label">
        <span className={`a-susp__key a-susp__key--${tone}`} aria-hidden="true" />
        <span>
          {label}
          <span className="a-susp__range tabular">{range}</span>
        </span>
      </dt>
      <dd className="a-stat__value tabular">{value}</dd>
    </div>
  )
}

/** How a work's votes divide between high, medium and low suspicion, as one strip of its full count. */
function Share({ work: w }: { work: AdminSuspicionWork }) {
  const width = (n: number) => `${(n / w.votes) * 100}%`
  return (
    <span
      className="a-susp__strip"
      role="img"
      aria-label={`${w.votes} səsdən ${w.high} yüksək, ${w.medium} orta, ${w.low} aşağı şübhəli`}
    >
      {w.high > 0 && <span className="a-susp__seg a-susp__seg--high" style={{ width: width(w.high) }} />}
      {w.medium > 0 && <span className="a-susp__seg a-susp__seg--mid" style={{ width: width(w.medium) }} />}
      {w.low > 0 && <span className="a-susp__seg a-susp__seg--low" style={{ width: width(w.low) }} />}
    </span>
  )
}

function SuspiciousVotes({ title, votes }: { title?: string; votes: AdminSuspiciousVote[] }) {
  return (
    <>
      {title && <h4 className="a-subtitle">{title}</h4>}
      <table className="a-table a-susp__list">
        <thead>
          <tr>
            <th scope="col">Bal</th>
            <th scope="col">İş</th>
            <th scope="col">Hesab</th>
            <th scope="col">Səbəblər</th>
          </tr>
        </thead>
        <tbody>
          {votes.map((v) => (
            <tr key={`${v.reference}-${v.votedAt}-${v.voterName}`}>
              <td className="a-susp__score">
                <Badge tone={v.score >= 60 ? 'bad' : 'review'}>{v.score}%</Badge>
                {v.evidence === 'PhoneCluster' && <span className="a-susp__evidence">telefon klasteri</span>}
                {v.evidence === 'CoordinatedIdentity' && <span className="a-susp__evidence">R2 koordinasiyalı kimlik</span>}
                {v.evidence === 'DisposableEmailDuringVoting' && <span className="a-susp__evidence">D14 müvəqqəti e-poçt</span>}
              </td>
              <td className="tabular a-susp__ref">{v.reference}</td>
              <td className="a-susp__who">
                <span className="a-table__primary">{v.voterName}</span>
                <span className="a-table__sub tabular">
                  açılıb {formatDateTime(v.signedUpAt)}, səs {formatDateTime(v.votedAt)}
                </span>
              </td>
              <td className="a-susp__why">
                <ul className="a-reasons">
                  {v.reasons.map((r) => (
                    <li key={r}>{r}</li>
                  ))}
                </ul>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  )
}
