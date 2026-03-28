# RFC-0003: Optional Webhook/HTTP Integration

ID: `RFC-0003`
Status: `Accepted`
Target Milestone: `M4`
Author: `Mogy`
Date: `2026-03-28`

## Summary

Add an optional outbound webhook pipeline that pushes high-signal anti-cheat events to external systems with rate limiting, retries, and exponential backoff.

## Motivation

Server operators need lightweight integration with moderation dashboards, SIEM pipelines, and alerting bots without coupling those systems directly to in-process hooks.

## Goals

- Add optional HTTP push for suspicion and penalty events.
- Provide operator-controlled retry and backoff behavior.
- Keep anti-cheat runtime fail-safe when endpoint is unavailable.

## Non-Goals

- Guaranteed delivery across restarts.
- Bidirectional control channel from webhook receiver.
- Additional gameplay actions based on webhook response.

## Design

- Events are enqueued as envelopes (`eventType` + payload).
- A bounded in-memory queue drains in FIFO order.
- Requests are throttled by `RateLimitPerSecond`.
- Failed requests are retried with exponential backoff up to `MaxRetries`.
- On permanent failure, events are dropped with debug logging.
- Local anti-cheat logic never blocks on webhook success.

## Config

`Webhook` object:
- `Enabled` (`bool`, default `false`)
- `Endpoint` (`string`)
- `AuthToken` (`string`, optional)
- `AuthHeader` (`string`, default `Authorization`)
- `MaxRetries` (`int`, default `3`)
- `BaseBackoffSeconds` (`float`, default `1.5`)
- `MaxBackoffSeconds` (`float`, default `20.0`)
- `RateLimitPerSecond` (`int`, default `2`)
- `QueueMaxSize` (`int`, default `500`)
- `EmitSuspicionEvents` (`bool`, default `true`)
- `EmitPenaltyEvents` (`bool`, default `true`)

## API and Compatibility

- Existing in-process hooks (`OnMogyAcSuspicion`, `OnMogyAcPenaltyApplied`) remain unchanged.
- Public API version remains `1.0.0`; webhook is an additive integration path.
- Existing configs auto-extend with safe defaults.

## Risks

- Endpoint outages can create queue pressure.
- Misconfigured endpoint/auth can silently drop value.

Mitigations:
- Queue cap with oldest-drop behavior.
- Retry with capped backoff.
- Debug logs for failures/retries/drops.

## Acceptance Criteria

- Suspicion and penalty events can be pushed to an external endpoint.
- Request rate stays within configured limits.
- Failures retry with backoff, then drop safely after max retries.
- Core damage/suspicion flow remains unaffected during webhook errors.
