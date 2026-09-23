import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

/**
 * The application root.
 *
 * Everything the panel shows lives in the routed pages: the shell with its
 * permission-driven menu, or the sign-in and password screens outside it.
 */
@Component({
  selector: 'app-root',
  imports: [RouterOutlet],
  template: '<router-outlet />',
})
export class App {}
