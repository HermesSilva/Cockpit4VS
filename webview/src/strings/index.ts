// String lookup for the UI.
//
// This is NOT an i18n layer, and deliberately so: the VS port is English-only, with no
// locale, no catalogue switching and no runtime language change. What survives from the
// original is the lookup function, because it keeps every user-visible string in one
// reviewable file — the alternative was inlining ~500 literals across the components,
// which buys nothing and loses that.
//
// The signature is kept as `Translator` so the components' props did not have to change.
import { strings, type Strings } from './catalog';

export type StringKey = keyof Strings;

/**
 * A string by key, with positional interpolation: `{0}`, `{1}`, …
 *
 * An unknown key returns the key itself rather than an empty string — a visible
 * `foo.bar` in the UI is a bug report; a blank label is a mystery.
 */
export function t(key: StringKey, ...args: (string | number)[]): string {
  let text: string = strings[key] ?? String(key);

  args.forEach((value, index) => {
    text = text.replace(new RegExp(`\\{${index}\\}`, 'g'), String(value));
  });

  return text;
}

export type Translator = typeof t;
