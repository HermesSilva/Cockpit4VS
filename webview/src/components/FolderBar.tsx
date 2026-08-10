import { Tooltip } from './Tooltip';
import type { Translator } from '../strings';

interface Props {
  t: Translator;
  /** The folder this tab's conversation runs in. */
  cwd?: string;
  /** Disabled mid-turn: moving the folder clears the conversation. */
  busy: boolean;
  onChange: () => void;
  onOpen: (path: string) => void;
}

/**
 * The folder strip above the timeline.
 *
 * A conversation is scoped to a folder — its transcript, its permissions and its CLAUDE.md
 * all come from there — and with several tabs open on different folders, the only honest
 * thing is to say on each tab which folder it is talking about. It doubles as the way to
 * move a tab elsewhere, since that is the same question the user is asking when they look
 * here.
 */
export function FolderBar({ t, cwd, busy, onChange, onOpen }: Props) {
  if (!cwd) return null;

  // The tail is what identifies a folder in practice; the full path lives in the tooltip.
  const name = cwd.replace(/[\\/]+$/, '').split(/[\\/]/).pop() || cwd;

  return (
    <div className="folderbar">
      <Tooltip className="folderbar-tip" title={t('folder.title')} text={cwd}>
        <button
          type="button"
          className="folderbar-path"
          onClick={() => onOpen(cwd)}
          aria-label={t('folder.reveal')}
        >
          <span className="folderbar-icon" aria-hidden="true">
            {/* A folder, drawn rather than a glyph so it inherits the VS theme's colors. */}
            <svg width="13" height="13" viewBox="0 0 16 16" fill="none">
              <path
                d="M1.5 4.2c0-.66.54-1.2 1.2-1.2h3.1l1.3 1.6h6.2c.66 0 1.2.54 1.2 1.2v6.4c0 .66-.54 1.2-1.2 1.2H2.7c-.66 0-1.2-.54-1.2-1.2V4.2Z"
                stroke="currentColor"
                strokeWidth="1.2"
                strokeLinejoin="round"
              />
            </svg>
          </span>
          <span className="folderbar-name">{name}</span>
        </button>
      </Tooltip>

      <button
        type="button"
        className="folderbar-change"
        onClick={onChange}
        disabled={busy}
        title={busy ? t('folder.changeBusy') : t('folder.changeHint')}
      >
        {t('folder.change')}
      </button>
    </div>
  );
}
