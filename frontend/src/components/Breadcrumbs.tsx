import { Link } from 'react-router-dom'

export interface Crumb {
  label: string
  to?: string
}

/** Tells the visitor which level of Platform → Project → Competition they are on. */
export function Breadcrumbs({ items, className = '' }: { items: Crumb[]; className?: string }) {
  return (
    <nav aria-label="Harada olduğunuz" className={`crumbs ${className}`}>
      <ol role="list">
        {items.map((item, i) => {
          const last = i === items.length - 1
          return (
            <li key={`${item.label}-${i}`}>
              {last || !item.to ? (
                <span aria-current={last ? 'page' : undefined}>{item.label}</span>
              ) : (
                <Link to={item.to}>{item.label}</Link>
              )}
            </li>
          )
        })}
      </ol>
    </nav>
  )
}
