export const TOKENS_JSON: string;
export const TOKENS_CSS: string;
/** The CSS text for a token file already parsed. */
export function generate(tokens: unknown): string;
/** The CSS for the committed token file. */
export function build(): string;
