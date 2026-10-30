import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import '@fontsource-variable/archivo/standard.css'
import '@fontsource-variable/schibsted-grotesk/wght.css'
import './styles/tokens.css'
import './styles/base.css'
import './styles/components.css'
import App from './App'
import { captureAttribution } from './lib/analytics'

// Before anything navigates: the first address is the one an ad's parameters arrive on.
captureAttribution(window.location.search, window.location.pathname)

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
