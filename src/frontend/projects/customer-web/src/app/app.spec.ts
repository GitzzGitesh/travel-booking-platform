import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { App } from './app';
import { installDialogShim } from './ui/dialog.testing';

describe('App', () => {
  beforeEach(async () => {
    installDialogShim();
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter([])],
    }).compileComponents();
  });

  it('renders the shell landmarks', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;

    expect(element.querySelector('header .brand')?.getAttribute('href')).toBe('/');
    expect(element.querySelector('main#main-content router-outlet')).not.toBeNull();
    expect(element.querySelector('a.skip-link')?.getAttribute('href')).toBe('#main-content');
  });

  it('links Flights, Hotels (ADR 0030) and My trips, with nothing left as coming soon', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const nav = (fixture.nativeElement as HTMLElement).querySelector('.primary-nav')!;

    expect(nav.textContent).not.toContain('Soon');
    expect(
      [...nav.querySelectorAll('a')].map((a) => [a.textContent?.trim(), a.getAttribute('href')]),
    ).toEqual([
      ['Flights', '/'],
      ['Hotels', '/hotels'],
      ['My trips', '/trips'],
    ]);
  });

  it('opens the menu dialog from the menu button', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    const menuButton = element.querySelector<HTMLButtonElement>('.menu-button')!;

    expect(menuButton.getAttribute('aria-expanded')).toBe('false');
    menuButton.click();
    await fixture.whenStable();

    expect(element.querySelector('dialog#site-menu')?.hasAttribute('open')).toBe(true);
    expect(menuButton.getAttribute('aria-expanded')).toBe('true');

    element.querySelector<HTMLButtonElement>('dialog#site-menu .icon-btn')!.click();
    await fixture.whenStable();
    expect(menuButton.getAttribute('aria-expanded')).toBe('false');
  });
});
