import { lazy, Suspense } from 'react'
import { createBrowserRouter, Outlet, RouterProvider, ScrollRestoration } from 'react-router-dom'
import { AuthProvider } from './auth/AuthContext'
import { RequireAuth } from './auth/RequireAuth'
import { Footer } from './components/Footer'
import { Header } from './components/Header'
import { NotFoundState, PageLoading } from './components/PageState'
import { StaleBundleBoundary } from './components/StaleBundle'
import { VotingAnnouncement } from './components/voting/VotingAnnouncement'
import { PlatformProvider } from './platform/PlatformProvider'
import { campaigns } from './content/campaigns'
import { useCanonical } from './lib/canonical'
import { FeaturedCompetitionProvider } from './lib/featuredCompetition'
import HomePage from './pages/HomePage'

// Route-level code splitting: the home page ships in the main bundle, the rest load on demand.
const ProjectsPage = lazy(() => import('./pages/ProjectsPage'))
const ProjectDetailPage = lazy(() => import('./pages/ProjectDetailPage'))
const CompetitionsPage = lazy(() => import('./pages/CompetitionsPage'))
const CompetitionDetailPage = lazy(() => import('./pages/CompetitionDetailPage'))
const ParticipatePage = lazy(() => import('./pages/ParticipatePage'))
const SubmissionPage = lazy(() => import('./pages/submission/SubmissionPage'))
const ResultsPage = lazy(() => import('./pages/ResultsPage'))
const CompetitionResultsPage = lazy(() => import('./pages/results/CompetitionResultsPage'))
const GalleryPage = lazy(() => import('./pages/results/GalleryPage'))
const GalleryWorkPage = lazy(() => import('./pages/results/GalleryWorkPage'))
const VotingListPage = lazy(() => import('./pages/voting/VotingListPage'))
const VotingWorkPage = lazy(() => import('./pages/voting/VotingWorkPage'))
const HowToPage = lazy(() => import('./pages/InfoPages').then((m) => ({ default: m.HowToPage })))
const AboutPage = lazy(() => import('./pages/InfoPages').then((m) => ({ default: m.AboutPage })))
const TermsPage = lazy(() => import('./pages/InfoPages').then((m) => ({ default: m.TermsPage })))
const PrivacyPage = lazy(() => import('./pages/InfoPages').then((m) => ({ default: m.PrivacyPage })))
const ContactPage = lazy(() => import('./pages/InfoPages').then((m) => ({ default: m.ContactPage })))
const LoginPage = lazy(() => import('./pages/auth/LoginPage'))
const RegisterPage = lazy(() => import('./pages/auth/RegisterPage'))
const QrlogReturnPage = lazy(() => import('./pages/auth/QrlogReturnPage'))
const ForgotPasswordPage = lazy(() =>
  import('./pages/auth/PasswordPages').then((m) => ({ default: m.ForgotPasswordPage })),
)
const ResetPasswordPage = lazy(() =>
  import('./pages/auth/PasswordPages').then((m) => ({ default: m.ResetPasswordPage })),
)
const ConfirmEmailPage = lazy(() => import('./pages/auth/ConfirmEmailPage'))
const RegisterVerifyPage = lazy(() =>
  import('./pages/auth/EmailFirstRegistration').then((m) => ({ default: m.RegisterVerifyPage })),
)
const RegisterCompletePage = lazy(() =>
  import('./pages/auth/EmailFirstRegistration').then((m) => ({ default: m.RegisterCompletePage })),
)
const MySubmissionsPage = lazy(() => import('./pages/account/MySubmissionsPage'))

// Administration: a separate shell (no public header/footer), loaded only when an admin opens it.
const AdminLayout = lazy(() => import('./admin/AdminLayout').then((m) => ({ default: m.AdminLayout })))
const AdminDashboard = lazy(() => import('./admin/pages/DashboardPage'))
const AdminProjects = lazy(() => import('./admin/pages/ProjectsPage'))
const AdminProjectEdit = lazy(() => import('./admin/pages/ProjectEditPage'))
const AdminCompetitions = lazy(() => import('./admin/pages/CompetitionsPage'))
const AdminCompetitionEdit = lazy(() => import('./admin/pages/CompetitionEditPage'))
const AdminSubmissions = lazy(() => import('./admin/pages/SubmissionsPage'))
const AdminSubmissionDetail = lazy(() => import('./admin/pages/SubmissionDetailPage'))
const AdminSubmissionsPrint = lazy(() => import('./admin/pages/SubmissionsPrintPage'))
const AdminFinalShow = lazy(() => import('./admin/pages/FinalShowPage'))
const AdminLeaderboardPrint = lazy(() => import('./admin/pages/LeaderboardPrintPage'))
const AdminParticipants = lazy(() => import('./admin/pages/ParticipantsPage'))
const AdminVotingLaunch = lazy(() => import('./admin/pages/VotingLaunchPage'))
const ProfilePage = lazy(() => import('./pages/account/ProfilePage'))
const AdminSettings = lazy(() => import('./admin/pages/SettingsPage'))
const AdminJuryPrint = lazy(() => import('./admin/pages/JuryPrintPage'))

// The juror's panel: its own frame (no public header, no admin), on the juror's own session cookie.
const JuryStartPage = lazy(() => import('./jury/JuryShell').then((m) => ({ default: m.JuryStartPage })))
const JuryEvaluatePage = lazy(() => import('./jury/JuryEvaluate').then((m) => ({ default: m.JuryEvaluatePage })))

function Layout() {
  // The public pages only: the administration is behind a sign-in and is nobody's search result.
  useCanonical()
  return (
    // One competition's stage, fetched once, so the navbar badge and the page below it agree about it.
    <FeaturedCompetitionProvider>
      <a href="#main" className="skip-link">
        Məzmuna keç
      </a>
      <Header />
      <VotingAnnouncement />
      <main id="main" className="main" tabIndex={-1}>
        <Suspense fallback={<PageLoading />}>
          <Outlet />
        </Suspense>
      </main>
      <Footer />
      <ScrollRestoration />
    </FeaturedCompetitionProvider>
  )
}

const router = createBrowserRouter([
  {
    // The print view of the submissions: an admin page without the admin's frame, so a printed page is only the work.
    path: '/admin/submissions/print',
    element: (
      <Suspense fallback={<PageLoading />}>
        <AdminSubmissionsPrint />
      </Suspense>
    ),
  },
  {
    // The public vote on paper (the frozen Top 10, or the full ranking): an admin page without the admin's frame.
    path: '/admin/final-print/:competitionId',
    element: (
      <Suspense fallback={<PageLoading />}>
        <AdminLeaderboardPrint />
      </Suspense>
    ),
  },
  {
    // The jury final's show (D13): full screen on a television, so without the admin's frame — behind the same gate.
    path: '/admin/final-show/:competitionId',
    element: (
      <Suspense fallback={<PageLoading />}>
        <AdminFinalShow />
      </Suspense>
    ),
  },
  {
    // The jury's catalog of every work (9 or 6 to a page), each juror's score sheets and the internal ranking: printed,
    // without the admin frame.
    path: '/admin/jury-print/:competitionId',
    element: (
      <Suspense fallback={<PageLoading />}>
        <AdminJuryPrint />
      </Suspense>
    ),
  },
  {
    path: '/jury/start',
    element: (
      <Suspense fallback={<PageLoading />}>
        <JuryStartPage />
      </Suspense>
    ),
  },
  {
    path: '/jury/evaluate/:submissionId?',
    element: (
      <Suspense fallback={<PageLoading />}>
        <JuryEvaluatePage />
      </Suspense>
    ),
  },
  {
    path: '/admin',
    element: (
      <Suspense fallback={<PageLoading />}>
        <AdminLayout />
        <ScrollRestoration />
      </Suspense>
    ),
    children: [
      { index: true, element: <AdminDashboard /> },
      { path: 'projects', element: <AdminProjects /> },
      { path: 'projects/new', element: <AdminProjectEdit /> },
      { path: 'projects/:id', element: <AdminProjectEdit /> },
      { path: 'competitions', element: <AdminCompetitions /> },
      { path: 'competitions/new', element: <AdminCompetitionEdit /> },
      { path: 'competitions/:id', element: <AdminCompetitionEdit /> },
      { path: 'submissions', element: <AdminSubmissions /> },
      { path: 'submissions/:id', element: <AdminSubmissionDetail /> },
      { path: 'participants', element: <AdminParticipants /> },
      { path: 'voting', element: <AdminVotingLaunch /> },
      { path: 'settings', element: <AdminSettings /> },
      { path: '*', element: <NotFoundState /> },
    ],
  },
  {
    element: <Layout />,
    children: [
      { path: '/', element: <HomePage /> },
      { path: '/projects', element: <ProjectsPage /> },
      { path: '/projects/:slug', element: <ProjectDetailPage /> },
      { path: '/competitions', element: <CompetitionsPage /> },
      { path: '/competitions/:slug', element: <CompetitionDetailPage /> },
      // The short addresses ads point at (content/campaigns.ts): the competition's own page, at a name that fits
      // in an ad. Rendered in place rather than redirected, so the ad's parameters stay in the address bar.
      ...campaigns.map((c) => ({ path: c.path, element: <CompetitionDetailPage slug={c.competitionSlug} /> })),
      { path: '/competitions/:slug/results', element: <CompetitionResultsPage /> },
      { path: '/competitions/:slug/gallery', element: <GalleryPage /> },
      { path: '/competitions/:slug/gallery/:reference', element: <GalleryWorkPage /> },
      // The public vote. Anonymous: anybody may read the ballot; the API decides who may cast on it.
      { path: '/competitions/:slug/voting', element: <VotingListPage /> },
      { path: '/competitions/:slug/voting/:reference', element: <VotingWorkPage /> },
      {
        path: '/competitions/:slug/submit',
        element: (
          <RequireAuth>
            <ParticipatePage />
          </RequireAuth>
        ),
      },
      {
        path: '/submissions/:id',
        element: (
          <RequireAuth>
            <SubmissionPage />
          </RequireAuth>
        ),
      },
      { path: '/results', element: <ResultsPage /> },
      { path: '/how-to-participate', element: <HowToPage /> },
      { path: '/about', element: <AboutPage /> },
      { path: '/terms', element: <TermsPage /> },
      { path: '/privacy', element: <PrivacyPage /> },
      { path: '/contact', element: <ContactPage /> },
      { path: '/login', element: <LoginPage /> },
      { path: '/login/qrlog', element: <QrlogReturnPage /> },
      { path: '/register', element: <RegisterPage /> },
      { path: '/register/verify', element: <RegisterVerifyPage /> },
      { path: '/register/complete', element: <RegisterCompletePage /> },
      { path: '/forgot-password', element: <ForgotPasswordPage /> },
      { path: '/reset-password', element: <ResetPasswordPage /> },
      { path: '/confirm-email', element: <ConfirmEmailPage /> },
      {
        path: '/my/submissions',
        element: (
          <RequireAuth>
            <MySubmissionsPage />
          </RequireAuth>
        ),
      },
      {
        path: '/my/profile',
        element: (
          <RequireAuth>
            <ProfilePage />
          </RequireAuth>
        ),
      },
      { path: '*', element: <NotFoundState /> },
    ],
  },
])

export default function App() {
  return (
    // Outside the router on purpose: a chunk that will not load breaks routing itself, so the notice has to live
    // above it.
    <StaleBundleBoundary>
      <PlatformProvider>
        <AuthProvider>
          <RouterProvider router={router} />
        </AuthProvider>
      </PlatformProvider>
    </StaleBundleBoundary>
  )
}
