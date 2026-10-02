import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { App } from './app';

describe('App', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  async function render(session: object | null, status = 200): Promise<HTMLElement> {
    const fixture = TestBed.createComponent(App);
    await Promise.resolve();
    http.expectOne('/api/admin/v1/session').flush(session, { status, statusText: String(status) });
    await new Promise((resolve) => setTimeout(resolve));
    fixture.detectChanges();
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  it('renders the shell landmarks', async () => {
    const element = await render(null, 401);

    expect(element.querySelector('header .brand')?.getAttribute('href')).toBe('/');
    expect(element.querySelector('main#main-content router-outlet')).not.toBeNull();
    expect(element.querySelector('a.skip-link')?.getAttribute('href')).toBe('#main-content');
    expect(element.querySelector('nav')).toBeNull(); // signed out: no navigation
  });

  it('shows navigation by permission and the signed-in staff member', async () => {
    const element = await render({ staffId: 'staff:7', permissions: ['orders.read'] });

    expect(element.querySelector('nav a')?.textContent).toContain('Booking queues');
    expect(element.querySelector('.staff-id')?.textContent).toContain('staff:7');
  });

  it('hides what the staff member may not use', async () => {
    const element = await render({ staffId: 'staff:8', permissions: ['personal-data.legal-hold'] });

    expect(element.querySelector('nav a')).toBeNull();
  });
});
