import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  afterRenderEffect,
  inject,
  input,
  viewChild,
} from '@angular/core';
import {
  BarController,
  BarElement,
  CategoryScale,
  Chart,
  Legend,
  LinearScale,
  Tooltip,
} from 'chart.js';

import { formatMoney } from './format.pipes';

Chart.register(BarController, BarElement, CategoryScale, LinearScale, Tooltip, Legend);

export interface RevenueChartDay {
  readonly label: string;
  readonly sales: number;
  readonly refunds: number;
}

/** A token's value as the page has it now, so the chart follows the theme. */
function token(name: string): string {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

/**
 * Sales and refunds by day as bars (docs/DESIGN_SYSTEM.md section 4, "Gəlir
 * qrafiki"). Colours are read from the design tokens, never written here;
 * the same figures are always in a table next to it, so the chart is not the
 * only way to read them.
 */
@Component({
  selector: 'app-revenue-chart',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<div class="chart">
    <canvas #canvas [attr.aria-label]="label()" role="img"></canvas>
  </div>`,
  styles: `
    .chart {
      position: relative;
      height: 260px;
    }
  `,
})
export class RevenueChartComponent {
  readonly days = input.required<readonly RevenueChartDay[]>();
  readonly salesLabel = input.required<string>();
  readonly refundsLabel = input.required<string>();
  readonly label = input.required<string>();
  readonly language = input<string>('az');

  private readonly canvas = viewChild.required<ElementRef<HTMLCanvasElement>>('canvas');
  private chart: Chart | undefined;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.chart?.destroy());

    afterRenderEffect(() => {
      const days = this.days();
      const language = this.language();
      const money = (value: number | string): string => formatMoney(Number(value), language);

      this.chart?.destroy();

      // A browser without a canvas (a test, a headless check) has no chart:
      // the table next to it still carries the figures.
      if (!this.canvas().nativeElement.getContext?.('2d')) {
        return;
      }

      this.chart = new Chart(this.canvas().nativeElement, {
        type: 'bar',
        data: {
          labels: days.map((d) => d.label),
          datasets: [
            {
              label: this.salesLabel(),
              data: days.map((d) => d.sales),
              backgroundColor: token('--brand-500'),
            },
            {
              label: this.refundsLabel(),
              data: days.map((d) => d.refunds),
              backgroundColor: token('--danger-500'),
            },
          ],
        },
        options: {
          responsive: true,
          maintainAspectRatio: false,
          animation: false,
          scales: {
            x: { ticks: { color: token('--neutral-600') }, grid: { display: false } },
            y: {
              beginAtZero: true,
              ticks: { color: token('--neutral-600'), callback: (value) => money(value) },
              grid: { color: token('--neutral-200') },
            },
          },
          plugins: {
            legend: { labels: { color: token('--neutral-800') } },
            tooltip: {
              callbacks: { label: (item) => `${item.dataset.label}: ${money(item.parsed.y ?? 0)}` },
            },
          },
        },
      });
    });
  }
}
