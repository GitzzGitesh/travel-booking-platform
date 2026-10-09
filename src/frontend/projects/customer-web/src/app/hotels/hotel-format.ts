/** What a rate's board basis includes, worded the same on hotel search and on the booking page. */
const boards: Record<string, string> = {
  RoomOnly: 'Room only',
  Breakfast: 'Breakfast included',
  HalfBoard: 'Half board',
  FullBoard: 'Full board',
  AllInclusive: 'All inclusive',
};

/** "Breakfast" → "Breakfast included"; an unknown value is shown as the API sends it. */
export function boardLabel(board: string): string {
  return boards[board] ?? board;
}
