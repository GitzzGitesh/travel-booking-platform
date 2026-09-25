import { ChangeDetectionStrategy, Component, ElementRef, signal, viewChild } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { closeOnBackdropClick, openSheet } from './ui/dialog';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  selector: 'app-root',
  styleUrl: './app.css',
  templateUrl: './app.html',
})
export class App {
  /** Products shown as coming soon: navigation placeholders only, with no routes behind them. */
  protected readonly comingSoon = ['Hotels', 'My trips'] as const;
  protected readonly menuOpen = signal(false);
  protected readonly closeOnBackdropClick = closeOnBackdropClick;

  private readonly menu = viewChild.required<ElementRef<HTMLDialogElement>>('menu');
  private readonly menuButton = viewChild.required<ElementRef<HTMLButtonElement>>('menuButton');

  protected openMenu(): void {
    openSheet(this.menu().nativeElement, this.menuButton().nativeElement);
    this.menuOpen.set(true);
  }
}
