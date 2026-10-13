import { createContext, useContext, useState, type PropsWithChildren, type ReactNode } from 'react';
import { createPortal } from 'react-dom';

// Live status pills (incidents, security alerts, notifications, follow-ups) are rendered by
// independent realtime components. They share one rail under the top bar so they wrap instead of
// being stacked at fixed pixel offsets on top of each other and of the page header.
const StatusRailContext = createContext<{ element: HTMLElement | null; setElement: (element: HTMLElement | null) => void } | undefined>(undefined);

export function StatusRailProvider({ children }: PropsWithChildren) {
  const [element, setElement] = useState<HTMLElement | null>(null);
  return <StatusRailContext.Provider value={{ element, setElement }}>{children}</StatusRailContext.Provider>;
}

export function StatusRail() {
  const context = useContext(StatusRailContext);
  return <div className="status-rail" ref={context?.setElement} />;
}

export function StatusRailItem({ children }: { children: ReactNode }) {
  const element = useContext(StatusRailContext)?.element;
  return element ? createPortal(children, element) : null;
}
