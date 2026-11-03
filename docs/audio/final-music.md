# Final-hours music: sources and licence

Played on the pages of an open vote during its last three hours (`frontend/src/components/FinalMusic.tsx`).
Both tracks come from Pixabay Music under the Pixabay Content License: free to use, commercial use allowed, no
attribution required, modification allowed. The only relevant prohibition is selling or distributing a track
on its own, unchanged. Here both are trimmed, levelled, re-encoded and used as part of the site.
Licence summary: https://pixabay.com/service/license-summary/

| File | Track | Author | Source | Use |
|---|---|---|---|---|
| `frontend/public/audio/final-hours-bed.mp3` | Thinking Time | Sonican | https://pixabay.com/music/pulses-thinking-time-219360/ | the loop, from 3 h to 60 s before the close |
| `frontend/public/audio/final-minute.mp3` | Time Pressure (Action Tension Countdown Trailer Main) | AudioAtlant | https://pixabay.com/music/orchestral-time-pressure-action-tension-countdown-trailer-main-609551/ | the last 55.5 s, ending on the close |

Downloaded on 3 November 2026 from the tracks' own Pixabay pages, using their own "Free download" buttons.

Processing (ffmpeg):
- The leading and trailing silence was cut: the bed keeps 0.64–126.14 s and the finale keeps 1.04–56.54 s.
- The levels were matched: the bed by −0.7 dB to about −16.5 LUFS, the finale by −5.7 dB to about −15.4 LUFS.
- Short fades were added at the edges against clicks.
- Both were re-encoded as MP3 at 96 kbps, 44.1 kHz stereo, with the metadata stripped. The bed is 1.5 MB and the finale is 0.67 MB.

The finale has a composed 2.5 s pause at 45.7–48.2 s. Its last hit therefore lands 7.3 s before the close.
