import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { StaffSession } from './staff-session';

/** Test providers: a signed-in staff member holding these permissions, the router and a testing HttpClient. */
export function staffTestProviders(permissions: string[]) {
  const session: Partial<StaffSession> = {
    state: signal('signed-in'),
    can: (permission: string) => permissions.includes(permission),
  };
  return [
    provideRouter([]),
    provideHttpClient(),
    provideHttpClientTesting(),
    { provide: StaffSession, useValue: session },
  ];
}

/** Lets pending promises (the generated client) settle, then renders. */
export async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
  await fixture.whenStable();
}

/** Types into an input and submits its form, as a person would. */
export function fillAndSubmit(form: HTMLFormElement, values: Record<string, string>): void {
  for (const [name, value] of Object.entries(values)) {
    const field = form.querySelector(`[name="${name}"]`) as HTMLInputElement;
    field.value = value;
    field.dispatchEvent(new Event('input'));
  }
  form.dispatchEvent(new Event('submit', { cancelable: true }));
}

export function button(root: HTMLElement, text: string): HTMLButtonElement {
  return Array.from(root.querySelectorAll('button')).find((b) =>
    b.textContent?.includes(text),
  ) as HTMLButtonElement;
}
