// The shape of an AskUserQuestion answer: the choices, comma separated, followed by the text
// the user added to that question. One place defines the separator — the modal writes it, the
// timeline card reads it.

// The choices sit on the first line; the added text on the ones after it.
export const ANSWER_NOTE_SEP = '\n';

/** Joins choices and added text. Text alone is a valid answer too. */
export function joinAnswer(choices: string, note: string): string {
  const n = note.trim();
  if (!n) return choices;
  return choices ? `${choices}${ANSWER_NOTE_SEP}${n}` : n;
}

/**
 * Undoes the join. It only cuts when the first line really is a set of known choices: a purely
 * written answer — even across several lines — stays whole, and the card shows it as "Other".
 */
export function splitAnswerNote(ans: string, known: Set<string>): { core: string; note?: string } {
  const at = ans.indexOf(ANSWER_NOTE_SEP);
  if (at < 0) return { core: ans };
  const core = ans.slice(0, at).trim();
  const toks = core
    .split(',')
    .map((s) => s.trim())
    .filter(Boolean);
  if (!toks.length || !toks.every((tk) => known.has(tk))) return { core: ans };
  return { core, note: ans.slice(at + ANSWER_NOTE_SEP.length).trim() };
}
