import type { GalleryItem } from '../../api/types'

/** What to call a work when its title field is not public: its reference number, never a person. */
export const workName = (w: Pick<GalleryItem, 'title' | 'reference'>) => w.title ?? `İş ${w.reference}`
