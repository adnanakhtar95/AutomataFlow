/** Workspace roles in increasing order of privilege (the Api's OrganizationRole). */
export const Roles = ['Viewer', 'Operator', 'Admin', 'Owner'] as const;

export type Role = (typeof Roles)[number];

/**
 * Whether <paramref name="role"/> is at least <paramref name="minimum"/>. Used ONLY to hide controls the Api would refuse;
 * the Api enforces every permission itself on every request.
 */
export function atLeast(role: string | null | undefined, minimum: Role): boolean {
  const have = Roles.indexOf(role as Role);
  return have >= 0 && have >= Roles.indexOf(minimum);
}
