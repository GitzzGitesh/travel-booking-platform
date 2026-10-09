import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import {
  Api,
  decideRoleChange,
  listRoleChanges,
  listRoleGrants,
  requestRoleChange,
} from '@travel-booking/admin-api-client';
import type {
  RoleChangeAction,
  RoleChangeResponse,
  RoleGrantResponse,
} from '@travel-booking/admin-api-client';
import { describeProblem } from '../shared/problems';
import { ReasonForm, reasonPattern } from '../shared/reason-form';
import { StaffSession } from '../staff-session';

/** The roles the server knows (Modules.Access StaffRoles); the server refuses any other name. */
export const staffRoles = ['Operations', 'Privacy', 'Administrator'] as const;

/** A workforce object id as the server accepts it: a lowercase GUID. */
const objectIdPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

const roleChangeProblems: Record<string, string> = {
  'self-approval-not-allowed':
    'Nobody requests a change to their own access or decides on their own request.',
  'revocation-withdrawal-only':
    'A revocation can only be withdrawn by the person who requested it.',
  'granted-by-configuration':
    "This role is granted by the environment's configuration: change it there.",
  'requester-no-longer-authorized':
    'The requester may no longer request changes: reject it and request again if needed.',
  'request-expired': 'This request waited too long: reject it and request again.',
  'already-decided': 'This request was already decided.',
  'nothing-to-change': 'The account already has this role (or does not have it to revoke).',
  'role-change-pending': 'A request for this account and role is already waiting for a decision.',
  'role-change-not-found': 'This request was not found.',
  'staff-account-required': 'A staff account is required.',
};

/**
 * Staff access (ADR 0022): who holds which role, and the maker-checker flow. One administrator requests a grant or a
 * revocation, a different one decides. The server enforces every rule; this page only offers what the permissions allow.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, ReasonForm],
  selector: 'adm-staff-access',
  templateUrl: './staff-access.html',
  styles: `
    .action {
      margin-block: 1.5rem;
      padding: 1rem;
      border: 1px solid #c8c8c8;
      border-radius: 4px;
    }
    .request-form {
      display: grid;
      gap: 0.5rem;
      max-inline-size: 36rem;
    }
    fieldset {
      border: 0;
      padding: 0;
      margin: 0;
    }
  `,
})
export class StaffAccess {
  private readonly api = inject(Api);
  protected readonly session = inject(StaffSession);

  protected readonly roles = staffRoles;
  protected readonly grants = signal<RoleGrantResponse[]>([]);
  protected readonly pending = signal<RoleChangeResponse[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly state = signal<'loading' | 'loaded' | 'error'>('loading');
  protected readonly busy = signal(false);
  protected readonly message = signal<{ text: string; error: boolean } | null>(null);

  protected readonly objectId = signal('');
  protected readonly role = signal<string>(staffRoles[0]);
  protected readonly action = signal<Exclude<RoleChangeAction, null>>('Grant');
  protected readonly reason = signal('');
  protected readonly requestErrors = signal<{ objectId: boolean; reason: boolean }>({
    objectId: false,
    reason: false,
  });

  constructor() {
    void this.load();
  }

  protected async submitRequest(event: Event): Promise<void> {
    event.preventDefault();
    // QA BUG-007: a new attempt starts without the previous answer, so a field problem is never shown beside a stale message.
    this.message.set(null);
    const objectId = this.objectId().trim();
    const reason = this.reason().trim();
    const errors = {
      objectId: !objectIdPattern.test(objectId),
      reason: !reasonPattern.test(reason),
    };
    this.requestErrors.set(errors);
    if (errors.objectId || errors.reason) {
      return;
    }
    await this.act(async () => {
      const request = await this.api.invoke(requestRoleChange, {
        body: { objectId, role: this.role(), action: this.action(), reason },
      });
      this.objectId.set('');
      this.reason.set('');
      return `Requested: ${request.action} ${request.role}. A different administrator must approve it.`;
    });
  }

  protected async decide(
    request: RoleChangeResponse,
    approve: boolean,
    reason: string,
  ): Promise<void> {
    await this.act(async () => {
      const decided = await this.api.invoke(decideRoleChange, {
        requestId: request.requestId,
        body: { approve, reason },
      });
      return `${decided.status}: ${decided.action} ${decided.role} for ${decided.objectId}.`;
    });
  }

  protected async loadMore(): Promise<void> {
    try {
      const page = await this.api.invoke(listRoleChanges, {
        status: 'Pending',
        cursor: this.nextCursor() ?? undefined,
      });
      this.pending.update((pending) => [...pending, ...page.requests]);
      this.nextCursor.set(page.nextCursor);
    } catch {
      this.state.set('error');
    }
  }

  private async act(action: () => Promise<string>): Promise<void> {
    if (this.busy()) {
      return;
    }
    this.busy.set(true);
    try {
      this.message.set({ text: await action(), error: false });
      await this.load();
    } catch (error) {
      this.message.set({ text: describeProblem(error, roleChangeProblems), error: true });
    } finally {
      this.busy.set(false);
    }
  }

  private async load(): Promise<void> {
    try {
      const [grants, page] = await Promise.all([
        this.api.invoke(listRoleGrants),
        this.api.invoke(listRoleChanges, { status: 'Pending' }),
      ]);
      this.grants.set(grants);
      this.pending.set(page.requests);
      this.nextCursor.set(page.nextCursor);
      this.state.set('loaded');
    } catch {
      this.state.set('error');
    }
  }
}
