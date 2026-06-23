# TODO / future developments

## Hardening

- **Silent token refresh.** Google access tokens expire after ~1 hour. A refresh token is
  captured (`AccessType=offline`); wiring up silent refresh in the API service is the recommended
  next hardening step.

## API surface to grow into

- `accounts`
- `accounts.channelPartnerLinks`
- `products`
- `products.skus`

## Known placeholders

- The dashboard figures on the home page are placeholders until the reporting endpoints are added.

## Notes

- `GoogleChannel:AccountId` is required for every Channel API call and is validated at runtime.
