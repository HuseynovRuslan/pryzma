import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { ApiError } from '../api/client'
import { submissionsApi } from '../api/endpoints'
import { clearIntent } from '../auth/intent'
import { PageLoading } from '../components/PageState'
import { track } from '../lib/analytics'
import { useTitle } from '../lib/useTitle'

/**
 * Landing point of "Təklif ver" (also after the login/registration detour). Opens or resumes the
 * participant's draft for the chosen competition + category, then hands over to the submission form.
 */
export default function ParticipatePage() {
  useTitle('Təklif ver')
  const { slug = '' } = useParams()
  const [params] = useSearchParams()
  const category = params.get('category')
  const navigate = useNavigate()
  const [error, setError] = useState<ApiError>()
  const started = useRef(false)

  useEffect(() => {
    // StrictMode runs effects twice in development; the server call is idempotent, but one is enough.
    if (started.current) return
    started.current = true
    submissionsApi
      .start(slug, category)
      .then((draft) => {
        clearIntent()
        // Signed in and at the form: the funnel's third step. A resumed draft counts too — it is a new session
        // of somebody working on an entry, which is what this step measures.
        track({ name: 'submission_started', slug, submissionId: draft.id })
        navigate(`/submissions/${draft.id}`, { replace: true })
      })
      .catch((err: unknown) => setError(err instanceof ApiError ? err : new ApiError(0, 'Təklif açılmadı.')))
  }, [slug, category, navigate])

  if (!error) return <PageLoading label="Təklif forması açılır" />

  const categoryProblem = error.fieldErrors.category?.[0]
  return (
    <div className="container page-state" role="alert">
      <h1>Təklif açılmadı</h1>
      <p className="lede">{categoryProblem ?? error.message}</p>
      <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap' }}>
        <Link to={`/competitions/${slug}`} className="btn btn--primary">
          Müsabiqəyə qayıt
        </Link>
        <Link to="/my/submissions" className="btn">
          İşlərim
        </Link>
      </div>
    </div>
  )
}
