import { Link } from 'react-router-dom'
import { meApi } from '../../api/endpoints'
import type { MySubmission } from '../../api/types'
import { useAuth } from '../../auth/AuthContext'
import { WinnerBadge } from '../../components/WinnerBadge'
import { PageError, PageLoading } from '../../components/PageState'
import { formatDate, submissionStatusLabel } from '../../lib/format'
import { useApi } from '../../lib/useApi'
import { placeName, singularAz } from '../../lib/text'
import { useTitle } from '../../lib/useTitle'
import { AccountNav } from './AccountNav'
import './Account.css'

export default function MySubmissionsPage() {
  useTitle('İşlərim')
  const { user } = useAuth()
  const state = useApi((signal) => meApi.submissions(signal), [])

  return (
    <div className="container account">
      <header className="account__head">
        <p className="account__who">
          {user?.firstName} {user?.lastName}
        </p>
        <h1>İşlərim</h1>
        <AccountNav />
      </header>

      {state.status === 'loading' && <PageLoading />}
      {state.status === 'error' && <PageError error={state.error} onRetry={state.retry} />}
      {state.status === 'success' &&
        (state.data.length === 0 ? (
          <div className="empty">
            <h2>Hələ təklifiniz yoxdur</h2>
            <p>Hazırladığınız və göndərdiyiniz bütün təkliflər, hansı layihədə olmasından asılı olmayaraq, burada görünəcək.</p>
            <Link to="/competitions" className="btn btn--primary">
              Açıq müsabiqələrə bax
            </Link>
          </div>
        ) : (
          <SubmissionList items={state.data} />
        ))}
    </div>
  )
}

function SubmissionList({ items }: { items: MySubmission[] }) {
  return (
    <div className="subs">
      <div className="subs__header" aria-hidden="true">
        <span>Təklif</span>
        <span>Müsabiqə</span>
        <span>Tarix</span>
        <span>Status</span>
        <span />
      </div>
      <ul role="list">
        {items.map((s) => {
          const draft = s.status === 'Draft'
          return (
            <li key={s.id} className={`subs__row${draft ? ' is-draft' : ''}`}>
              <div className="subs__cell subs__cell--main">
                <span className="subs__ref tabular">
                  {s.referenceNumber ? `Təklif #${s.referenceNumber}` : 'İstinad nömrəsi təqdimdən sonra verilir'}
                </span>
                <span className="subs__title">{s.title || 'Adsız qaralama'}</span>
                {s.resultsPublished && (
                  // Neutral wording for everyone; only the winner gets a mark.
                  <span className="subs__result">
                    {s.isWinner && <WinnerBadge size="sm" label={s.place ? placeName(s.place) : undefined} />}
                    <Link to={`/competitions/${s.competitionSlug}/results`}>Nəticələr açıqlanıb — bax</Link>
                  </span>
                )}
              </div>
              <div className="subs__cell">
                <span className="subs__label">Müsabiqə</span>
                <span>
                  <Link to={`/projects/${s.projectSlug}`}>{s.projectName}</Link>
                  <br />
                  <Link to={`/competitions/${s.competitionSlug}`}>{s.competitionTitle}</Link>
                  {s.categoryName && (
                    <span className="subs__category">
                      <span className="visually-hidden">{singularAz(s.categoryLabel ?? 'Kateqoriya')}: </span>
                      {s.categoryName}
                    </span>
                  )}
                </span>
              </div>
              <div className="subs__cell">
                <span className="subs__label">{draft ? 'Yenilənib' : 'Göndərilib'}</span>
                <span className="tabular">{formatDate(draft ? s.updatedAt : (s.submittedAt ?? s.createdAt))}</span>
              </div>
              <div className="subs__cell">
                <span className="subs__label">Status</span>
                <span className={`subs__status subs__status--${s.status.toLowerCase()}`}>
                  {submissionStatusLabel[s.status]}
                </span>
              </div>
              <div className="subs__cell subs__cell--action">
                <Link
                  to={`/submissions/${s.id}`}
                  className={`btn btn--sm${draft ? ' btn--primary' : ''}`}
                  aria-label={`${draft ? 'Davam et' : 'Bax'}: ${s.title || 'adsız qaralama'}`}
                >
                  {draft ? 'Davam et' : 'Bax'}
                </Link>
              </div>
            </li>
          )
        })}
      </ul>
    </div>
  )
}
