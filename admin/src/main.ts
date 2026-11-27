import { bootstrapApplication } from '@angular/platform-browser';
import { appConfig } from './app/app.config';
import { App } from './app/app';
import { startErrorTracking } from './app/core/error-tracking/error-tracking';

// Error tracking (T-478) starts beside the app, not before it: the panel does
// not wait for the API to say where errors go.
void startErrorTracking();

bootstrapApplication(App, appConfig)
  .catch((err) => console.error(err));
