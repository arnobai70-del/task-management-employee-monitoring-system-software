import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter } from 'react-router-dom';
import { AuthProvider } from './auth';
import AdminExperience from './AdminExperience';
import App from './App';
import OperationsIncidentRealtimeNotice from './OperationsIncidentRealtimeNotice';
import './styles.css';
import './admin-experience.css';

const root = document.getElementById('root');
if (!root) throw new Error('Root element was not found.');

createRoot(root).render(
  <StrictMode>
    <BrowserRouter>
      <AuthProvider>
        <App />
        <AdminExperience />
        <OperationsIncidentRealtimeNotice />
      </AuthProvider>
    </BrowserRouter>
  </StrictMode>
);
