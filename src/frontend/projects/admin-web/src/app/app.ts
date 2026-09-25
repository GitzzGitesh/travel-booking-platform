import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink, RouterOutlet } from '@angular/router';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, RouterOutlet],
  selector: 'adm-root',
  styleUrl: './app.css',
  templateUrl: './app.html',
})
export class App {}
