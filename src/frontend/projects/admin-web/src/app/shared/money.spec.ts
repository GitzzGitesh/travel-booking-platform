import { MoneyPipe } from './money';

// QA BUG-006: staff see amounts as customers do: grouped, with the currency, and never padded with storage zeros.
describe('MoneyPipe', () => {
  const pipe = new MoneyPipe();
  const intl = (amount: number, currency: string) =>
    new Intl.NumberFormat(undefined, {
      style: 'currency',
      currency,
      maximumFractionDigits: 20,
    }).format(amount);

  it('formats the API amount without its storage zeros, grouped and labelled with the currency', () => {
    const shown = pipe.transform({ amount: '1249.2800', currency: 'XTS' });
    expect(shown).toBe(intl(1249.28, 'XTS'));
    expect(shown).not.toContain('2800');
    expect(pipe.transform({ amount: '0.1050', currency: 'EUR' })).toBe(intl(0.105, 'EUR')); // a third decimal is kept, never rounded away
  });

  it('shows nothing for no amount, and an unknown currency as sent', () => {
    expect(pipe.transform(null)).toBe('');
    expect(pipe.transform({ amount: '12.50', currency: 'NOT-A-CODE' })).toBe('12.50 NOT-A-CODE');
  });
});
