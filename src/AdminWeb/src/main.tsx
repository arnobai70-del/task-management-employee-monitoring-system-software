import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter } from 'react-router-dom';
import { AuthProvider } from './auth';
import App from './App';
import OperationsIncidentRealtimeNotice from './OperationsIncidentRealtimeNotice';
import './styles.css';

const root = document.getElementById('root');
if (!root) throw new Error('Root element was not found.');

createRoot(root).render(
  <StrictMode>
    <BrowserRouter>
      <AuthProvider>
        <App />
        <OperationsIncidentRealtimeNotice />
      </AuthProvider>
    </BrowserRouter>
  </StrictMode>
);
