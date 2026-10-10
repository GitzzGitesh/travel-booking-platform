/**
 * The one locale every date, time and amount is formatted in: British English, whatever the browser's language
 * (product owner, 2026-10-10: dates in English while the UI is English only). Day before month ("Sun 14 Feb",
 * "5 November 2026") and a 24-hour clock. Replaced by the request's locale once markets and languages are decided (Q2).
 */
export const displayLocale = 'en-GB';
