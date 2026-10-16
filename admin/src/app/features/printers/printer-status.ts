import { PrinterReading } from '../../core/api/step7.models';
import { StatusTone } from '../../shared/status-chip.component';

/** A printer's status as every page shows it (docs/DESIGN_SYSTEM.md, "Printer statusu"). */
export interface PrinterStatus {
  /** `stale`, `unreachable`, `stopped`, `attention`, `printing`, `ready` or `unknown`. */
  readonly key: string;
  readonly tone: StatusTone;
  readonly icon: string;
}

/** The printer's own reasons that say something; IPP's "none" says nothing. */
export function printerReasons(reading: PrinterReading): string[] {
  return reading.sample.stateReasons.filter((reason) => reason && reason !== 'none');
}

/** The first that applies wins: an old reading says nothing about now. */
export function printerStatus(reading: PrinterReading): PrinterStatus {
  const sample = reading.sample;

  if (reading.stale) {
    return { key: 'stale', tone: 'neutral', icon: 'timer_off' };
  }

  if (sample.online === false) {
    return { key: 'unreachable', tone: 'danger', icon: 'link_off' };
  }

  if (sample.state === 'stopped') {
    return { key: 'stopped', tone: 'danger', icon: 'error' };
  }

  if (printerReasons(reading).length > 0 || sample.lastError) {
    return { key: 'attention', tone: 'warning', icon: 'warning' };
  }

  if (sample.state === 'printing') {
    return { key: 'printing', tone: 'info', icon: 'print' };
  }

  if (sample.state === 'idle') {
    return { key: 'ready', tone: 'success', icon: 'check_circle' };
  }

  return { key: 'unknown', tone: 'neutral', icon: 'radio_button_unchecked' };
}

/** The consumables that run out - toner and ink - lowest first. */
export function lowestToner(reading: PrinterReading): { name: string; percent: number } | null {
  const levels = reading.sample.supplies
    .filter((supply) => (supply.type === 'toner' || supply.type === 'ink') && supply.levelPercent != null)
    .map((supply) => ({ name: supply.name, percent: supply.levelPercent as number }))
    .sort((a, b) => a.percent - b.percent);

  return levels[0] ?? null;
}
