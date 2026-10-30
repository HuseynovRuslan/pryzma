import '@testing-library/jest-dom/vitest'

// jsdom has no layout, so no scrolling: a refused form brings its first invalid field into view a frame later
// (focusFirstInvalid), which threw after the test had moved on and failed the run. Tests that check scrolling
// still put their own stub in place.
if (!Element.prototype.scrollIntoView) Element.prototype.scrollIntoView = () => {}
