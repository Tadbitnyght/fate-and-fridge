import { Navigate, Route, Routes } from 'react-router-dom'

import { AppShell } from './components/AppShell'
import { RequireSession } from './components/RequireSession'
import { StandaloneLayout } from './components/StandaloneLayout'
import { AuthProvider } from './features/auth/AuthProvider'
import { DepartmentsTab } from './features/reference-data/DepartmentsTab'
import { ReferenceDataPage } from './features/reference-data/ReferenceDataPage'
import { ReferenceDataPlaceholder } from './features/reference-data/ReferenceDataPlaceholder'
import { UnitsTab } from './features/reference-data/UnitsTab'
import { AccountSection } from './features/settings/AccountSection'
import { HouseholdSection } from './features/settings/HouseholdSection'
import { SecuritySection } from './features/settings/SecuritySection'
import { UsersSection } from './features/settings/UsersSection'
import { CalendarPage } from './pages/CalendarPage'
import { ChangePasswordPage } from './pages/ChangePasswordPage'
import { DashboardPage } from './pages/DashboardPage'
import { GroceriesPage } from './pages/GroceriesPage'
import { LoginPage } from './pages/LoginPage'
import { RecipesPage } from './pages/RecipesPage'
import { SettingsPage } from './pages/SettingsPage'
import { MilestonesPage } from './features/progress/MilestonesPage'
import { StartupRedirect } from './features/startup/StartupRedirect'
import { SetupPage } from './pages/SetupPage'
import { StatisticsPage } from './pages/StatisticsPage'

export function App() {
  return (
    <AuthProvider>
      <Routes>
        <Route element={<StandaloneLayout />}>
          <Route path="/setup" element={<SetupPage />} />
          <Route path="/login" element={<LoginPage />} />
          <Route path="/change-password" element={<ChangePasswordPage />} />
        </Route>

        <Route element={<RequireSession />}>
          <Route element={<AppShell />}>
            <Route index element={<StartupRedirect />} />
            <Route path="calendar" element={<CalendarPage />} />
            <Route path="dashboard" element={<DashboardPage />} />
            <Route path="recipes" element={<RecipesPage />} />
            <Route path="groceries" element={<GroceriesPage />} />
            <Route path="statistics" element={<StatisticsPage />} />
            <Route path="progress" element={<MilestonesPage />} />
            <Route path="settings" element={<SettingsPage />}>
              <Route index element={<HouseholdSection />} />
              <Route path="reference-data" element={<ReferenceDataPage />}>
                <Route index element={<DepartmentsTab />} />
                <Route path="units" element={<UnitsTab />} />
                <Route
                  path="ingredients"
                  element={
                    <ReferenceDataPlaceholder
                      title="Ingredients"
                      description="Canonical ingredients, departments, aliases, and merge."
                    />
                  }
                />
                <Route
                  path="proteins"
                  element={
                    <ReferenceDataPlaceholder
                      title="Proteins"
                      description="Primary proteins used to space out dinner repeats."
                    />
                  }
                />
                <Route
                  path="tags"
                  element={
                    <ReferenceDataPlaceholder
                      title="Tags"
                      description="Recipe tags used for filtering and browsing."
                    />
                  }
                />
              </Route>
              <Route path="users" element={<UsersSection />} />
              <Route path="security" element={<SecuritySection />} />
              <Route path="account" element={<AccountSection />} />
            </Route>
            <Route path="*" element={<Navigate to="/" replace />} />
          </Route>
        </Route>
      </Routes>
    </AuthProvider>
  )
}
