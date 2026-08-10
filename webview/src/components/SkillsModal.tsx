import { useEffect, useMemo, useState } from 'react';
import type { Translator } from '../strings';
import type { SkillState, SkillOverride, HookInjection } from '../../../shared/protocol';
import { Portal } from './Portal';
import { fmtTk } from '../util/format';

interface Props {
  t: Translator;
  skills?: SkillState[];
  listingTokens?: number;
  total?: number;
  listed?: number;
  hooks?: HookInjection[];
  busy: boolean;
  onRefresh: () => void;
  onOverride: (name: string, value: SkillOverride) => void;
  onClose: () => void;
}

const OVERRIDES: SkillOverride[] = ['on', 'name-only', 'user-invocable-only', 'off'];

// Known Claude Code hook events (the trigger each one fires on). DirectoryAdded is new
// in 2.1.219 (fires after /add-dir or an SDK register_repo_root). An unknown event falls
// back to a generic hint — the panel never breaks on a new trigger name.
const HOOK_EVENTS = new Set([
  'SessionStart',
  'SessionEnd',
  'UserPromptSubmit',
  'PreToolUse',
  'PostToolUse',
  'Notification',
  'Stop',
  'SubagentStop',
  'PreCompact',
  'DirectoryAdded',
]);
function hookEventHint(t: Translator, event: string): string {
  // Every `hookEvent.<Known>` key exists in the catalog; the cast is safe because we
  // only build it for events in HOOK_EVENTS. Unknown events use the generic key.
  return HOOK_EVENTS.has(event)
    ? t(`hookEvent.${event}` as Parameters<Translator>[0])
    : t('hookEvent.unknown', event);
}

// Groups by origin. `source` comes from the engine (get_context_usage): measured on CLI
// 2.1.217 as 'projectSettings' | 'userSettings' | 'built-in'. Anything new falls into 'other'.
type Group = 'project' | 'user' | 'built-in' | 'other';
const GROUP_ORDER: Group[] = ['project', 'user', 'built-in', 'other'];

export function groupOf(source?: string): Group {
  if (source === 'projectSettings') return 'project';
  if (source === 'userSettings') return 'user';
  if (source === 'built-in') return 'built-in';
  return 'other';
}

/** The OBSERVATION axis — what is happening — kept apart from the configuration one. */
type Observed = 'active' | 'resident' | 'light';

/**
 * `resident` is the state that must not be hidden: the skill was switched off but its body
 * is still in the context — switching off prevents a re-trigger, it does not unload, and the
 * engine offers no way to unload one skill on its own.
 */
export function observed(s: SkillState): Observed {
  if (s.active !== true) return 'light';
  const off = s.override === 'off' || s.override === 'user-invocable-only';
  return off ? 'resident' : 'active';
}

// The "Skills" panel (X2). Two axes side by side: the dropdown CONFIGURES what enters the
// listing; the column beside it OBSERVES what is already in the context. No invented numbers —
// metadata is measured by the engine, the loaded body is an estimate and is labelled as one.
export function SkillsModal({
  t,
  skills,
  listingTokens,
  total,
  listed,
  hooks,
  busy,
  onRefresh,
  onOverride,
  onClose,
}: Props) {
  const [filter, setFilter] = useState<Group | 'all'>('all');

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  const list = skills ?? [];
  const groups = useMemo(() => {
    const map = new Map<Group, SkillState[]>();
    for (const s of list) {
      const g = groupOf(s.source);
      const arr = map.get(g) ?? [];
      arr.push(s);
      map.set(g, arr);
    }
    return GROUP_ORDER.filter((g) => map.has(g)).map((g) => ({ group: g, items: map.get(g)! }));
  }, [list]);

  // The sums: metadata comes measured from the engine, while the loaded body is ESTIMATED —
  // and only exists when the injected message could be measured — so the totals stay apart.
  const activeTokens = list.reduce((a, s) => a + (s.active ? (s.activeTokens ?? 0) : 0), 0);
  const activeCount = list.filter((s) => s.active).length;
  // The weight bar's scale, relative to the listing's most expensive skill.
  const maxMeta = list.reduce((a, s) => Math.max(a, s.metaTokens ?? 0), 0);
  const shown = filter === 'all' ? groups : groups.filter((g) => g.group === filter);

  return (
    <Portal>
      <div className="modal-overlay" onClick={onClose}>
        <div className="modal usage" onClick={(e) => e.stopPropagation()}>
          <div className="usage-head">
            <span className="modal-title">{t('skills.title')}</span>
            <button type="button" className="ctx-link mcp-refresh" onClick={onRefresh} disabled={busy}>
              ⟳ {t('plugins.refresh')}
            </button>
            <button type="button" className="usage-close" title={t('common.close')} onClick={onClose}>
              ✕
            </button>
          </div>

          {busy && list.length === 0 ? (
            <div className="usage-loading">
              <span className="usage-spinner" aria-hidden="true" />
              <span>{t('skills.loading')}</span>
            </div>
          ) : list.length === 0 ? (
            <div className="usage-body">
              <div className="usage-muted">{t('skills.none')}</div>
              <div className="usage-muted">{t('skills.hint')}</div>
            </div>
          ) : (
            <div className="usage-body">
              <div className="skills-tiles">
                <Tile tone="count" label={t('skills.tile.count')} value={String(total ?? list.length)} />
                <Tile
                  tone="metadata"
                  label={t('skills.tile.metadata')}
                  value={fmtTk(listingTokens)}
                  note={listed != null && total != null && listed < total ? t('skills.tile.listedOf', String(listed), String(total)) : undefined}
                />
                <Tile
                  tone="active"
                  label={t('skills.tile.active')}
                  value={activeCount === 0 ? '—' : fmtTk(activeTokens)}
                  note={activeCount > 0 ? t('skills.tile.estimated') : undefined}
                  strong={activeCount > 0}
                />
              </div>

              {groups.length > 1 && (
                <div className="skills-filters">
                  {groups.map((g) => (
                    <button
                      key={g.group}
                      type="button"
                      className={`skills-chip ${g.group} ${filter === g.group ? 'on' : ''}`}
                      onClick={() => setFilter(g.group)}
                    >
                      {t(`skills.group.${g.group}` as never)}
                      <span className="skills-chip-n">{g.items.length}</span>
                    </button>
                  ))}
                  <button
                    type="button"
                    className={`skills-chip ${filter === 'all' ? 'on' : ''}`}
                    onClick={() => setFilter('all')}
                  >
                    {t('skills.group.all')}
                    <span className="skills-chip-n">{list.length}</span>
                  </button>
                </div>
              )}

              {shown.map(({ group, items }) => (
                <div key={group} className={`skills-group ${group}`}>
                  <div className="skills-group-label">
                    {t(`skills.group.${group}` as never)}
                    <span className="skills-group-n">{items.length}</span>
                  </div>
                  {items.map((s) => (
                    <SkillRow key={s.name} t={t} s={s} maxMeta={maxMeta} onOverride={onOverride} />
                  ))}
                </div>
              ))}

              {hooks && hooks.length > 0 && (
                <div className="skills-group hooks">
                  <div className="skills-group-label">
                    {t('skills.hooks.title')}
                    <span className="skills-group-n">{hooks.length}</span>
                  </div>
                  <div className="skills-hooks-note">{t('skills.hooks.note')}</div>
                  {hooks.map((h) => (
                    <div key={h.hook} className="skills-hook-row">
                      <span className="skills-name">{h.hook}</span>
                      {h.event && (
                        <span className="skills-hook-event" title={hookEventHint(t, h.event)}>
                          {h.event}
                        </span>
                      )}
                      {h.skill && <span className="skills-hook-skill">→ {h.skill}</span>}
                      <span className="skills-hook-tk">
                        {h.count > 1 ? `${h.count}× · ` : ''}
                        {t('skills.hooks.tokens', fmtTk(h.tokens))}
                      </span>
                    </div>
                  ))}
                </div>
              )}

              <div className="skills-legend">
                <div>
                  <span className="skills-obs active">⚡ {t('skills.obs.active')}</span> — {t('skills.legend.active')}
                </div>
                <div>
                  <span className="skills-obs light">{t('skills.obs.light')}</span> — {t('skills.legend.light')}
                </div>
                <div>
                  <span className="skills-obs resident">⚠ {t('skills.obs.resident')}</span> — {t('skills.legend.resident')}
                </div>
                <div className="skills-legend-scope">{t('skills.legend.scope')}</div>
              </div>
            </div>
          )}
        </div>
      </div>
    </Portal>
  );
}

function Tile({
  tone,
  label,
  value,
  note,
  strong,
}: {
  tone: 'count' | 'metadata' | 'active';
  label: string;
  value: string;
  note?: string;
  strong?: boolean;
}) {
  return (
    <div className={`skills-tile ${tone} ${strong ? 'strong' : ''}`}>
      <div className="skills-tile-label">{label}</div>
      <div className="skills-tile-value">{value}</div>
      <div className="skills-tile-note">{note ?? ' '}</div>
    </div>
  );
}

function SkillRow({
  t,
  s,
  maxMeta,
  onOverride,
}: {
  t: Translator;
  s: SkillState;
  maxMeta: number;
  onOverride: (name: string, value: SkillOverride) => void;
}) {
  const obs = observed(s);
  const group = groupOf(s.source);
  const off = s.override === 'off';
  // The weight bar: this skill's cost against the most expensive one in the listing.
  const weight = maxMeta > 0 ? Math.max(2, Math.round(((s.metaTokens ?? 0) / maxMeta) * 100)) : 0;
  return (
    <div className={`skills-row ${group} ${obs} ${off ? 'is-off' : ''}`}>
      <div className="skills-row-head">
        <span className="skills-name">{s.name}</span>
        <span className={`skills-src ${group}`}>{t(`skills.group.${group}` as never)}</span>
        <span className={`skills-obs ${obs}`} title={t(`skills.legend.${obs}` as never)}>
          {obs === 'active' && '⚡ '}
          {obs === 'resident' && '⚠ '}
          {t(`skills.obs.${obs}` as never)}
        </span>
      </div>
      <div className="skills-weight" aria-hidden="true">
        <span className="skills-weight-fill" style={{ width: `${weight}%` }} />
      </div>
      <div className="skills-row-body">
        <div className="skills-cost">
          {t('skills.metaTokens', s.metaTokens != null ? String(s.metaTokens) : '?')}
          {obs === 'active' &&
            (s.activeTokens != null ? (
              <span className="skills-active-tk"> · {t('skills.activeTokens', fmtTk(s.activeTokens))}</span>
            ) : (
              <span className="skills-active-tk"> · {t('skills.activeUnknown')}</span>
            ))}
          {/* Switched off and still resident: the number is what still occupies the context. */}
          {obs === 'resident' && (
            <span className="skills-resident-tk">
              {' · '}
              {s.activeTokens != null
                ? t('skills.residentTokens', fmtTk(s.activeTokens))
                : t('skills.residentUnknown')}
            </span>
          )}
          {/* A load through a hook is INFERRED: the injected body matches the SKILL.md on disk. */}
          {s.invokedBy === 'hook' && (s.active || obs === 'resident') && (
            <span className="skills-via-hook"> · {t('skills.viaHook')}</span>
          )}
          {obs === 'light' && s.override === 'name-only' && ` · ${t('skills.note.nameOnly')}`}
          {obs === 'light' && s.override === 'user-invocable-only' && ` · ${t('skills.note.slashOnly')}`}
          {obs === 'light' && s.override === 'off' && ` · ${t('skills.note.off')}`}
        </div>
        <select
          className="skills-select"
          title={t('skills.overrideHelp')}
          value={s.override ?? 'on'}
          onChange={(e) => onOverride(s.name, e.target.value as SkillOverride)}
        >
          {OVERRIDES.map((o) => (
            <option key={o} value={o}>
              {t(`skills.override.${o}` as never)}
            </option>
          ))}
        </select>
      </div>
    </div>
  );
}

