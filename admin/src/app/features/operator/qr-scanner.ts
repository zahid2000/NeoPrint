/**
 * Reading a QR with the phone's camera, by what the browser itself brings: the
 * camera through `getUserMedia`, the code through `BarcodeDetector` (Chrome on
 * Android has both). No library is loaded for it; where either is missing the
 * panel offers no scanning and the code is typed in instead.
 */

/** The part of the Shape Detection API this file uses; not in TypeScript's DOM types yet. */
interface Detector {
  detect(source: HTMLVideoElement): Promise<readonly { readonly rawValue: string }[]>;
}

type DetectorConstructor = new (options: { formats: string[] }) => Detector;

/** What a kiosk's activation screen shows: `neoprint://activate/{deviceId}?code={pairingCode}`. */
export const activationQr = (text: string): boolean => text.toLowerCase().startsWith('neoprint://activate/');

const detectorOf = (): DetectorConstructor | undefined =>
  (globalThis as { BarcodeDetector?: DetectorConstructor }).BarcodeDetector;

/** How often the picture is looked at. */
const everyMs = 250;

export class QrScanner {
  private stream: MediaStream | null = null;
  private timer: ReturnType<typeof setInterval> | null = null;
  private settle: ((text: string | null) => void) | null = null;

  static supported(): boolean {
    return detectorOf() !== undefined && typeof navigator !== 'undefined' && !!navigator.mediaDevices?.getUserMedia;
  }

  /**
   * Shows the back camera in the video and waits for a code that is wanted.
   *
   * @returns The code's text; null when {@link stop} ended it first.
   * @throws When there is no camera or the user refused it.
   */
  async read(video: HTMLVideoElement, wanted: (text: string) => boolean): Promise<string | null> {
    this.stop();

    const Detector = detectorOf();

    if (!Detector) {
      throw new Error('This browser cannot read codes.');
    }

    const detector = new Detector({ formats: ['qr_code'] });

    this.stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' }, audio: false });
    video.srcObject = this.stream;
    await video.play();

    return new Promise<string | null>((resolve) => {
      this.settle = resolve;

      let looking = false;

      this.timer = setInterval(() => {
        if (looking) {
          return;
        }

        looking = true;

        void detector
          .detect(video)
          .then((codes) => {
            const found = codes.find((code) => wanted(code.rawValue));

            if (found) {
              this.finish(found.rawValue);
            }
          })
          // A frame that could not be read is only a frame: the next one is tried.
          .catch(() => undefined)
          .finally(() => (looking = false));
      }, everyMs);
    });
  }

  /** Ends the reading and lets go of the camera. */
  stop(): void {
    this.finish(null);
  }

  private finish(text: string | null): void {
    if (this.timer !== null) {
      clearInterval(this.timer);
      this.timer = null;
    }

    this.stream?.getTracks().forEach((track) => track.stop());
    this.stream = null;

    const settle = this.settle;

    this.settle = null;
    settle?.(text);
  }
}
