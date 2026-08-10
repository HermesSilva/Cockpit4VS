import { createPortal } from 'react-dom';
import type { ReactNode } from 'react';

// Renders the children in the webview's <body role="document">, outside the .app tree.
// It guarantees modal overlays escape the clipping/stacking of ancestors with
// overflow or transform, and always stay on top.
export function Portal({ children }: { children: ReactNode }) {
  return createPortal(children, document.body);
}
