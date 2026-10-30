/*
  The public site leads with competitions: a visitor goes home → poster → competition → entry, and never has to
  pass through a project to get there. Projects stay a real thing in the admin and keep their own pages
  (/projects, /projects/:slug), they are simply not part of the public wayfinding any more.
*/
export const primaryNav = [
  { to: '/competitions', label: 'Müsabiqələr' },
  { to: '/results', label: 'Nəticələr' },
  { to: '/how-to-participate', label: 'Necə iştirak etməli?' },
  { to: '/contact', label: 'Əlaqə' },
]
