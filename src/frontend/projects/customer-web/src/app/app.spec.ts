import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { App } from './app';

describe('App', () => {
  beforeEach(async () => {
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
});
