import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import {
  type Facet,
  type Facets,
  type OfferFilters,
  activeFilterCount,
  noFilters,
} from './flight-filtering';

type Group = keyof OfferFilters;

/** Filter checkboxes built from the facets of the current results. Shown in the sidebar and the mobile sheet. */
@Component({
  selector: 'app-flight-filters',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './flight-filters.html',
  styleUrl: './flight-filters.css',
})
export class FlightFilters {
  readonly facets = input.required<Facets>();
  readonly value = input.required<OfferFilters>();
  /** Keeps ids unique when the panel is rendered twice (sidebar and sheet). */
  readonly idPrefix = input.required<string>();
  /** The sheet has its own dialog heading; the sidebar panel needs one. */
  readonly showHeading = input(true);

  readonly valueChange = output<OfferFilters>();

  protected readonly active = computed(() => activeFilterCount(this.value()));

  protected readonly groups = computed(() => {
    const facets = this.facets();
    return [
      { key: 'stops' as Group, legend: 'Stops', facets: facets.stops as Facet<unknown>[] },
      {
        key: 'times' as Group,
        legend: 'Departure time',
        note: 'Local time at the departure airport',
        facets: facets.times as Facet<unknown>[],
      },
      {
        key: 'carriers' as Group,
        legend: 'Airlines',
        note: 'By airline code',
        facets: facets.carriers as Facet<unknown>[],
      },
    ].filter((group) => group.facets.length > 0);
  });

  protected isChecked(group: Group, value: unknown): boolean {
    return (this.value()[group] as readonly unknown[]).includes(value);
  }

  protected toggle(group: Group, value: unknown, checked: boolean): void {
    const current = this.value()[group] as readonly unknown[];
    const next = checked ? [...current, value] : current.filter((v) => v !== value);
    this.valueChange.emit({ ...this.value(), [group]: next });
  }

  protected clear(): void {
    this.valueChange.emit(noFilters);
  }
}
