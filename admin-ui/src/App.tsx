import { NavLink, Navigate, Route, Routes, useLocation } from 'react-router';
import { useAuth } from './auth';
import { Login } from './pages/Login';
import { OverviewPage } from './pages/Overview';
import { Tickets } from './pages/Tickets';
import { TicketDetailPage } from './pages/TicketDetail';
import { Conversations } from './pages/Conversations';
import { ConversationPage } from './pages/Conversation';
import { FeedbackPage } from './pages/Feedback';
import { Staff } from './pages/Staff';
import { Audit } from './pages/Audit';

export function App() {
  const { me, logout } = useAuth();
  const location = useLocation();
  if (!me) return <Login />;
  const admin = me.role === 'admin';
  return (
    <>
      <header className="top">
        <h1>Operations · .NET</h1>
        <nav>
          <NavLink to="/">Overview</NavLink>
          <NavLink to="/tickets">Tickets</NavLink>
          <NavLink to="/conversations">Conversations</NavLink>
          <NavLink to="/feedback">Feedback</NavLink>
          {admin && <NavLink to="/staff">Staff</NavLink>}
          {admin && <NavLink to="/audit">Audit</NavLink>}
        </nav>
        <span className="who">{me.username} · {me.role}</span>
        <button onClick={() => void logout()}>Sign out</button>
      </header>
      <main key={location.pathname}>
        <Routes>
          <Route path="/" element={<OverviewPage />} />
          <Route path="/tickets" element={<Tickets />} />
          <Route path="/tickets/:number" element={<TicketDetailPage />} />
          <Route path="/conversations" element={<Conversations />} />
          <Route path="/conversations/:id" element={<ConversationPage />} />
          <Route path="/feedback" element={<FeedbackPage />} />
          <Route path="/staff" element={admin ? <Staff /> : <Navigate to="/" replace />} />
          <Route path="/audit" element={admin ? <Audit /> : <Navigate to="/" replace />} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </main>
    </>
  );
}
