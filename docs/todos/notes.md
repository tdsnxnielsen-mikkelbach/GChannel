> Part of the [GChannel TODO index](../todo.md).

## Notes

- `GoogleChannel:AccountId` is required for every Channel API call and is validated at runtime.
- Most mutating calls (create/transfer/change) return **long-running operations**; the **Operations**
  page (§7) polls `operations.get` and reflects pending/done/failed state, and entitlement actions
  surface the returned operation name for tracking.

