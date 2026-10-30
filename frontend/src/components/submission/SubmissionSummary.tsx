import type { Submission } from '../../api/types'
import { formatBytes } from '../../lib/format'
import { singularAz } from '../../lib/text'
import { AuthedImage } from './AuthedImage'

/** Everything the participant is (or was) submitting, as it will be judged. Used by preview and receipt. */
export function SubmissionSummary({ submission: s }: { submission: Submission }) {
  const label = singularAz(s.competition.categoryLabel ?? 'Kateqoriya')
  return (
    <div className="summary">
      <dl className="summary__context">
        <div>
          <dt>Layihə</dt>
          <dd>{s.project.name}</dd>
        </div>
        <div>
          <dt>Müsabiqə</dt>
          <dd>{s.competition.title}</dd>
        </div>
        {s.category && (
          <div>
            <dt>{label}</dt>
            <dd className="summary__category">{s.category.name}</dd>
          </div>
        )}
      </dl>

      <dl className="summary__answers">
        {s.fields.map((f) => {
          if (f.fieldType === 'File') {
            const own = s.files.filter((x) => x.fieldKey === f.key)
            return (
              <div key={f.key} className="summary__row">
                <dt>{f.label}</dt>
                <dd>
                  {own.length === 0 ? (
                    <span className="muted">Fayl əlavə olunmayıb</span>
                  ) : (
                    <ul role="list" className="summary__files">
                      {own.map((file) => (
                        <li key={file.id}>
                          {file.contentType.startsWith('image/') ? (
                            <AuthedImage submissionId={s.id} fileId={file.id} alt={file.originalFileName} />
                          ) : (
                            <span className="thumb thumb--doc" aria-hidden="true">
                              PDF
                            </span>
                          )}
                          <span className="summary__file-name">{file.originalFileName}</span>
                          <span className="muted">{formatBytes(file.sizeBytes)}</span>
                        </li>
                      ))}
                    </ul>
                  )}
                </dd>
              </div>
            )
          }
          const value = s.answers[f.key]
          const shown = f.fieldType === 'Checkbox' ? (value === 'true' ? 'Bəli' : 'Xeyr') : value
          return (
            <div key={f.key} className="summary__row">
              <dt>{f.label}</dt>
              <dd className={f.fieldType === 'LongText' ? 'summary__long' : undefined}>
                {shown ? shown : <span className="muted">Doldurulmayıb</span>}
              </dd>
            </div>
          )
        })}
      </dl>
    </div>
  )
}
