import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  afterRenderEffect,
  computed,
  inject,
  input,
  viewChild,
} from '@angular/core';
import {
  CategoryScale,
  Chart,
  Filler,
  LineController,
  LineElement,
  LinearScale,
  PointElement,
  Tooltip,
} from 'chart.js';

Chart.register(LineController, LineElement, PointElement, CategoryScale, LinearScale, Filler, Tooltip);

/** One line of a chart; a gap is null. */
export interface LineSeries {
  readonly label: string;
  readonly values: readonly (number | null)[];
}

/** A token's value as the page has it now, so the chart follows the theme. */
function token(name: string): string {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

/** The tokens the lines are drawn in, in order: the first is the brand. */
const lineTokens = ['--brand-500', '--info-500', '--success-500', '--neutral-600', '--danger-500'];

/**
 * A line chart over time (docs/DESIGN_SYSTEM.md, "Telemetriya qrafiki"):
 * kiosk telemetry and a printer's counters. Colours come from the design
 * tokens; a limit, when there is one, is a dashed line in the warning
 * colour. The figures are never only here: the caller writes them out next
 * to the chart.
 */
@Component({
  selector: 'app-line-chart',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<div class="chart">
    <canvas #canvas [attr.aria-label]="label()" role="img"></canvas>
  </div>`,
  styles: `
    .chart {
      position: relative;
      height: var(--chart-height);
    }
  `,
})
export class LineChartComponent {
  readonly labels = input.required<readonly string[]>();
  readonly series = input.required<readonly LineSeries[]>();

  /** What the chart shows, for a screen reader. */
  readonly label = input.required<string>();

  /** The limit the values should stay on the right side of, drawn dashed. */
  readonly threshold = input<number | null>(null);
  readonly thresholdLabel = input<string>('');

  /** How a value is written on the axis and in the tooltip. */
  readonly format = input<(value: number) => string>((value) => String(value));

  /** Starts the axis at nought: a level or a percentage, not a counter. */
  readonly fromZero = input(true);

  private readonly canvas = viewChild.required<ElementRef<HTMLCanvasElement>>('canvas');
  private chart: Chart | undefined;

  /** Whether there is anything to draw at all. */
  readonly empty = computed(() => this.series().every((line) => line.values.every((value) => value === null)));

  constructor() {
    inject(DestroyRef).onDestroy(() => this.chart?.destroy());

    afterRenderEffect(() => {
      const labels = this.labels();
      const series = this.series();
      const threshold = this.threshold();
      const format = this.format();

      this.chart?.destroy();

      // A browser without a canvas (a test, a headless check) has no chart:
      // the figures written next to it still say what it would.
      if (!this.canvas().nativeElement.getContext?.('2d')) {
        return;
      }

      const lines = series.map((line, index) => ({
        label: line.label,
        data: [...line.values],
        borderColor: token(lineTokens[index % lineTokens.length]),
        backgroundColor: token(lineTokens[index % lineTokens.length]),
        borderWidth: 2,
        pointRadius: 0,
        spanGaps: false,
        tension: 0.2,
      }));

      if (threshold !== null) {
        lines.push({
          label: this.thresholdLabel(),
          data: labels.map(() => threshold),
          borderColor: token('--warning-500'),
          backgroundColor: token('--warning-500'),
          borderWidth: 1,
          pointRadius: 0,
          spanGaps: true,
          tension: 0,
          borderDash: [6, 4],
        } as (typeof lines)[number]);
      }

      this.chart = new Chart(this.canvas().nativeElement, {
        type: 'line',
        data: { labels: [...labels], datasets: lines },
        options: {
          responsive: true,
          maintainAspectRatio: false,
          animation: false,
          interaction: { mode: 'index', intersect: false },
          scales: {
            x: { ticks: { color: token('--neutral-600'), maxTicksLimit: 6, maxRotation: 0 }, grid: { display: false } },
            y: {
              beginAtZero: this.fromZero(),
              ticks: { color: token('--neutral-600'), callback: (value) => format(Number(value)) },
              grid: { color: token('--neutral-200') },
            },
          },
          plugins: {
            legend: { display: false },
            tooltip: {
              callbacks: { label: (item) => `${item.dataset.label}: ${format(item.parsed.y ?? 0)}` },
            },
          },
        },
      });
    });
  }
}
