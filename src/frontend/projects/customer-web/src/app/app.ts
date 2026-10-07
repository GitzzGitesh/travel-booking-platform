import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { CustomerSession } from './customer-session';
import { closeOnBackdropClick, openSheet } from './ui/dialog';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  selector: 'app-root',
  styleUrl: './app.css',
  templateUrl: './app.html',
})
export class App {
  /** Products shown as coming soon: navigation placeholders only, with no routes behind them (none today). */
  protected readonly comingSoon: readonly string[] = [];
  protected readonly menuOpen = signal(false);
  protected readonly session = inject(CustomerSession);
  protected readonly signingOut = signal(false);
  private readonly router = inject(Router);
  protected readonly closeOnBackdropClick = closeOnBackdropClick;

  private readonly menu = viewChild.required<ElementRef<HTMLDialogElement>>('menu');
  private readonly menuButton = viewChild.required<ElementRef<HTMLButtonElement>>('menuButton');

  constructor() {
    void this.session.load(); // in the browser only: the server-side render is always signed out (ADR 0028)
  }

  /** The server runs the sign-in (ADR 0028) and brings the customer back to this page. */
  protected signIn(): void {
    void this.session.signIn(this.router.url);
  }

  protected async signOut(): Promise<void> {
    this.signingOut.set(true);
    try {
      await this.session.signOut();
    } finally {
      this.signingOut.set(false);
    }
  }

  protected openMenu(): void {
    openSheet(this.menu().nativeElement, this.menuButton().nativeElement);
    this.menuOpen.set(true);
  }
}
