import { Navigate, Route, Routes } from 'react-router-dom'

import { AppShell } from './components/AppShell'
import { RequireSession } from './components/RequireSession'
import { AuthProvider } from './features/auth/AuthProvider'
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
import { SetupPage } from './pages/SetupPage'
import { StatisticsPage } from './pages/StatisticsPage'

export function App() {
  return (
    <AuthProvider>
      <Routes>
        <Route path="/setup" element={<SetupPage />} />
        <Route path="/login" element={<LoginPage />} />
        <Route path="/change-password" element={<ChangePasswordPage />} />

        <Route element={<RequireSession />}>
          <Route element={<AppShell />}>
            <Route index element={<CalendarPage />} />
            <Route path="dashboard" element={<DashboardPage />} />
            <Route path="recipes" element={<RecipesPage />} />
            <Route path="groceries" element={<GroceriesPage />} />
            <Route path="statistics" element={<StatisticsPage />} />
            <Route path="settings" element={<SettingsPage />}>
              <Route index element={<HouseholdSection />} />
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
