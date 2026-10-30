/**
 * Advertising campaigns: a short address an ad can point at, and the few words written for that ad. Everything a
 * visitor has to be able to trust — the deadline, the prizes, the rules, whether entries are open at all — is read
 * from the competition itself and never repeated here, so the admin's edits reach the ad's page unchanged.
 *
 * The words below apply only while the competition accepts entries. Once it closes, its page goes back to the
 * competition's own title and description: "make the symbol" is not something to tell somebody who can no longer.
 */
export interface Campaign {
  /** The address the ads use, e.g. "/emircan". It shows the competition's page; nothing is redirected. */
  path: string
  competitionSlug: string
  /** The page's headline, in the voice of the ad. The competition's own title stays on the page above it. */
  headline: string
  lede: string
  /** The main button's label. */
  cta: string
  /**
   * Short reassurances set under the button. Each must be true of this competition's rules as published — they
   * were checked against its participation section when they were written, and must be re-checked if it changes.
   */
  reassurance: string[]
}

export const campaigns: Campaign[] = [
  {
    path: '/emircan',
    competitionSlug: 'emircanin-simvolu',
    headline: 'Əmircanın simvolunu sən yarat',
    lede: 'Əmircan üçün simvol və loqo ideyanı təqdim et',
    cta: 'İdeyanı təqdim et',
    // The platform does not ask entrants to be professionals ("PRIZMA haqqında"), and the competition's
    // participation section says AI-made entries are accepted.
    reassurance: ['Dizayner olmaq şərt deyil', 'AI alətlərindən istifadə etmək olar'],
  },
]

export function campaignFor(competitionSlug: string): Campaign | null {
  return campaigns.find((c) => c.competitionSlug === competitionSlug) ?? null
}
