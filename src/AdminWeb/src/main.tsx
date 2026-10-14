import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter } from 'react-router-dom';
// Shared management styles are also used by shell-level toasts and pills, so load them before
// lazily loaded pages (same cascade order as the single-bundle build).
import './management.css';
import { AuthProvider } from './auth';
import AdminExperience from './AdminExperience';
import App from './App';
import OperationsIncidentRealtimeNotice from './OperationsIncidentRealtimeNotice';
import { StatusRailProvider } from './StatusRail';
import './styles.css';
import './admin-experience.css';

const root = document.getElementById('root');
if (!root) throw new Error('Root element was not found.');

createRoot(root).render(
  <StrictMode>
    <BrowserRouter>
      <AuthProvider>
        <StatusRailProvider>
          <App />
          <AdminExperience />
          <OperationsIncidentRealtimeNotice />
        </StatusRailProvider>
      </AuthProvider>
    </BrowserRouter>
  </StrictMode>
);
