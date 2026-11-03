# Leave policy configuration guide

For HR administrators. Routes are under `api/hrm/leave` and need `HRM_LEAVE_MANAGE` unless stated. The module and the `FEATURE_HRM_LEAVE` sub-feature must both be on. Full contract: [API-CONTRACT.md §6.4](API-CONTRACT.md).

## How it fits together

- A **leave type** is the kind of leave and its rules. A **policy** says how many days a type gives and how they are granted.
- The **ledger** is the record of every change. An employee's balance for a leave year is the sum of their ledger entries for that type and year. Entries are never edited: a correction is a new entry.

## Seeded leave types

Created on the organization's first HRM use. All can be edited.

| Code | Days | Paid | Carry-forward cap | Attachment above | Restricted to |
|---|---|---|---|---|---|
| ANNUAL | 14 | Yes | 14 | | |
| SICK | 10 | Yes | none | 2 days | |
| CASUAL | 10 | Yes | none | | |
| MATERNITY | 90 | Yes | none | 1 day | Female |
| PATERNITY | 30 | Yes | none | 1 day | Male |
| UNPAID | 0 | No | none | | |

These are defaults, not statements of legal entitlement. Check them against your contracts and applicable law.

## Leave type settings

`POST types`, `PUT types/{uuid}`, `PATCH types/{uuid}/active`

| Setting | Effect |
|---|---|
| `isPaid` | Unpaid leave spends no quota and reduces paid days in payroll. |
| `carryForwardCap` | Most days that move to the next year. **Empty means the type does not carry**: the whole balance lapses at year close. |
| `attachmentAfterDays` | A request for more days than this needs a document (`LEAVE_ATTACHMENT_REQUIRED`). |
| `genderRestriction` | `M`, `F` or `Other`. Other employees cannot request it, and bulk allocation skips them. An employee with no gender recorded is also refused. |
| `allowNegativeBalance` | Lets a request go beyond the balance. |

A type with pending requests cannot be deactivated (`LEAVE_TYPE_IN_USE`).

## Policies

`POST types/{uuid}/policies`, `PUT policies/{uuid}`

- `entitlement`: days per year.
- `accrualMethod`:
  - `Annual`: the whole entitlement at once.
  - `Monthly`: one twelfth for each month reached, from the join month for someone who joined that year.
  - `None`: nothing is granted.
- `effectiveFrom`, `effectiveTo`: adding a policy closes the open one for the same grade the day before the new one starts. A new policy must start after the latest one (`LEAVE_POLICY_OVERLAP`).
- `gradeUuid`: optional. An employee gets their grade's policy if it has one in force that year, otherwise the policy with no grade. The employee's grade comes from their latest employment record.

## Yearly routine

1. **Close the old year:** `POST carry-forward { fromYear, toYear }`. `toYear` must be `fromYear + 1`. The old year's balance goes to zero, and up to the cap moves to the new year. Running it again changes nothing. Decide pending requests from the old year first: one approved after the close takes its days from the closed year, leaving that year negative.
2. **Allocate the new year:** `POST allocate/bulk { leaveTypeUuid, year }`, once per type. It covers employees on the books in your HR scope. Running it again gives nobody a second annual allocation.
3. **Monthly types:** run `allocate/bulk` again each month.

Single employee: `POST allocate { employeeUuid, leaveTypeUuid, days, year, note? }`. A second allocation is refused (`LEAVE_ALREADY_ALLOCATED`). To change a quota, send `adjustment: true` with signed days.

## How requests count

- Only working days count: weekly offs of the employee's shift and holidays do not. A half day is 0.5 on a single date.
- Pending requests count against what is available, but only approval writes to the ledger. Cancelling an approved request gives the days back.
- A request cannot cross a year end (`LEAVE_SPANS_YEARS`): make one request per year.
- Requests go to the `LEAVE_REQUEST` workflow. The approver is the line manager, else the department head, else another holder of `HRM_LEAVE_APPROVE`. Only the assigned approver can decide, apart from a System or Org Admin; holding `HRM_LEAVE_APPROVE` alone is not enough. Nobody decides their own request.

## Limitations

- **No pro-rating for mid-year joiners.** `Annual` gives the full entitlement whatever the join date. To give less, allocate manually or send an adjustment. `Monthly` does follow the join month.
- **Accrual runs only when `allocate/bulk` is called.** There is no schedule. If nobody runs it, monthly balances stop growing.
- **Editing a shift does not recompute attendance.** Call `POST api/hrm/attendance/days/recompute { employeeUuid?, from, to }` (`HRM_ATTENDANCE_MANAGE`, up to 92 days). Assigning a shift does recompute, up to 92 days from its start. Leave requests already made keep their day count.
- **A finalized payroll does not follow later changes.** Cancelling leave or overriding an attendance day afterwards leaves that run as it was. Add a payroll adjustment.
- **Bulk allocation and carry-forward are limited to the caller's HR scope.** A caller with a partial scope covers only those employees.
- **Rejecting through the workflow inbox** does not fill the request's decision note. Rejecting through the leave route does.
