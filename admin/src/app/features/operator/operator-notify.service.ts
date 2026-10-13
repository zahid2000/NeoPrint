import { DOCUMENT } from '@angular/common';
import { Injectable, inject, signal } from '@angular/core';

/** What the browser says about showing notifications; `unsupported` where it has none. */
export type NotificationAccess = 'default' | 'granted' | 'denied' | 'unsupported';

/**
 * A short sound and a browser notification for what an operator has to know
 * at once - a new task, a Critical alert, an incident (plan section 8B).
 *
 * Only after the operator has said yes: the browser is asked when they press
 * "Bildirişləri aç", never on its own, and a "no" is not asked about again.
 * Without permission the panel still shows everything; it just stays silent.
 */
@Injectable({ providedIn: 'root' })
export class OperatorNotifyService {
  private readonly view = inject(DOCUMENT).defaultView;

  private readonly state = signal<NotificationAccess>(this.read());

  /** Whether the operator has been asked, and what they said. */
  readonly access = this.state.asReadonly();

  private audio: AudioContext | null = null;

  /** Asks the browser. Called from a button press, which is what a browser requires. */
  async enable(): Promise<void> {
    const api = this.view?.Notification;

    if (!api) {
      this.state.set('unsupported');

      return;
    }

    try {
      this.state.set(await api.requestPermission());
    } catch {
      this.state.set(this.read());
    }

    // A button press is also what lets a page make a sound later.
    this.prepareSound();
  }

  /**
   * Says it: the sound, and a notification when the operator allowed them.
   * The same `tag` twice replaces the first rather than stacking up.
   */
  announce(title: string, body: string, tag: string): void {
    if (this.state() !== 'granted') {
      return;
    }

    this.beep();

    try {
      const api = this.view?.Notification;

      if (api) {
        new api(title, { body, tag });
      }
    } catch {
      // Some mobile browsers only show notifications through a service
      // worker; the sound has been played and the panel itself shows the rest.
    }
  }

  private read(): NotificationAccess {
    return this.view?.Notification?.permission ?? 'unsupported';
  }

  private prepareSound(): void {
    try {
      const Context = this.view?.AudioContext;

      this.audio ??= Context ? new Context() : null;
      void this.audio?.resume();
    } catch {
      this.audio = null;
    }
  }

  /** Two short tones, made here: no sound file to ship or to licence. */
  private beep(): void {
    this.prepareSound();

    const audio = this.audio;

    if (!audio) {
      return;
    }

    try {
      [0, 0.22].forEach((offset, index) => {
        const tone = audio.createOscillator();
        const volume = audio.createGain();
        const start = audio.currentTime + offset;

        tone.frequency.value = index === 0 ? 880 : 1175;
        volume.gain.setValueAtTime(0.0001, start);
        volume.gain.exponentialRampToValueAtTime(0.2, start + 0.02);
        volume.gain.exponentialRampToValueAtTime(0.0001, start + 0.18);

        tone.connect(volume).connect(audio.destination);
        tone.start(start);
        tone.stop(start + 0.2);
      });
    } catch {
      // A sound that cannot be made is not worth an error.
    }
  }
}
