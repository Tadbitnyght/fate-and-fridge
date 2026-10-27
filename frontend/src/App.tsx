import { Navigate, Route, Routes } from 'react-router-dom'

import { AppShell } from './components/AppShell'
import { RequireSession } from './components/RequireSession'
import { StandaloneLayout } from './components/StandaloneLayout'
import { AuthProvider } from './features/auth/AuthProvider'
import { LiveSyncProvider } from './features/live-sync/LiveSyncProvider'
import { useDocumentTitle } from './hooks/useDocumentTitle'
import { DepartmentsTab } from './features/reference-data/DepartmentsTab'
import { IngredientsTab } from './features/reference-data/IngredientsTab'
import { ProteinsTab } from './features/reference-data/ProteinsTab'
import { ReferenceDataPage } from './features/reference-data/ReferenceDataPage'
import { TagsTab } from './features/reference-data/TagsTab'
import { UnitsTab } from './features/reference-data/UnitsTab'
import { AboutSection } from './features/settings/AboutSection'
import { AccountSection } from './features/settings/AccountSection'
import { AuditLogSection } from './features/settings/AuditLogSection'
import { BackupsSection } from './features/settings/BackupsSection'
import { HouseholdSection } from './features/settings/HouseholdSection'
import { SecuritySection } from './features/settings/SecuritySection'
import { UsersSection } from './features/settings/UsersSection'
import { CalendarPage } from './features/calendar/CalendarPage'
import { CalendarPrintPage } from './features/calendar/CalendarPrintPage'
import { ChangePasswordPage } from './pages/ChangePasswordPage'
import { DashboardPage } from './pages/DashboardPage'
import { GroceriesPage } from './pages/GroceriesPage'
import { GroceryPrintPage } from './features/groceries/GroceryPrintPage'
import { LoginPage } from './pages/LoginPage'
import { ImportPage } from './features/import/ImportPage'
import { RecipeFormPage } from './features/recipes/RecipeFormPage'
import { RecipesBrowser } from './features/recipes/RecipesBrowser'
import { SettingsPage } from './pages/SettingsPage'
import { StartupRedirect } from './features/startup/StartupRedirect'
import { RestoreFromBackupPage } from './features/setup/RestoreFromBackupPage'
import { SetupPage } from './pages/SetupPage'
import { StatisticsPage } from './features/statistics/StatisticsPage'

export function App() {
  useDocumentTitle()

  return (
    <AuthProvider>
      <LiveSyncProvider>
        <Routes>
          <Route element={<StandaloneLayout />}>
            <Route path="/setup" element={<SetupPage />} />
            <Route path="/setup/restore" element={<RestoreFromBackupPage />} />
            <Route path="/login" element={<LoginPage />} />
            <Route path="/change-password" element={<ChangePasswordPage />} />
          </Route>

          <Route element={<RequireSession />}>
            <Route element={<AppShell />}>
              <Route index element={<StartupRedirect />} />
              <Route path="calendar" element={<CalendarPage />} />
              <Route path="calendar/print" element={<CalendarPrintPage />} />
              <Route path="dashboard" element={<DashboardPage />} />
              <Route path="recipes" element={<RecipesBrowser />} />
              <Route path="recipes/new" element={<RecipeFormPage />} />
              <Route path="recipes/import" element={<ImportPage />} />
              <Route path="recipes/:id/edit" element={<RecipeFormPage />} />
              <Route path="groceries" element={<GroceriesPage />} />
              <Route path="groceries/print" element={<GroceryPrintPage />} />
              <Route path="statistics" element={<StatisticsPage />} />
              <Route path="settings" element={<SettingsPage />}>
                <Route index element={<HouseholdSection />} />
                <Route path="reference-data" element={<ReferenceDataPage />}>
                  <Route index element={<DepartmentsTab />} />
                  <Route path="units" element={<UnitsTab />} />
                  <Route path="ingredients" element={<IngredientsTab />} />
                  <Route path="proteins" element={<ProteinsTab />} />
                  <Route path="tags" element={<TagsTab />} />
                </Route>
                <Route path="users" element={<UsersSection />} />
                <Route path="security" element={<SecuritySection />} />
                <Route path="audit" element={<AuditLogSection />} />
                <Route path="backups" element={<BackupsSection />} />
                <Route path="about" element={<AboutSection />} />
                <Route path="account" element={<AccountSection />} />
              </Route>
              <Route path="*" element={<Navigate to="/" replace />} />
            </Route>
          </Route>
        </Routes>
      </LiveSyncProvider>
    </AuthProvider>
  )
}
