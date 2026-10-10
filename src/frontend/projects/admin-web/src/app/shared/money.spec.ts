import { MoneyPipe, displayLocale } from './money';

// QA BUG-006: staff see amounts as customers do: grouped, with the currency, and never padded with storage zeros.
describe('MoneyPipe', () => {
  const pipe = new MoneyPipe();

  it('formats the API amount without its storage zeros, grouped and labelled with the currency', () => {
    const shown = pipe.transform({ amount: '1249.2800', currency: 'TND' });
    expect(shown).toBe('TND 1,249.280'); // the dinar's three decimals, not the four stored (a no-break space after the code)
    expect(pipe.transform({ amount: '0.1050', currency: 'EUR' })).toBe('€0.105'); // a third decimal is kept, never rounded away
  });

  it('formats in British English, whatever the browser language', () => {
    expect(displayLocale).toBe('en-GB');
    expect(pipe.transform({ amount: '1249.5', currency: 'EUR' })).toBe('€1,249.50'); // not "1.249,50 €"
  });

  it('shows nothing for no amount, and an unknown currency as sent', () => {
    expect(pipe.transform(null)).toBe('');
    expect(pipe.transform({ amount: '12.50', currency: 'NOT-A-CODE' })).toBe('12.50 NOT-A-CODE');
  });
});
